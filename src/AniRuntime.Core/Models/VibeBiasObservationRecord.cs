namespace AniRuntime.Core.Models;

/// <summary>
/// Vibe Loop V1.5a (2026-05-21) — structured persistence record for one
/// observational pass. Supersedes the log-line-only telemetry shape so
/// downstream consumers (V1.5b Gate 3 pre-bias correlation, C.2
/// effectiveness matrix, C.5 effectiveness substrate write-back) can join
/// the recommended register against <c>closed_conversation_records</c> by
/// <c>thread_id</c>. Same architectural principle as D-008: one data plane,
/// no parallel log-scraping path. See GitHub Issue #46.
///
/// One record per <c>VibeBiasObservation.ObserveAsync</c> invocation that
/// produces a bias result (skipped observations still emit Serilog
/// V15_BIAS_SKIP but do not persist).
///
/// <see cref="ThreadId"/> is nullable for the outreach call-site case: when
/// V1.5a runs from <c>OutreachPipeline</c> there may be no active thread.
/// </summary>
public sealed record VibeBiasObservationRecord
{
    public required Guid             Id                      { get; init; }
    public required DateTimeOffset   ObservedAt              { get; init; }
    public required string           CallSite                { get; init; }      // 'outreach' | 'reply'
    public          Guid?            ThreadId                { get; init; }
    public required string           ContactName             { get; init; }
    public required int              CandidateCount          { get; init; }
    public required int              TierBreakdownLight      { get; init; }
    public required int              TierBreakdownMedium     { get; init; }
    public required int              TierBreakdownHeavy      { get; init; }

    /// <summary>JSON array of <c>{ recordId, weight, tier, ageHours, valence }</c> objects, one per surfaced record.</summary>
    public required string           SurfacedRecordsJson     { get; init; }

    public required string           RecommendedRegister     { get; init; }
    public required float            RecommendedValue        { get; init; }
    public required string           DiversityScoreReason    { get; init; }
}
