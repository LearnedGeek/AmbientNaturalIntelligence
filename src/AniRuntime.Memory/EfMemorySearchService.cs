using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused EF Core implementation
/// of <see cref="IMemorySearch"/>. Phase 5 closeout (2026-05-18): all
/// semantic-search delegations to the legacy <see cref="SqliteMemoryService"/>
/// have been replaced by <see cref="ISemanticSearchComposer"/>.
///
/// Eight methods, split by responsibility:
/// <list type="bullet">
///   <item>Simple tier/type reads (<see cref="GetByTypeAsync"/>,
///     <see cref="GetByTierAsync"/>, <see cref="GetAnchoredMemoriesAsync"/>)
///     — direct EF queries via <see cref="MemoryRepository"/>.</item>
///   <item>Composite-scoring searches (<see cref="SearchAsync"/>,
///     <see cref="SearchWithScoresAsync"/>, <see cref="SearchByTypeAsync"/>,
///     <see cref="SearchByTierAsync"/>) and <see cref="GetLinkedMemoriesAsync"/>
///     — delegated to the composer.</item>
/// </list>
/// </summary>
public sealed class EfMemorySearchService : IMemorySearch
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly ISemanticSearchComposer _composer;
    private readonly ILogger<EfMemorySearchService> _log;

    public EfMemorySearchService(
        IDbContextFactory<AniDbContext> dbFactory,
        ISemanticSearchComposer composer,
        ILogger<EfMemorySearchService> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _composer  = composer  ?? throw new ArgumentNullException(nameof(composer));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false)
        => _composer.SearchAsync(query, topK, ct, enforceOriginQuota);

    public Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(
        string query, int topK = 10, CancellationToken ct = default)
        => _composer.SearchWithScoresAsync(query, topK, ct);

    public Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(
        string query, MemoryType type, int topK = 5, CancellationToken ct = default)
        => _composer.SearchByTypeAsync(query, type, topK, ct);

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
        => _composer.GetLinkedMemoriesAsync(memoryId, relationshipType, ct);

    public Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f)
        => _composer.SearchByTierAsync(query, tier, topK, ct, minCosine);

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
