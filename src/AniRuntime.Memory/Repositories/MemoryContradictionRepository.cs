using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

public sealed class MemoryContradictionRepository : IMemoryContradictionRepository
{
    private readonly AniDbContext _db;
    public MemoryContradictionRepository(AniDbContext db) => _db = db;

    public void Add(MemoryContradictionEntity contradiction)
        => _db.MemoryContradictions.Add(contradiction);

    public void Update(MemoryContradictionEntity contradiction)
        => _db.MemoryContradictions.Update(contradiction);

    public Task<MemoryContradictionEntity?> GetAsync(
        Guid newMemoryId, Guid existingMemoryId, CancellationToken ct = default)
    {
        return _db.MemoryContradictions
            .FirstOrDefaultAsync(c => c.NewMemoryId == newMemoryId
                                   && c.ExistingMemoryId == existingMemoryId, ct);
    }

    public async Task<IReadOnlyList<MemoryContradictionEntity>> GetUnresolvedAsync(
        CancellationToken ct = default)
    {
        return await _db.MemoryContradictions
            .Where(c => !c.IsResolved)
            .OrderByDescending(c => c.FlaggedAt)
            .ToListAsync(ct);
    }
}
