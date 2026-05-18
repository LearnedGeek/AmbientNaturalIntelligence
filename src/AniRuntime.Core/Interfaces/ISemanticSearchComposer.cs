using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused contract for the
/// composite semantic-search pipeline (Park et al. cosine + importance +
/// recency scoring; MMR diversity rerank; Agentic-Lens Layer 1 protected
/// origin-tier slots; Theme G own-output ceiling; Feature 31 link-enhanced
/// retrieval; tier-scoped recency-off Facts-tier ranking; Feature 24
/// type-aware decay). Extracted from <c>SqliteMemoryService</c>'s
/// <c>SearchAsync</c>, <c>SearchWithScoresAsync</c>, <c>SearchByTypeAsync</c>,
/// <c>SearchByTierAsync</c>, and <c>GetLinkedMemoriesAsync</c> so the
/// <c>IMemorySearch</c> facade can delegate composition to a dedicated
/// domain service.
///
/// <para>
/// Five methods, each with its own caveat:
/// </para>
/// <list type="bullet">
///   <item><see cref="SearchAsync"/> — full composition; <c>enforceOriginQuota</c>
///     gates the Layer 1 protected-slots backfill (inner-thought cycle
///     opts in; conversation-reply leaves it off).</item>
///   <item><see cref="SearchWithScoresAsync"/> — same composition plus
///     Feature 31 link-enhanced retrieval (≥0.40 cosine on 1-hop
///     neighbours of the top-K).</item>
///   <item><see cref="SearchByTypeAsync"/> — same composition filtered
///     to one <c>MemoryType</c> at the candidate-load step.</item>
///   <item><see cref="SearchByTierAsync"/> — Apr 30 tier-aware: Facts
///     scores on cosine + importance only (no recency); other tiers
///     keep the full composite. <c>minCosine</c> is the Theme P P.4
///     noise-floor on raw cosine before the top-K cut.</item>
///   <item><see cref="GetLinkedMemoriesAsync"/> — Feature 31 1-hop
///     bidirectional <c>memory_links</c> traversal returning the linked
///     records (not scored).</item>
/// </list>
/// </summary>
public interface ISemanticSearchComposer
{
    Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false);

    Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(
        string query, int topK = 10, CancellationToken ct = default);

    Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(
        string query, MemoryType type, int topK = 5, CancellationToken ct = default);

    Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f);

    Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default);
}
