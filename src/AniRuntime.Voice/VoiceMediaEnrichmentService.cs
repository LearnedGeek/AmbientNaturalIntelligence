using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Enriches outgoing messages with a voice audio attachment via ElevenLabs TTS.
/// The audio is cached in memory and served via /media/{key} for Twilio to fetch.
///
/// Not every message gets a voice attachment — a probability gate controls frequency
/// to keep voice messages feeling special rather than routine.
/// </summary>
public class VoiceMediaEnrichmentService : IMediaEnrichmentService
{
    private readonly ITextToSpeechService _tts;
    private readonly IMemoryService _memory;
    private readonly MediaCacheService _cache;
    private readonly VoiceOptions _options;
    private readonly ILogger<VoiceMediaEnrichmentService> _logger;

    // Probability of attaching a voice message (0.0–1.0).
    // Start low — voice should feel like a surprise, not the default.
    private const float VoiceAttachmentProbability = 0.15f;

    private static readonly Random Rng = new();

    public VoiceMediaEnrichmentService(
        ITextToSpeechService tts,
        IMemoryService memory,
        MediaCacheService cache,
        IOptions<VoiceOptions> options,
        ILogger<VoiceMediaEnrichmentService> logger)
    {
        _tts     = tts;
        _memory  = memory;
        _cache   = cache;
        _options = options.Value;
        _logger  = logger;
    }

    public async Task<List<Uri>> EnrichAsync(OutreachDecision decision, CancellationToken ct = default)
    {
        var urls = new List<Uri>();

        if (!_options.Enabled || string.IsNullOrWhiteSpace(decision.Message))
            return urls;

        // Probability gate — not every message should be a voice note
        if (Rng.NextDouble() > VoiceAttachmentProbability)
        {
            _logger.LogDebug("Voice enrichment: skipped (probability gate)");
            return urls;
        }

        try
        {
            var emotionalState = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
            var audioStream = await _tts.SynthesizeAsync(decision.Message, emotionalState, ct)
                .ConfigureAwait(false);

            using var ms = new MemoryStream();
            await audioStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var key = _cache.Store(ms.ToArray(), "audio/mpeg");

            // The URL must be publicly accessible — Twilio fetches it.
            // Uses the ngrok base URL which is the same host as the webhooks.
            var baseUrl = _options.PublicBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _logger.LogWarning("Voice enrichment: PublicBaseUrl not configured — cannot attach audio");
                return urls;
            }

            var mediaUrl = new Uri($"{baseUrl.TrimEnd('/')}/media/{key}");
            urls.Add(mediaUrl);

            _logger.LogInformation("Voice enrichment: attached audio ({Bytes} bytes) at {Url}",
                ms.Length, mediaUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice enrichment failed — sending text only");
        }

        return urls;
    }
}
