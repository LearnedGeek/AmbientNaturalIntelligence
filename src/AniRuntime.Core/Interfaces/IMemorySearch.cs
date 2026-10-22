using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Semantic and type-based memory retrieval.
/// Split from IMemoryService (ISP) — search consumers don't need
/// state persistence or analytics methods.
/// </summary>
public interface IMemorySearch
{
    /// <summary>
    /// Semantic search with optional origin-quota enforcement (Agentic Lens Layer 1 Phase 1c,
    /// Apr 2026). When <paramref name="enforceOriginQuota"/> is true AND
    /// <see cref="AniOptions.RetrievalProtectedSlotsEnabled"/> is true, the returned pool
    /// is adjusted to reserve a minimum non-caregiver-origin share per
    /// <see cref="AniOptions.MinNonCaregiverRetrievalFraction"/>. Intended for the inner-thought
    /// cycle retrieval path; conversation-reply retrieval should leave this false so reply
    /// generation remains caregiver-weighted.
    /// </summary>
    Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false);
    Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(string query, MemoryType type, int topK = 5, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetByTypeAsync(MemoryType type, int limit = 50, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Feature 31: Returns all memories linked to the given memory (1-hop bidirectional).
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default);

    /// <summary>
    /// Epistemic Grounding (Apr 10, 2026): Tier-scoped semantic search. Returns only
    /// memories whose <see cref="MemoryRecord.Provenance"/> matches the requested tier.
    ///
    /// Use this when the caller needs epistemically-bounded retrieval:
    /// - <see cref="EpistemicTier.Facts"/> for grounding factual claims about Mark/world.
    ///   This is the only tier that should ground assertions about Mark's external life.
    /// - <see cref="EpistemicTier.Episodic"/> for conversation continuity — "what was said."
    ///   Never treated as factual grounding.
    /// - <see cref="EpistemicTier.Interior"/> for Ani's voice, mood, self-concept, and
    ///   reflective continuity. Full creative latitude, structurally isolated from facts.
    ///
    /// Theme P Phase P.4 (May 12, 2026) — <paramref name="minCosine"/> is a
    /// per-call retrieval noise floor. Records whose raw cosine similarity to
    /// the query embedding falls below this value are excluded BEFORE top-K
    /// is taken. The filter is applied on raw cosine (not on composite
    /// score) because composite can be inflated by importance/recency on
    /// semantically-unrelated records. Default <c>0.0f</c> preserves the
    /// pre-P.4 behaviour for any consumer that has not migrated. Per-consumer
    /// calibration lives in <see cref="AniOptions.MinCosineThresholdVerifier"/>
    /// and <see cref="AniOptions.MinCosineThresholdComposition"/>.
    ///
    /// See docs/spec/design/ANI-Epistemic-Grounding-Architecture.md for the full design.
    /// </summary>
    Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f);

    /// <summary>
    /// Epistemic Grounding: Non-scored tier retrieval for callers that just need recent
    /// memories of a specific tier without semantic ranking. Useful for constructing
    /// prompt sections that want "the N most recent Interior memories" regardless of query.
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetByTierAsync(
        EpistemicTier tier, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// F-4 Phase 4a.1a (2026-10-22) — Perception-only retrieval ordered by
    /// the three-dimension importance model named in the Content-Coverage
    /// Audit: <c>effective_weight = Importance × TopicValence_lookup × fatigue_decay</c>.
    /// This phase ships axis 2 (preference, via <paramref name="topicValence"/>);
    /// axis 1 (significance) uses the record's existing <c>Importance</c>
    /// as placeholder until Phase 4a.1b lands; axis 3 (fatigue) hardcodes to
    /// 1.0 until Phase 4a.1c lands.
    ///
    /// <para>
    /// <b>Why this method exists:</b> the Life Audit §Dim 1 found that
    /// <see cref="MemoryType.Perception"/> is excluded from
    /// <c>RetrievalContextBuilder</c>'s RecentMemory composition — 7 months
    /// of RSS perception events (1,092 persisted) with near-zero footprint
    /// in 19k inner thoughts. Fix is a dedicated retrieval path for
    /// Perception that respects the project's existing importance / recency /
    /// provenance / decay machinery rather than raw-recency ordering.
    /// </para>
    ///
    /// <para>
    /// <b>Fuzzy TopicValence lookup:</b> for each candidate memory,
    /// implementations walk the <paramref name="topicValence"/> keys and
    /// pick the highest-weighted key whose text appears (case-insensitive
    /// substring) in <see cref="MemoryRecord.Content"/>. If no key matches,
    /// a neutral default (0.5f) is used so unmatched items are neither
    /// amplified nor zeroed-out. Simple heuristic matches the pre-Phase 1
    /// state of TopicValence keys as curated prose descriptions.
    /// </para>
    ///
    /// <para>
    /// <b>Source filtering:</b> callers typically pass
    /// <paramref name="allowedSourceNames"/> = {"rss", "weather"} to exclude
    /// <c>twilio-inbound</c> (which already surfaces via Mark's inbound
    /// channel) and <c>reactive-share-source</c> (which is Ani's own prior
    /// outreach substrate). Null allowedSourceNames means include all
    /// Perception source types.
    /// </para>
    ///
    /// <para>
    /// See <c>ani-docs/research/ANI-Content-Coverage-Audit-2026-10-22.md</c>
    /// §Recommendation and plan doc §Phase 4a.1a.
    /// </para>
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetPerceptionsOrderedAsync(
        IReadOnlyDictionary<string, float> topicValence,
        int limit = 5,
        IReadOnlyCollection<string>? allowedSourceNames = null,
        CancellationToken ct = default);
}
