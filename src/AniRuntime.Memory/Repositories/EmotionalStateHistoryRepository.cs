using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

public sealed class EmotionalStateHistoryRepository : IEmotionalStateHistoryRepository
{
    private readonly AniDbContext _db;
    public EmotionalStateHistoryRepository(AniDbContext db) => _db = db;

    public void Add(EmotionalStateHistoryEntity snapshot)
        => _db.EmotionalStateHistory.Add(snapshot);

    public async Task<IReadOnlyList<EmotionalStateHistoryEntity>> GetSinceAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        return await _db.EmotionalStateHistory
            .Where(h => h.RecordedAt >= since)
            .OrderByDescending(h => h.RecordedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EmotionalStateHistoryEntity>> GetRecentAsync(
        int hours, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        return await _db.EmotionalStateHistory
            .Where(h => h.RecordedAt >= since)
            .OrderByDescending(h => h.RecordedAt)
            .ToListAsync(ct);
    }
}
