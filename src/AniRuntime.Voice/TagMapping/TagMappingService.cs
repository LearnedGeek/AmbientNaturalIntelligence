using System.Text.Json;
using LearnedGeek.ML.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Voice.TagMapping;

/// <summary>
/// Stage 1 implementation: static rule-based tag mapping.
/// Resolves emotion classification results to v3 audio tags using a priority-ranked rule set.
/// Future stages (semantic similarity, learned preferences) extend this class.
///
/// 2026-05-20 — moved from <c>LearnedGeek.ML.TagMapping</c> back to ANI per
/// learnedgeek-libs Issue #2.
/// </summary>
public class TagMappingService : ITagMappingService
{
    private readonly List<TagMappingRule> _rules;
    private readonly ILogger<TagMappingService> _log;

    public TagMappingService(ILogger<TagMappingService> log, string? jsonPath = null)
    {
        _log = log;
        _rules = LoadRules(jsonPath);
    }

    public TagResolution? Resolve(EmotionResult emotion, DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.Now;
        var hour = now.Hour;

        var candidates = _rules
            .Where(r => MatchesEmotion(r, emotion) && MatchesHour(r, hour))
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.MinConfidence)
            .ToList();

        if (candidates.Count == 0)
            return null;

        var best = candidates[0];
        var confidence = emotion.PrimaryEmotion.Equals(best.Emotion, StringComparison.OrdinalIgnoreCase)
            ? emotion.Confidence
            : emotion.Scores.GetValueOrDefault(best.Emotion, 0f);

        _log.LogDebug("TagMapping resolved: {Emotion} ({Confidence:F2}) @ {Hour}h -> {Tag} (priority {Priority})",
            emotion.PrimaryEmotion, emotion.Confidence, hour, best.Tag, best.Priority);

        return new TagResolution(best.Tag, "static", best.Emotion, confidence);
    }

    public void RecordFeedback(string tag, string emotion, bool positive)
    {
        // Stage 1: log only. Stage 3 will persist and learn from feedback.
        _log.LogInformation("TagMapping feedback: {Tag} for {Emotion} = {Signal}",
            tag, emotion, positive ? "positive" : "negative");
    }

    private static bool MatchesEmotion(TagMappingRule rule, EmotionResult emotion)
    {
        // Primary emotion match
        if (emotion.PrimaryEmotion.Equals(rule.Emotion, StringComparison.OrdinalIgnoreCase))
        {
            return emotion.Confidence >= rule.MinConfidence
                && emotion.Confidence <= rule.MaxConfidence;
        }

        // Secondary emotion match (check scores dictionary)
        if (emotion.Scores.TryGetValue(rule.Emotion, out var score))
        {
            return score >= rule.MinConfidence && score <= rule.MaxConfidence;
        }

        return false;
    }

    private static bool MatchesHour(TagMappingRule rule, int hour)
    {
        if (rule.HourStart is null || rule.HourEnd is null)
            return true; // No time constraint

        var start = rule.HourStart.Value;
        var end = rule.HourEnd.Value;

        // Handle overnight ranges (e.g., 22 to 4)
        if (start > end)
            return hour >= start || hour < end;

        return hour >= start && hour < end;
    }

    private List<TagMappingRule> LoadRules(string? jsonPath)
    {
        var path = jsonPath ?? FindDefaultPath();

        if (path is null || !File.Exists(path))
        {
            _log.LogWarning("StaticTagMap.json not found at {Path}, using empty rule set", path);
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            var map = JsonSerializer.Deserialize<StaticTagMap>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            _log.LogInformation("TagMapping loaded {Count} rules from {Path}",
                map?.Rules.Count ?? 0, path);
            return map?.Rules ?? [];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to load StaticTagMap.json from {Path}", path);
            return [];
        }
    }

    private static string? FindDefaultPath()
    {
        // Check relative to executing assembly, then common locations
        var dir = AppContext.BaseDirectory;
        var candidate = Path.Combine(dir, "TagMapping", "StaticTagMap.json");
        if (File.Exists(candidate)) return candidate;

        candidate = Path.Combine(dir, "StaticTagMap.json");
        if (File.Exists(candidate)) return candidate;

        return null;
    }
}
