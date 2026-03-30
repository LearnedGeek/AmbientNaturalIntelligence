using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// ElevenLabs v3 streaming TTS via HTTP POST per sentence.
/// Replaces the WebSocket service (v2 only) with HTTP streaming
/// that supports v3 model and audio tags.
///
/// Architecture: TokenBuffer accumulates tokens → complete sentence →
/// VoiceTagEnricher adds [tag] → HTTP POST to /stream → read chunked
/// PCM response → fire AudioChunkReceived events.
///
/// Each sentence is an independent HTTP request. No persistent connection,
/// no BOS/EOS framing, no reconnection logic, no idle timeout management.
/// Simpler and v3-compatible.
/// </summary>
public class ElevenLabsV3StreamingService : IStreamingTextToSpeechService
{
    private readonly VoiceOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ElevenLabsV3StreamingService> _log;
    private EmotionalState? _emotionalState;
    private readonly List<string> _pendingText = new();

    public event Action<ReadOnlyMemory<byte>>? AudioChunkReceived;

    public ElevenLabsV3StreamingService(
        IOptions<VoiceOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<ElevenLabsV3StreamingService> log)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _log = log;
    }

    public Task StartAsync(EmotionalState? emotionalState = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ElevenLabsApiKey))
            throw new InvalidOperationException("ElevenLabs API key is not configured.");
        if (string.IsNullOrWhiteSpace(_options.ElevenLabsVoiceId))
            throw new InvalidOperationException("ElevenLabs VoiceId is not configured.");

        _emotionalState = emotionalState;
        _pendingText.Clear();

        _log.LogInformation("ElevenLabs v3 TTS initialized: voice={VoiceId}, model=eleven_v3",
            _options.ElevenLabsVoiceId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Queue a sentence for synthesis. Each complete sentence triggers an
    /// HTTP POST to ElevenLabs and streams back PCM audio chunks.
    /// </summary>
    public async Task SendTextAsync(string textChunk, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(textChunk)) return;

        // Enrich with v3 audio tags based on content and emotional state
        var enriched = VoiceTagEnricher.Enrich(textChunk, _emotionalState);

        _log.LogDebug("ElevenLabs v3: synthesizing \"{Text}\"",
            enriched.Length > 80 ? enriched[..80] + "..." : enriched);

        await SynthesizeAndStreamAsync(enriched, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Flush any remaining text. With HTTP-per-sentence, this is a no-op
    /// since each SendTextAsync already triggers synthesis.
    /// </summary>
    public Task FlushAsync(CancellationToken ct = default)
    {
        _log.LogDebug("ElevenLabs v3: flush (no-op for HTTP streaming)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _pendingText.Clear();
        _log.LogInformation("ElevenLabs v3 TTS stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// POST a sentence to ElevenLabs /stream endpoint and read chunked PCM response.
    /// Each chunk fires AudioChunkReceived for real-time playback on the client.
    /// </summary>
    private async Task SynthesizeAndStreamAsync(string text, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("elevenlabs-v3");

        var voiceSettings = ElevenLabsTextToSpeechService.MapEmotionalStateToVoiceSettings(_emotionalState);

        var requestBody = new
        {
            text,
            model_id = "eleven_v3",
            output_format = "pcm_16000",
            voice_settings = new
            {
                stability = voiceSettings.Stability,
                similarity_boost = voiceSettings.SimilarityBoost,
                style = voiceSettings.Style,
            },
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{_options.ElevenLabsVoiceId}/stream";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = content;
        request.Headers.Add("xi-api-key", _options.ElevenLabsApiKey);

        try
        {
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning("ElevenLabs v3: HTTP {Status} — {Error}",
                    response.StatusCode, errorBody.Length > 200 ? errorBody[..200] : errorBody);
                return;
            }

            // Read the chunked response stream — each chunk is raw PCM audio
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[8192]; // 8KB chunks ~0.25s of 16kHz PCM
            int bytesRead;
            int totalBytes = 0;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)
                .ConfigureAwait(false)) > 0)
            {
                totalBytes += bytesRead;
                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                AudioChunkReceived?.Invoke(chunk);
            }

            _log.LogDebug("ElevenLabs v3: streamed {Bytes} bytes of audio", totalBytes);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "ElevenLabs v3: HTTP request failed");
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("ElevenLabs v3: synthesis cancelled");
        }
    }

    public ValueTask DisposeAsync()
    {
        _pendingText.Clear();
        return ValueTask.CompletedTask;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
