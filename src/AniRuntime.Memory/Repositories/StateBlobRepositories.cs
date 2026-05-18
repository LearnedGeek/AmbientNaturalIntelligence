using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

/// <summary>
/// Singleton-row state-blob repositories. Each owns one row (id=1) holding
/// a JSON-serialized state document. EfMemoryService handles serialization
/// at the service boundary; repositories just round-trip the JSON string.
/// </summary>
public interface IStateBlobRepository<TEntity> where TEntity : class
{
    Task<TEntity?> GetAsync(CancellationToken ct = default);
    Task UpsertAsync(string json, CancellationToken ct = default);
}

public sealed class CharacterStateRepository : IStateBlobRepository<CharacterStateEntity>
{
    private readonly AniDbContext _db;
    public CharacterStateRepository(AniDbContext db) => _db = db;

    public Task<CharacterStateEntity?> GetAsync(CancellationToken ct = default)
        => _db.CharacterState.FirstOrDefaultAsync(ct);

    public async Task UpsertAsync(string json, CancellationToken ct = default)
    {
        var existing = await _db.CharacterState.FirstOrDefaultAsync(e => e.Id == 1, ct);
        if (existing == null)
            _db.CharacterState.Add(new CharacterStateEntity { Id = 1, Json = json });
        else
            existing.Json = json;
    }
}

public sealed class DesireStateRepository : IStateBlobRepository<DesireStateEntity>
{
    private readonly AniDbContext _db;
    public DesireStateRepository(AniDbContext db) => _db = db;

    public Task<DesireStateEntity?> GetAsync(CancellationToken ct = default)
        => _db.DesireState.FirstOrDefaultAsync(ct);

    public async Task UpsertAsync(string json, CancellationToken ct = default)
    {
        var existing = await _db.DesireState.FirstOrDefaultAsync(e => e.Id == 1, ct);
        if (existing == null)
            _db.DesireState.Add(new DesireStateEntity { Id = 1, Json = json });
        else
            existing.Json = json;
    }
}

public sealed class EmotionalStateBlobRepository : IStateBlobRepository<EmotionalStateBlobEntity>
{
    private readonly AniDbContext _db;
    public EmotionalStateBlobRepository(AniDbContext db) => _db = db;

    public Task<EmotionalStateBlobEntity?> GetAsync(CancellationToken ct = default)
        => _db.EmotionalState.FirstOrDefaultAsync(ct);

    public async Task UpsertAsync(string json, CancellationToken ct = default)
    {
        var existing = await _db.EmotionalState.FirstOrDefaultAsync(e => e.Id == 1, ct);
        if (existing == null)
            _db.EmotionalState.Add(new EmotionalStateBlobEntity { Id = 1, Json = json });
        else
            existing.Json = json;
    }
}

public sealed class RelationshipHealthRepository : IStateBlobRepository<RelationshipHealthEntity>
{
    private readonly AniDbContext _db;
    public RelationshipHealthRepository(AniDbContext db) => _db = db;

    public Task<RelationshipHealthEntity?> GetAsync(CancellationToken ct = default)
        => _db.RelationshipHealth.FirstOrDefaultAsync(ct);

    public async Task UpsertAsync(string json, CancellationToken ct = default)
    {
        var existing = await _db.RelationshipHealth.FirstOrDefaultAsync(e => e.Id == 1, ct);
        if (existing == null)
            _db.RelationshipHealth.Add(new RelationshipHealthEntity { Id = 1, Json = json });
        else
            existing.Json = json;
    }
}
