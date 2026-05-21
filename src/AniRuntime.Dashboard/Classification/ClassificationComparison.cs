namespace AniRuntime.Dashboard.Classification;

/// <summary>
/// Side-by-side comparison of heuristic vs ML classification on the same text.
/// Used to evaluate LM-Kit accuracy against ANI's heuristic register system.
///
/// 2026-05-20 — moved from <c>LearnedGeek.ML.Models</c> back to ANI alongside
/// ClassificationComparisonService (Issue #2 scope expansion). The comparison
/// is ANI-flavored — its inputs (Register, Category, Severity) are ANI's
/// emotional-model vocabulary, and the tag-resolution comparison depends on
/// the AniRuntime.Voice.TagMapping subsystem also moved per Issue #2.
/// </summary>
public class ClassificationComparison
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceContributionId { get; init; }
    public string SourceContent { get; init; } = string.Empty;

    // Existing heuristic classification
    public string HeuristicRegister { get; init; } = string.Empty;
    public string HeuristicCategory { get; init; } = string.Empty;
    public float HeuristicSeverity { get; init; }

    // LM-Kit classification
    public string MLEmotion { get; init; } = string.Empty;
    public float MLConfidence { get; init; }
    public string MLScoresJson { get; init; } = "{}";
    public bool MLSarcasmDetected { get; init; }
    public float MLSarcasmConfidence { get; init; }

    // Resolved tag comparison
    public string? HeuristicTag { get; init; }
    public string? MLTag { get; init; }

    // Agreement
    public bool TagsMatch => HeuristicTag == MLTag;
    public bool EmotionAligns { get; init; }

    public DateTimeOffset ClassifiedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Summary statistics from a comparison scan.
/// </summary>
public record ComparisonSummary(
    int TotalCompared,
    int TagMatches,
    int EmotionAlignments,
    float TagAgreementRate,
    float EmotionAgreementRate,
    Dictionary<string, int> MLEmotionDistribution,
    Dictionary<string, int> HeuristicRegisterDistribution,
    DateTimeOffset ScanTimestamp);

/// <summary>
/// Lightweight snapshot of a contribution for comparison purposes.
/// Avoids coupling to the full EmotionalContribution model.
/// </summary>
public record ContributionSnapshot(
    Guid Id,
    string SourceContent,
    string Register,
    string Category,
    float Severity);
