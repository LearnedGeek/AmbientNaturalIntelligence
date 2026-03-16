using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// ElevenLabs streaming TTS via WebSocket. Accepts text chunks (from LLM token buffer)
/// and streams PCM 16kHz audio back for direct playback on the MAUI client.
/// Reuses emotional voice mapping from <see cref="ElevenLabsTextToSpeechService"/>.
/// Each instance manages one TTS session — create per voice call via IServiceProvider.
/// </summary>
public class ElevenLabsStreamingTTSService : IStreamingTextToSpeechService
{
    private readonly VoiceOptions _options;
    private readonly ILogger<ElevenLabsStreamingTTSService> _log;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _isFirstChunk = true;
    private EmotionalState? _emotionalState;

    public event Action<ReadOnlyMemory<byte>>? AudioChunkReceived;

    public ElevenLabsStreamingTTSService(
        IOptions<VoiceOptions> options,
        ILogger<ElevenLabsStreamingTTSService> log)
    {
        _options = options.Value;
        _log     = log;
    }

    public async Task StartAsync(EmotionalState? emotionalState = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ElevenLabsApiKey))
            throw new InvalidOperationException("ElevenLabs API key is not configured.");
        if (string.IsNullOrWhiteSpace(_options.ElevenLabsVoiceId))
            throw new InvalidOperationException("ElevenLabs VoiceId is not configured.");

        _emotionalState = emotionalState;
        _isFirstChunk = true;

        _ws = new ClientWebSocket();

        var uri = new Uri(
            $"wss://api.elevenlabs.io/v1/text-to-speech/{_options.ElevenLabsVoiceId}/stream-input" +
            $"?model_id={_options.ElevenLabsModelId}&output_format=pcm_16000");

        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);

        // Send BOS (beginning of stream) with voice settings and API key
        var voiceSettings = ElevenLabsTextToSpeechService.MapEmotionalStateToVoiceSettings(_emotionalState);
        var bos = new
        {
            text = " ",  // BOS requires non-empty text
            voice_settings = new
            {
                stability = voiceSettings.Stability,
                similarity_boost = voiceSettings.SimilarityBoost,
                style = voiceSettings.Style,
            },
            xi_api_key = _options.ElevenLabsApiKey,
            try_trigger_generation = false,
        };

        await SendJsonAsync(bos, ct).ConfigureAwait(false);
        _log.LogInformation("ElevenLabs streaming TTS connected: voice={VoiceId}, stability={Stability:F2}",
            _options.ElevenLabsVoiceId, voiceSettings.Stability);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);
    }

    public async Task SendTextAsync(string textChunk, CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open || string.IsNullOrEmpty(textChunk))
            return;

        // Prepend emotional tag to the first real text chunk only
        if (_isFirstChunk)
        {
            textChunk = ElevenLabsTextToSpeechService.PrependEmotionalTag(textChunk, _emotionalState);
            _isFirstChunk = false;
        }

        var msg = new { text = textChunk, try_trigger_generation = true };
        await SendJsonAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_ws?.State != WebSocketState.Open) return;

        // Empty text with no trigger signals end of input — flushes remaining audio
        var msg = new { text = "" };
        await SendJsonAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException) { /* may already be closing */ }
        }

        _receiveCts?.Cancel();
        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _log.LogInformation("ElevenLabs streaming TTS stopped");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // ElevenLabs streams a mix of binary audio and JSON status messages
        var buffer = new byte[8192];

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // JSON status message — log but don't forward
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessStatusMessage(json);
                    continue;
                }

                // Binary = PCM audio chunk — forward to client
                if (result.Count > 0)
                {
                    var audioChunk = new byte[result.Count];
                    Buffer.BlockCopy(buffer, 0, audioChunk, 0, result.Count);
                    AudioChunkReceived?.Invoke(audioChunk);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogWarning(ex, "ElevenLabs WebSocket error in receive loop");
        }
    }

    private void ProcessStatusMessage(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<ElevenLabsStatusMessage>(json);
            if (msg?.Audio is not null)
            {
                // Some responses include base64-encoded audio in JSON
                var audioBytes = Convert.FromBase64String(msg.Audio);
                if (audioBytes.Length > 0)
                    AudioChunkReceived?.Invoke(audioBytes);
            }
        }
        catch (JsonException)
        {
            _log.LogDebug("ElevenLabs: non-parseable status message");
        }
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _ws?.Dispose();
        _receiveCts?.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private record ElevenLabsStatusMessage
    {
        [JsonPropertyName("audio")]        public string? Audio        { get; init; }
        [JsonPropertyName("isFinal")]      public bool?   IsFinal      { get; init; }
        [JsonPropertyName("normalizedAlignment")] public object? Alignment { get; init; }
    }
}
