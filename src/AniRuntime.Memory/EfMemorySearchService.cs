using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused EF Core implementation
/// of <see cref="IMemorySearch"/>. Owns the simple type/tier reads,
/// anchored-memory list, and tier-scoped recency queries that have
/// already been ported off the legacy.
///
/// <para>
/// **Semantic-search composition is still delegated to <see cref="SqliteMemoryService"/>.**
/// Five methods remain on the strangler-fig fallback because they
/// involve cross-cutting domain logic that should live in a
/// dedicated <c>SemanticSearchComposer</c> service when extracted:
/// </para>
/// <list type="bullet">
///   <item><see cref="SearchAsync"/> + <see cref="SearchWithScoresAsync"/>
///     — full composite scoring (Park et al. cosine + importance + recency)
///     + MMR rerank (Carbonell &amp; Goldstein 1998) + Agentic-Lens Layer 1
///     origin-quota protected slots + Theme G own-output ceiling +
///     Feature 31 link-enhanced retrieval.</item>
///   <item><see cref="SearchByTypeAsync"/> — composite scoring filtered to one type.</item>
///   <item><see cref="SearchByTierAsync"/> — composite scoring tier-scoped (Apr 30 recency-off for Facts tier).</item>
///   <item><see cref="GetLinkedMemoriesAsync"/> — Feature 31 1-hop bidirectional traversal.</item>
/// </list>
/// <para>
/// Each of these is a candidate for extraction into its own focused
/// service in the next Phase 5 step. Holding the delegations here
/// makes the boundary explicit and the porting target visible.
/// </para>
/// </summary>
public sealed class EfMemorySearchService : IMemorySearch
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly SqliteMemoryService _legacy;
    private readonly ILogger<EfMemorySearchService> _log;

    public EfMemorySearchService(
        IDbContextFactory<AniDbContext> dbFactory,
        SqliteMemoryService legacy,
        ILogger<EfMemorySearchService> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _legacy    = legacy    ?? throw new ArgumentNullException(nameof(legacy));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false)
        => _legacy.SearchAsync(query, topK, ct, enforceOriginQuota);

    public Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(
        string query, int topK = 10, CancellationToken ct = default)
        => _legacy.SearchWithScoresAsync(query, topK, ct);

    public Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(
        string query, MemoryType type, int topK = 5, CancellationToken ct = default)
        => _legacy.SearchByTypeAsync(query, type, topK, ct);

    public async Task<IEnumerable<MemoryRecord>> GetByTypeAsync(
        MemoryType type, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryRepository(db);
        var entities = await repo.GetByTypeAsync(type, limit, ct).ConfigureAwait(false);
        return entities.Select(EfMemoryMappings.MapToRecord);
    }

    public async Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryRepository(db);
        var entities = await repo.GetAnchoredAsync(ct).ConfigureAwait(false);
        return entities.Select(EfMemoryMappings.MapToRecord);
    }

    public Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default)
        => _legacy.GetLinkedMemoriesAsync(memoryId, relationshipType, ct);

    public Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f)
        => _legacy.SearchByTierAsync(query, tier, topK, ct, minCosine);

    public async Task<IEnumerable<MemoryRecord>> GetByTierAsync(
        EpistemicTier tier, int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.Memories
            .Where(m => m.Provenance == tier && m.Tier != DecayTier.Compressed)
            .OrderByDescending(m => m.OccurredAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return entities.Select(EfMemoryMappings.MapToRecord);
    }
}
