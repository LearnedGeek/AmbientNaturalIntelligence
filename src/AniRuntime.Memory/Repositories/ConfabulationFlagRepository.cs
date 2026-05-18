using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

public sealed class ConfabulationFlagRepository : IConfabulationFlagRepository
{
    private readonly AniDbContext _db;
    public ConfabulationFlagRepository(AniDbContext db) => _db = db;

    public void Add(ConfabulationFlagEntity flag)
        => _db.ConfabulationFlags.Add(flag);

    public async Task<IReadOnlyList<ConfabulationFlagEntity>> GetRecentAsync(
        int limit, CancellationToken ct = default)
    {
        return await _db.ConfabulationFlags
            .OrderByDescending(f => f.FlaggedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ConfabulationFlagEntity>> GetByCategoryAsync(
        string category, CancellationToken ct = default)
    {
        return await _db.ConfabulationFlags
            .Where(f => f.TopicCategory == category || f.CanonicalCategory == category)
            .OrderByDescending(f => f.FlaggedAt)
            .ToListAsync(ct);
    }
}
