using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>
/// Phase 2 (2026-05-17) — repository contract for the <c>memories</c>
/// table. Methods enqueue changes on the underlying <c>AniDbContext</c>;
/// the caller (typically <c>EfMemoryService</c>) decides when to commit
/// via <c>SaveChangesAsync</c>. Multiple repositories sharing one
/// DbContext form the Unit of Work boundary — operations across repos
/// either succeed atomically or roll back together.
///
/// This contract intentionally exposes <see cref="MemoryEntity"/> (the
/// EF-managed type), not <see cref="MemoryRecord"/> (the domain POCO).
/// <c>EfMemoryService</c> handles the entity↔domain mapping at the
/// service layer boundary, preserving the existing
/// <c>IMemoryPersistence</c> / <c>IMemorySearch</c> contract for callers.
/// </summary>
public interface IMemoryRepository
{
    void Add(MemoryEntity entity);
    void Update(MemoryEntity entity);
    void Remove(MemoryEntity entity);

    Task<MemoryEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntity>> GetByTypeAsync(MemoryType type, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntity>> GetByTierAsync(DecayTier tier, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntity>> GetAnchoredAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MemoryEntity>> GetRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Loads all records with non-null embedding for in-memory cosine
    /// scoring. Excludes the <see cref="DecayTier.Compressed"/> tier.
    /// </summary>
    Task<IReadOnlyList<MemoryEntity>> GetForSemanticSearchAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MemoryEntity>> GetForSemanticSearchByTypeAsync(MemoryType type, CancellationToken ct = default);

    /// <summary>
    /// Loads records of a specific epistemic tier (Facts / Episodic / Interior)
    /// for tier-scoped retrieval. Excludes Compressed.
    /// </summary>
    Task<IReadOnlyList<MemoryEntity>> GetForTierScopedSearchAsync(EpistemicTier provenance, CancellationToken ct = default);

    /// <summary>
    /// Returns records eligible for compression per the Phase 6 v1.2 predicate:
    /// not Anchored, not Compressed, not character-seed source, importance below
    /// the configured ceiling, embedding present. The recency threshold is
    /// applied by the caller (decay-multiplier is type-aware).
    /// </summary>
    Task<IReadOnlyList<MemoryEntity>> GetCompressionCandidatesAsync(float importanceCeiling, int overFetch, CancellationToken ct = default);

    /// <summary>
    /// Bulk update: marks the supplied IDs as <see cref="DecayTier.Compressed"/>,
    /// excluding any that are already Anchored or Compressed. Returns the
    /// count actually updated. Per-source-tolerant — one source failing
    /// (e.g. record already gone) does not affect others.
    /// </summary>
    Task<int> MarkCompressedAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
}
