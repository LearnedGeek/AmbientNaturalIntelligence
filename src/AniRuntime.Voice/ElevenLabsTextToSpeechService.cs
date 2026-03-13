using System.Net.Http.Json;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// ElevenLabs TTS implementation. Converts text to speech with emotional state
/// mapped to voice settings (stability, similarity_boost, style).
///
/// Free tier: ~10,000 characters/month (~100 voice messages at typical length).
/// </summary>
public class ElevenLabsTextToSpeechService : ITextToSpeechService
{
    private readonly HttpClient _http;
    private readonly VoiceOptions _options;
    private readonly ILogger<ElevenLabsTextToSpeechService> _logger;

    private const string BaseUrl = "https://api.elevenlabs.io/v1";

    public ElevenLabsTextToSpeechService(
        HttpClient http,
        IOptions<VoiceOptions> options,
        ILogger<ElevenLabsTextToSpeechService> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;

        _http.DefaultRequestHeaders.Add("xi-api-key", _options.ElevenLabsApiKey);
    }

    public async Task<Stream> SynthesizeAsync(string text, EmotionalState? emotionalState = null, CancellationToken ct = default)
    {
        var voiceId = _options.ElevenLabsVoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new InvalidOperationException("ElevenLabs VoiceId is not configured.");

        var voiceSettings = MapEmotionalStateToVoiceSettings(emotionalState);

        var payload = new
        {
            text,
            model_id = _options.ElevenLabsModelId,
            voice_settings = voiceSettings,
        };

        var url = $"{BaseUrl}/text-to-speech/{voiceId}";
        _logger.LogDebug("ElevenLabs TTS: {Chars} chars, stability={Stability:F2}, similarity={Similarity:F2}",
            text.Length, voiceSettings.Stability, voiceSettings.SimilarityBoost);

        var response = await _http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var audioStream = new MemoryStream();
        await response.Content.CopyToAsync(audioStream, ct).ConfigureAwait(false);
        audioStream.Position = 0;

        _logger.LogInformation("ElevenLabs TTS: synthesized {Chars} chars → {Bytes} bytes audio",
            text.Length, audioStream.Length);

        return audioStream;
    }

    /// <summary>
    /// Map Ani's emotional state to ElevenLabs voice parameters.
    /// Higher warmth → softer/warmer delivery (lower stability, higher similarity).
    /// Lower energy → slower, quieter delivery.
    /// Higher playfulness → more expressive range (lower stability).
    /// </summary>
    internal static VoiceSettings MapEmotionalStateToVoiceSettings(EmotionalState? state)
    {
        if (state is null)
            return new VoiceSettings { Stability = 0.5f, SimilarityBoost = 0.75f, Style = 0.0f };

        // Stability: lower = more expressive. High playfulness or warmth → more expression.
        var expressiveness = Math.Max(state.Warmth, state.Playfulness);
        var stability = Math.Clamp(0.7f - (expressiveness - 0.5f) * 0.4f, 0.25f, 0.85f);

        // Similarity boost: higher = closer to original voice. Keep fairly high.
        var similarityBoost = Math.Clamp(0.65f + state.Warmth * 0.2f, 0.6f, 0.9f);

        // Style: ElevenLabs style exaggeration. Only use when emotion is notably different.
        var emotionalIntensity = Math.Abs(state.Warmth - 0.6f) + Math.Abs(state.Energy - 0.5f)
                               + Math.Abs(state.Playfulness - 0.5f);
        var style = Math.Clamp(emotionalIntensity * 0.3f, 0f, 0.5f);

        return new VoiceSettings
        {
            Stability       = stability,
            SimilarityBoost = similarityBoost,
            Style           = style,
        };
    }

    internal class VoiceSettings
    {
        public float Stability       { get; set; }

        public float SimilarityBoost { get; set; }

        public float Style           { get; set; }
    }
}
