using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Enriches outgoing messages with an image from the curated library.
/// The image is cached in memory and served via /media/{key} for Twilio to fetch.
/// Uses the same MediaCacheService as voice audio — shared infrastructure.
/// </summary>
public class ImageMediaEnrichmentService : IMediaEnrichmentService
{
    private readonly IImageSelectionService _imageSelection;
    private readonly IMemoryService _memory;
    private readonly MediaCacheService _cache;
    private readonly VoiceOptions _voiceOptions;
    private readonly ImageOptions _imageOptions;
    private readonly ILogger<ImageMediaEnrichmentService> _log;

    public ImageMediaEnrichmentService(
        IImageSelectionService imageSelection,
        IMemoryService memory,
        MediaCacheService cache,
        IOptions<VoiceOptions> voiceOptions,
        IOptions<ImageOptions> imageOptions,
        ILogger<ImageMediaEnrichmentService> log)
    {
        _imageSelection = imageSelection;
        _memory         = memory;
        _cache          = cache;
        _voiceOptions   = voiceOptions.Value;
        _imageOptions   = imageOptions.Value;
        _log            = log;
    }

    public async Task<List<Uri>> EnrichAsync(OutreachDecision decision, CancellationToken ct = default)
    {
        var urls = new List<Uri>();

        if (!_imageOptions.Enabled || string.IsNullOrWhiteSpace(decision.Message))
            return urls;

        if (string.IsNullOrWhiteSpace(_voiceOptions.PublicBaseUrl))
        {
            _log.LogDebug("Image enrichment: no PublicBaseUrl configured — skipping");
            return urls;
        }

        try
        {
            var emotionalState = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
            var imagePath = await _imageSelection.SelectImageAsync(
                decision.Message, emotionalState, ct).ConfigureAwait(false);

            if (imagePath is null)
                return urls;

            if (!File.Exists(imagePath))
            {
                _log.LogWarning("Image enrichment: selected file not found: {Path}", imagePath);
                return urls;
            }

            var imageBytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
            var contentType = Path.GetExtension(imagePath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                _ => "image/jpeg"
            };

            var cacheKey = _cache.Store(imageBytes, contentType);
            var publicUrl = new Uri($"{_voiceOptions.PublicBaseUrl.TrimEnd('/')}/media/{cacheKey}");
            urls.Add(publicUrl);

            _log.LogInformation("Image enrichment: attached {File} ({Bytes} bytes) at \"{Url}\"",
                Path.GetFileName(imagePath), imageBytes.Length, publicUrl);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Image enrichment failed");
        }

        return urls;
    }
}
