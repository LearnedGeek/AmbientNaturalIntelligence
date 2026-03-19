using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Utilities;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Selects images from a curated local library based on keyword matching
/// between message content and image tags. Includes probability gating
/// (images should feel organic, not constant) and daily rate limiting.
/// </summary>
public class ImageSelectionService : IImageSelectionService
{
    private readonly ImageOptions _options;
    private readonly ILogger<ImageSelectionService> _log;
    private readonly List<ImageEntry> _library = new();
    private readonly object _lock = new();
    private int _imagesSentToday;
    private DateTimeOffset _lastResetDate = DateTimeOffset.MinValue;
    private readonly Random _rng = new();

    public ImageSelectionService(
        IOptions<ImageOptions> options,
        ILogger<ImageSelectionService> log)
    {
        _options = options.Value;
        _log = log;
        LoadLibrary();
    }

    public Task<string?> SelectImageAsync(
        string messageContext, EmotionalState emotionalState, CancellationToken ct = default)
    {
        if (_library.Count == 0)
        {
            _log.LogDebug("Image selection: library is empty");
            return Task.FromResult<string?>(null);
        }

        // Daily rate limit
        ResetDailyCountIfNeeded();
        if (_imagesSentToday >= _options.MaxImagesPerDay)
        {
            _log.LogDebug("Image selection: daily limit reached ({Count}/{Max})",
                _imagesSentToday, _options.MaxImagesPerDay);
            return Task.FromResult<string?>(null);
        }

        // Probability gate — images should feel like a surprise, not a constant
        if (_rng.NextDouble() > _options.AttachmentProbability)
        {
            _log.LogDebug("Image selection: probability gate (skipped this time)");
            return Task.FromResult<string?>(null);
        }

        // Find matching images by keyword overlap
        var contextWords = messageContext.ToLowerInvariant().Split(
            [' ', ',', '.', '!', '?', '\n', '\r', '\t', '-', '—'],
            StringSplitOptions.RemoveEmptyEntries);

        var scored = _library
            .Select(img => new
            {
                Image = img,
                Score = img.Tags.Count(tag => contextWords.Any(w => w.Contains(tag) || tag.Contains(w)))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0)
        {
            _log.LogDebug("Image selection: no tag matches for context");
            return Task.FromResult<string?>(null);
        }

        // Pick from the top matches with some randomness
        var topScore = scored[0].Score;
        var candidates = scored.Where(x => x.Score >= topScore - 1).ToList();
        var selected = candidates[_rng.Next(candidates.Count)].Image;

        lock (_lock) { _imagesSentToday++; }

        var filePath = Path.Combine(_options.LibraryPath, selected.File);
        _log.LogInformation("Image selected: {File} (tags: {Tags}, score: {Score}, {Count}/{Max} today)",
            selected.File, string.Join(", ", selected.Tags), topScore,
            _imagesSentToday, _options.MaxImagesPerDay);

        return Task.FromResult<string?>(filePath);
    }

    private void LoadLibrary()
    {
        var manifestPath = Path.Combine(_options.LibraryPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            _log.LogWarning("Image library manifest not found at {Path}", manifestPath);
            return;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ImageManifest>(json, JsonOpts);
            if (manifest?.Images is null) return;

            foreach (var entry in manifest.Images)
            {
                var filePath = Path.Combine(_options.LibraryPath, entry.File);
                if (entry.File == "placeholder.txt") continue;

                if (!File.Exists(filePath))
                {
                    _log.LogWarning("Image file not found: {File}", filePath);
                    continue;
                }

                _library.Add(entry);
            }

            _log.LogInformation("Image library loaded: {Count} images", _library.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load image library from {Path}", manifestPath);
        }
    }

    private void ResetDailyCountIfNeeded()
    {
        var today = DateTimeOffset.UtcNow.Date;
        if (_lastResetDate.Date < today)
        {
            lock (_lock)
            {
                if (_lastResetDate.Date < today)
                {
                    _imagesSentToday = 0;
                    _lastResetDate = DateTimeOffset.UtcNow;
                }
            }
        }
    }

    // Extends JsonDefaults.CamelCase with comment handling for manifest.json
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonDefaults.CamelCase.PropertyNamingPolicy,
        PropertyNameCaseInsensitive = JsonDefaults.CamelCase.PropertyNameCaseInsensitive,
        DefaultIgnoreCondition      = JsonDefaults.CamelCase.DefaultIgnoreCondition,
        ReadCommentHandling         = JsonCommentHandling.Skip,
    };

    private record ImageManifest
    {
        public List<ImageEntry> Images { get; init; } = new();
    }
}

public record ImageEntry
{
    public string File { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = new();
    public string Description { get; init; } = string.Empty;
}
