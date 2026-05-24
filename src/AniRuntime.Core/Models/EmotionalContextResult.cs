namespace AniRuntime.Core.Models;

/// <summary>
/// Output of <c>IEmotionalContextBuilder.BuildAsync</c>. Carries the four
/// emotional-context fields that <c>ContextBuilder</c> previously assembled
/// inline. Extracted 2026-05-19 as the first sub-builder of the
/// ContextBuilder SRP decomposition (Testability Architecture Plan §2).
/// </summary>
/// <remarks>
/// All fields are nullable / can be empty — the source-of-truth shape matches
/// the previous inline behavior in <c>ContextBuilder.BuildContextSnapshotAsync</c>
/// where each field independently degraded to null/empty on retrieval failure.
/// </remarks>
public sealed record EmotionalContextResult(
    RelationshipHealth?     RelationshipHealth,
    EmotionalDrift?         EmotionalDrift,
    string?                 PatternAwareness,
    IReadOnlyList<string>   ProcessedThemes,
    // Theme R.6 (#64) — per-thought decayed-contribution trajectory summary.
    // Null when no active contributions exist; otherwise carries volatility +
    // dominant-registers summary for composer-side structural branching.
    EmotionalContributionTrajectory? ContributionTrajectory = null);
