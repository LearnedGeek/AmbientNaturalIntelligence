using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

public sealed class EmotionalContributionRepository : IEmotionalContributionRepository
{
    private readonly AniDbContext _db;
    public EmotionalContributionRepository(AniDbContext db) => _db = db;

    public void Add(EmotionalContributionEntity contribution)    => _db.EmotionalContributions.Add(contribution);
    public void Update(EmotionalContributionEntity contribution) => _db.EmotionalContributions.Update(contribution);

    public Task<EmotionalContributionEntity?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.EmotionalContributions.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<EmotionalContributionEntity>> GetActiveAsync(CancellationToken ct = default)
    {
        // Active = not fully decayed. Use a generous lookback (~30 days) and
        // let the EmotionalProcessor compute the actual decay at retrieval.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        return await _db.EmotionalContributions
            .Where(c => c.CreatedAt >= cutoff)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EmotionalContributionEntity>> GetSinceAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        return await _db.EmotionalContributions
            .Where(c => c.CreatedAt >= since)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => _db.EmotionalContributions
            .Where(c => c.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);
}
