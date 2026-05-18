using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

/// <summary>
/// Phase 2 (2026-05-17) — EF Core implementation of
/// <see cref="IMemoryRepository"/>. Takes a <see cref="AniDbContext"/> via
/// constructor so multiple repositories sharing the same context can
/// compose atomic operations (Unit of Work pattern).
///
/// All semantic-search loading paths exclude
/// <see cref="DecayTier.Compressed"/> per Phase 6 v1.2 — Compressed
/// records have been folded into reflection gists and are excluded from
/// retrieval but preserved for audit.
/// </summary>
public sealed class MemoryRepository : IMemoryRepository
{
    private readonly AniDbContext _db;

    public MemoryRepository(AniDbContext db) => _db = db;

    public void Add(MemoryEntity entity)    => _db.Memories.Add(entity);
    public void Update(MemoryEntity entity) => _db.Memories.Update(entity);
    public void Remove(MemoryEntity entity) => _db.Memories.Remove(entity);

    public Task<MemoryEntity?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.Memories.FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<MemoryEntity>> GetByTypeAsync(
        MemoryType type, int limit, CancellationToken ct = default)
    {
        return await _db.Memories
            .Where(m => m.Type == type && m.Tier != DecayTier.Compressed)
            .OrderByDescending(m => m.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> GetByTierAsync(
        DecayTier tier, int limit, CancellationToken ct = default)
    {
        return await _db.Memories
            .Where(m => m.Tier == tier)
            .OrderByDescending(m => m.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> GetAnchoredAsync(CancellationToken ct = default)
    {
        return await _db.Memories
            .Where(m => m.Tier == DecayTier.Anchored)
            .OrderBy(m => m.OccurredAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        // Mirrors the existing GetRecentAsync exclusion of reflection-source
        // records (prevents synthesis loops) AND v1.2 Compressed exclusion.
        return await _db.Memories
            .Where(m => (m.SourceName == null || m.SourceName != "reflection")
                     && m.Tier != DecayTier.Compressed)
            .OrderByDescending(m => m.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> GetForSemanticSearchAsync(CancellationToken ct = default)
    {
        return await _db.Memories
            .Where(m => m.Embedding != null && m.Tier != DecayTier.Compressed)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> GetForSemanticSearchByTypeAsync(
        MemoryType type, CancellationToken ct = default)
    {
        return await _db.Memories
            .Where(m => m.Embedding != null && m.Type == type && m.Tier != DecayTier.Compressed)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> GetForTierScopedSearchAsync(
        EpistemicTier provenance, CancellationToken ct = default)
    {
        return await _db.Memories
            .Where(m => m.Embedding != null && m.Provenance == provenance && m.Tier != DecayTier.Compressed)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> GetCompressionCandidatesAsync(
        float importanceCeiling, int overFetch, CancellationToken ct = default)
    {
        return await _db.Memories
            .Where(m => m.Tier != DecayTier.Anchored
                     && m.Tier != DecayTier.Compressed
                     && (m.SourceName == null || m.SourceName != "character-seed")
                     && m.Importance < importanceCeiling
                     && m.Embedding != null)
            .OrderBy(m => m.OccurredAt)
            .Take(overFetch)
            .ToListAsync(ct);
    }

    public async Task<int> MarkCompressedAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return 0;

        // ExecuteUpdate is the EF Core 7+ batched-update API. Translates to a
        // single UPDATE statement with WHERE id IN (...) AND tier NOT IN
        // ('Anchored','Compressed'). The per-source-tolerant guard from
        // the legacy MarkCompressedAsync is preserved.
        return await _db.Memories
            .Where(m => idList.Contains(m.Id)
                     && m.Tier != DecayTier.Anchored
                     && m.Tier != DecayTier.Compressed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.Tier, _ => DecayTier.Compressed), ct);
    }
}
