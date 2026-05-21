using System.Text.Json;
using AniRuntime.Voice.TagMapping;
using LearnedGeek.ML.Interfaces;
using LearnedGeek.ML.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Dashboard.Classification;

/// <summary>
/// Runs LM-Kit classification on existing emotional contributions and compares
/// against heuristic results. Stores comparisons for dashboard analysis.
///
/// 2026-05-20 — moved from <c>LearnedGeek.ML</c> back to ANI as a scope
/// expansion of Issue #2. This service is ANI-flavored — it consumes
/// ContributionSnapshot (ANI emotional-model vocabulary) and ITagMappingService
/// (also moved to ANI in Issue #2). Cross-project consumers comparing their
/// own heuristics against LM-Kit would build their own service against the
/// generic ITextClassificationService interface.
/// </summary>
public class ClassificationComparisonService
{
    private readonly ITextClassificationService _classifier;
    private readonly ITagMappingService _tagMapping;
    private readonly ILogger<ClassificationComparisonService> _log;

    // In-memory store — comparisons are transient analysis data, not persistent state
    private readonly List<ClassificationComparison> _comparisons = [];
    private ComparisonSummary? _latestSummary;
    private bool _scanInProgress;

    public IReadOnlyList<ClassificationComparison> Comparisons => _comparisons;
    public ComparisonSummary? LatestSummary => _latestSummary;
    public bool ScanInProgress => _scanInProgress;

    public ClassificationComparisonService(
        ITextClassificationService classifier,
        ITagMappingService tagMapping,
        ILogger<ClassificationComparisonService> log)
    {
        _classifier = classifier;
        _tagMapping = tagMapping;
        _log = log;
    }

    /// <summary>
    /// Run LM-Kit classification on a batch of contributions and compare against
    /// their existing heuristic register/category assignments.
    /// </summary>
    public async Task<ComparisonSummary> RunComparisonAsync(
        IReadOnlyList<ContributionSnapshot> contributions,
        CancellationToken ct = default)
    {
        if (_scanInProgress)
            throw new InvalidOperationException("Scan already in progress");

        _scanInProgress = true;
        _comparisons.Clear();

        try
        {
            _log.LogInformation("Classification comparison scan starting: {Count} contributions", contributions.Count);
            var processed = 0;

            foreach (var contrib in contributions)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(contrib.SourceContent)) continue;

                try
                {
                    var emotion = await _classifier.ClassifyEmotionAsync(contrib.SourceContent, ct).ConfigureAwait(false);
                    var sarcasm = await _classifier.DetectSarcasmAsync(contrib.SourceContent, ct).ConfigureAwait(false);

                    // Resolve what tag the ML classification would produce
                    var mlResolution = _tagMapping.Resolve(emotion);

                    // Resolve what tag the heuristic would produce (approximate from register)
                    var heuristicTag = ApproximateHeuristicTag(contrib.Register);

                    // Determine if the emotion aligns with the heuristic register
                    var emotionAligns = DoesEmotionAlignWithRegister(emotion.PrimaryEmotion, contrib.Register);

                    var comparison = new ClassificationComparison
                    {
                        SourceContributionId = contrib.Id,
                        SourceContent = contrib.SourceContent,
                        HeuristicRegister = contrib.Register,
                        HeuristicCategory = contrib.Category,
                        HeuristicSeverity = contrib.Severity,
                        MLEmotion = emotion.PrimaryEmotion,
                        MLConfidence = emotion.Confidence,
                        MLScoresJson = JsonSerializer.Serialize(emotion.Scores),
                        MLSarcasmDetected = sarcasm.IsSarcastic,
                        MLSarcasmConfidence = sarcasm.Confidence,
                        HeuristicTag = heuristicTag,
                        MLTag = mlResolution?.Tag,
                        EmotionAligns = emotionAligns,
                    };

                    _comparisons.Add(comparison);
                    processed++;

                    if (processed % 25 == 0)
                        _log.LogInformation("Classification comparison: {Processed}/{Total}", processed, contributions.Count);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Classification comparison failed for contribution {Id}", contrib.Id);
                }
            }

            _latestSummary = BuildSummary();
            _log.LogInformation(
                "Classification comparison complete: {Total} compared, tag agreement {TagRate:P0}, emotion agreement {EmotionRate:P0}",
                _latestSummary.TotalCompared, _latestSummary.TagAgreementRate, _latestSummary.EmotionAgreementRate);

            return _latestSummary;
        }
        finally
        {
            _scanInProgress = false;
        }
    }

    private ComparisonSummary BuildSummary()
    {
        var total = _comparisons.Count;
        var tagMatches = _comparisons.Count(c => c.TagsMatch);
        var emotionAlignments = _comparisons.Count(c => c.EmotionAligns);

        var mlDist = _comparisons
            .GroupBy(c => c.MLEmotion)
            .ToDictionary(g => g.Key, g => g.Count());

        var heuristicDist = _comparisons
            .Where(c => !string.IsNullOrEmpty(c.HeuristicRegister))
            .GroupBy(c => c.HeuristicRegister)
            .ToDictionary(g => g.Key, g => g.Count());

        return new ComparisonSummary(
            total,
            tagMatches,
            emotionAlignments,
            total > 0 ? (float)tagMatches / total : 0f,
            total > 0 ? (float)emotionAlignments / total : 0f,
            mlDist,
            heuristicDist,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Map a heuristic register to an approximate voice tag for comparison.
    /// This is what the current VoiceTagEnricher would roughly select.
    /// </summary>
    private static string? ApproximateHeuristicTag(string register) => register switch
    {
        "Longing" => "[wistful]",
        "Tenderness" => "[tender]",
        "Delight" => "[cheerful]",
        "Playfulness" => "[playful]",
        "Curiosity" => "[curious]",
        "Existential" => "[contemplative]",
        "Warmth" => "[adoring]",
        "Hurt" => "[heartbroken]",
        "Concern" => "[anxious]",
        "Resilience" => "[calm]",
        _ => null,
    };

    /// <summary>
    /// Check if the ML emotion category broadly aligns with the heuristic register.
    /// Uses a fuzzy mapping since the categories don't map 1:1.
    /// </summary>
    private static bool DoesEmotionAlignWithRegister(string emotion, string register)
    {
        return (emotion, register) switch
        {
            ("happiness", "Delight" or "Playfulness" or "Warmth") => true,
            ("sadness", "Longing" or "Hurt" or "Existential") => true,
            ("anger", "Hurt") => true,
            ("fear", "Concern" or "Existential") => true,
            ("neutral", "Curiosity" or "Existential" or "Resilience") => true,
            ("love", "Tenderness" or "Longing" or "Warmth") => true,
            ("curiosity", "Curiosity") => true,
            ("amusement", "Playfulness" or "Delight") => true,
            _ => false,
        };
    }
}
