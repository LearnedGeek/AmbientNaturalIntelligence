using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

public sealed class MemoryAuditRepository : IMemoryAuditRepository
{
    private readonly AniDbContext _db;
    public MemoryAuditRepository(AniDbContext db) => _db = db;

    public void Add(MemoryAuditEntity entry) => _db.MemoryAudit.Add(entry);

    public Task<MemoryAuditEntity?> GetByIdAsync(long id, CancellationToken ct = default)
        => _db.MemoryAudit.FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<MemoryAuditEntity>> GetRecentAsync(
        int limit, CancellationToken ct = default)
    {
        return await _db.MemoryAudit
            .OrderByDescending(a => a.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryAuditEntity>> GetForMemoryAsync(
        Guid memoryId, CancellationToken ct = default)
    {
        return await _db.MemoryAudit
            .Where(a => a.MemoryId == memoryId)
            .OrderByDescending(a => a.OccurredAt)
            .ToListAsync(ct);
    }
}
