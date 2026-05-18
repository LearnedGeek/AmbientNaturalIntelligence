using System.Text.Json;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused EF Core implementation
/// of <see cref="IStateStore"/>. Reads the four singleton state blobs
/// (character, desire, emotional, relationship-health) and the
/// emotional-state-history time series.
///
/// Extracted from the previous monolithic <c>EfMemoryService</c>.
/// Lifetime: registered as a singleton; one <see cref="AniDbContext"/>
/// is created per call via <see cref="IDbContextFactory{TContext}"/>
/// (UoW boundary = method scope, matching the existing repository
/// pattern).
/// </summary>
public sealed class EfStateStore : IStateStore
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly ILogger<EfStateStore> _log;

    public EfStateStore(
        IDbContextFactory<AniDbContext> dbFactory,
        ILogger<EfStateStore> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await new CharacterStateRepository(db).GetAsync(ct).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.Json)) return new CharacterStateDoc();
        return JsonSerializer.Deserialize<CharacterStateDoc>(entity.Json, JsonDefaults.CaseInsensitive)
               ?? new CharacterStateDoc();
    }

    public async Task<DesireState> GetDesireStateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await new DesireStateRepository(db).GetAsync(ct).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.Json)) return new DesireState();
        return JsonSerializer.Deserialize<DesireState>(entity.Json) ?? new DesireState();
    }

    public async Task<EmotionalState> GetEmotionalStateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await new EmotionalStateBlobRepository(db).GetAsync(ct).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.Json)) return new EmotionalState();
        return JsonSerializer.Deserialize<EmotionalState>(entity.Json) ?? new EmotionalState();
    }

    public async Task<RelationshipHealth> GetRelationshipHealthAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await new RelationshipHealthRepository(db).GetAsync(ct).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.Json)) return new RelationshipHealth();
        return JsonSerializer.Deserialize<RelationshipHealth>(entity.Json) ?? new RelationshipHealth();
    }

    public async Task<List<EmotionalStateSnapshot>> GetEmotionalHistoryAsync(int hours, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new EmotionalStateHistoryRepository(db);
        var entities = await repo.GetRecentAsync(hours, ct).ConfigureAwait(false);
        // Repository returns DESC; spec returns ASC for charting.
        return entities
            .OrderBy(h => h.RecordedAt)
            .Select(h => new EmotionalStateSnapshot(
                h.Warmth, h.Energy, h.Concern, h.Playfulness,
                h.ContactGapTension, h.RecordedAt))
            .ToList();
    }
}
