using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Analytical queries over memory — open loops, contradictions, metrics.
/// Split from IMemoryService (ISP) — analytics consumers don't need
/// CRUD or search operations.
/// </summary>
public interface IMemoryAnalytics
{
    Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default);
    Task<List<MemoryContradiction>> GetFlaggedContradictionsAsync(bool includeResolved = false, CancellationToken ct = default);
    Task<int> GetRecentMessageCountAsync(int days, CancellationToken ct = default);
    Task<float> GetAverageConversationValenceAsync(int days, CancellationToken ct = default);
    Task<(int outreach, int inbound)> GetInitiativeBalanceAsync(int days, CancellationToken ct = default);
    Task<List<EmotionalContribution>> GetActiveContributionsAsync(CancellationToken ct = default);
    Task<List<EmotionalContribution>> GetContributionsSinceAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<List<string>> GetProcessedThemesAsync(int maxThemes = 5, CancellationToken ct = default);

    /// <summary>
    /// Foundation Attribution (F-2) Phase 1 P8 (2026-08-23) — aggregate
    /// counts of persisted memories grouped by
    /// (<see cref="AttributedTo"/>, <see cref="MemoryRecord.AttributionTrust"/>).
    /// Surfaces the substrate's attribution health at a glance: unknown/
    /// unverified-historical rows should decline as fresh substrate writes
    /// overwrite the P3 backfill tail; Ani/Mark ratio should sit in a
    /// normal range for the deployment. Any spike in Unknown after a
    /// backfill run indicates a producer emit site regressed and started
    /// dropping attribution.
    /// </summary>
    Task<AttributionDistribution> GetAttributionDistributionAsync(CancellationToken ct = default);

    /// <summary>
    /// F-4 Phase 4a.2a (2026-10-22) — Returns the most-recent
    /// <paramref name="limit"/> non-null associative anchors from
    /// <c>emotional_contributions</c>, ordered newest first. Caller is
    /// typically <c>CognitiveCyclePipeline</c>, which uses this to hydrate
    /// its in-process chain on first use so the associative-drift substrate
    /// survives service restart.
    ///
    /// <para>
    /// Life Audit §Dim 5 and Mark's 2026-10-22 observation about the
    /// dashboard "Associative Drift" view surfaced that anchors are
    /// produced every cycle (1,694 in production over 7 months) but only
    /// the most recent is kept, in-memory, and lost on restart. This
    /// method is the retrieval half of the fix; see umbrella issue #171.
    /// </para>
    ///
    /// <para>
    /// Returns an empty list when no anchors are persisted or when the
    /// limit is zero/negative — never null. Fail-open on query error is
    /// the caller's responsibility.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> GetRecentAssociativeAnchorsAsync(
        int limit, CancellationToken ct = default);
}

/// <summary>
/// Aggregate breakdown of the memories table by attribution axis. Both
/// dictionaries key on the enum/string value; values are row counts.
/// </summary>
public sealed record AttributionDistribution(
    IReadOnlyDictionary<AttributedTo, int> ByAttributedTo,
    IReadOnlyDictionary<string, int>       ByTrust,
    int                                    TotalRows);
