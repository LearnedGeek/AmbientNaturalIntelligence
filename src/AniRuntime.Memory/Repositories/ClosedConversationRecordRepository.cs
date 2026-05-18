using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

public sealed class ClosedConversationRecordRepository : IClosedConversationRecordRepository
{
    private readonly AniDbContext _db;
    public ClosedConversationRecordRepository(AniDbContext db) => _db = db;

    public void Add(ClosedConversationRecordEntity record)    => _db.ClosedConversationRecords.Add(record);
    public void Update(ClosedConversationRecordEntity record) => _db.ClosedConversationRecords.Update(record);

    public Task<ClosedConversationRecordEntity?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _db.ClosedConversationRecords.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<ClosedConversationRecordEntity?> GetByThreadIdAsync(
        Guid threadId, CancellationToken ct = default)
    {
        return _db.ClosedConversationRecords
            .Where(r => r.ThreadId == threadId)
            .OrderByDescending(r => r.ClosedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ClosedConversationRecordEntity>> GetRecentAsync(
        int limit, CancellationToken ct = default)
    {
        return await _db.ClosedConversationRecords
            .OrderByDescending(r => r.ClosedAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
