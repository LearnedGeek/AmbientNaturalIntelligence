using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Deepgram Nova-3 streaming STT via WebSocket. Accepts PCM 16kHz 16-bit mono audio
/// directly from the MAUI client (zero transcoding). Fires events on final and interim transcripts.
/// Each instance manages one STT session — create per voice call via IServiceProvider.
///
/// Turn detection is delegated to <see cref="DebouncedUtterance"/> which accumulates
/// is_final segments and fires the complete utterance after a silence period.
/// </summary>
public class DeepgramStreamingSTTService : IStreamingSpeechToTextService
{
    private readonly VoiceOptions _options;
    private readonly ILogger<DeepgramStreamingSTTService> _log;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;

    private const int DebounceMs = 1500;

    /// <summary>
    /// Debounce handler for accumulating is_final segments into complete utterances.
    /// Exposed so the orchestrator can call Clear() to discard echo artifacts.
    /// </summary>
    public DebouncedUtterance Debounce { get; }

    public event Action<string>? TranscriptReceived;
    public event Action<string>? PartialTranscriptReceived;

    public DeepgramStreamingSTTService(
        IOptions<VoiceOptions> options,
        ILogger<DeepgramStreamingSTTService> log)
    {
        _options = options.Value;
        _log     = log;
        Debounce = new DebouncedUtterance(DebounceMs, log);
        Debounce.UtteranceReady += utterance => TranscriptReceived?.Invoke(utterance);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.DeepgramApiKey))
            throw new InvalidOperationException("Deepgram API key is not configured.");

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Token {_options.DeepgramApiKey}");

        // utterance_end_ms keeps the connection alive between utterances in multi-turn
        // conversation. Without it, Deepgram closes the session after the first speech_final,
        // producing a Metadata response and silently dropping all subsequent audio.
        var uri = new Uri(
            $"wss://api.deepgram.com/v1/listen" +
            $"?encoding=linear16&sample_rate=16000&channels=1" +
            $"&punctuate=true&interim_results=true" +
            $"&endpointing={_options.DeepgramEndpointingMs}" +
            $"&utterance_end_ms=1500" +
            $"&model={_options.DeepgramModel}");

        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        _log.LogInformation("Deepgram STT connected: model={Model}, endpointing={Ms}ms",
            _options.DeepgramModel, _options.DeepgramEndpointingMs);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16kHz, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        await _ws.SendAsync(pcm16kHz, WebSocketMessageType.Binary, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Debounce.Flush();

        if (_ws?.State == WebSocketState.Open)
        {
            await _ws.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary,
                endOfMessage: true, ct).ConfigureAwait(false);

            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException) { /* connection may already be closing */ }
        }

        _receiveCts?.Cancel();
        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _log.LogInformation("Deepgram STT stopped");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _log.LogDebug("Deepgram response: {Json}", json.Length > 200 ? json[..200] + "..." : json);
                ProcessTranscriptMessage(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogWarning(ex, "Deepgram WebSocket error in receive loop");
        }
    }

    /// <summary>
    /// Buffer for is_final segments that haven't reached speech_final yet.
    /// Deepgram sends is_final when a segment's transcription stabilizes,
    /// but speech_final only when the speaker actually stops talking.
    /// Without this buffer, a breath pause mid-sentence triggers the debounce
    /// and fires a half-utterance ("Are you really eating cold chicken soup?")
    /// before the speaker finishes ("That's new, but I guess the cornflakes...").
    /// </summary>
    private readonly List<string> _pendingSegments = new();

    private void ProcessTranscriptMessage(string json)
    {
        try
        {
            // Check message type before deserializing — UtteranceEnd and Metadata
            // have different schemas than Results and will throw JsonException
            // if deserialized as DeepgramResponse (channel is an array, not an object).
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                var msgType = typeProp.GetString();
                if (msgType is "Metadata" or "UtteranceEnd")
                    return; // not transcript data — skip
            }

            var msg = JsonSerializer.Deserialize<DeepgramResponse>(json);
            var transcript = msg?.Channel?.Alternatives?.FirstOrDefault()?.Transcript;

            if (msg?.IsFinal == true && !string.IsNullOrWhiteSpace(transcript))
            {
                _pendingSegments.Add(transcript);

                if (msg.SpeechFinal)
                {
                    // Speaker stopped — flush all accumulated segments as one utterance
                    var fullUtterance = string.Join(" ", _pendingSegments);
                    _pendingSegments.Clear();
                    Debounce.AddSegment(fullUtterance);
                }
                else
                {
                    // speech_final not yet received — start a safety timer.
                    // Deepgram doesn't always send speech_final (observed in production).
                    // If we have pending segments and no speech_final arrives within 3s,
                    // flush the buffer anyway to prevent the conversation from hanging.
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000).ConfigureAwait(false);
                        if (_pendingSegments.Count > 0)
                        {
                            var fullUtterance = string.Join(" ", _pendingSegments);
                            _pendingSegments.Clear();
                            _log.LogDebug("Deepgram: speech_final timeout — flushing {Count} pending segments",
                                fullUtterance.Split(' ').Length);
                            Debounce.AddSegment(fullUtterance);
                        }
                    });
                }
            }
            else if (msg?.IsFinal != true && !string.IsNullOrWhiteSpace(transcript))
            {
                PartialTranscriptReceived?.Invoke(transcript);
            }
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "Deepgram: failed to parse transcript message");
        }
    }

    public void ClearPendingSegments()
    {
        _pendingSegments.Clear();
        Debounce.Clear();
    }

    public void SendKeepAlive()
    {
        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                var keepAlive = Encoding.UTF8.GetBytes("{\"type\":\"KeepAlive\"}");
                _ = _ws.SendAsync(keepAlive, WebSocketMessageType.Text, true, CancellationToken.None);
                _log.LogDebug("Deepgram: keep-alive sent");
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Deepgram: keep-alive failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Debounce.Dispose();
        await StopAsync().ConfigureAwait(false);
        _ws?.Dispose();
        _receiveCts?.Dispose();
    }

    // ── Deepgram response shapes ──────────────────────────────────────────

    private record DeepgramResponse
    {
        [JsonPropertyName("channel")]      public DeepgramChannel? Channel     { get; init; }
        [JsonPropertyName("is_final")]     public bool             IsFinal     { get; init; }
        [JsonPropertyName("speech_final")] public bool             SpeechFinal { get; init; }
    }

    private record DeepgramChannel
    {
        [JsonPropertyName("alternatives")] public List<DeepgramAlternative>? Alternatives { get; init; }
    }

    private record DeepgramAlternative
    {
        [JsonPropertyName("transcript")]  public string? Transcript  { get; init; }
        [JsonPropertyName("confidence")]  public float   Confidence  { get; init; }
    }
}
