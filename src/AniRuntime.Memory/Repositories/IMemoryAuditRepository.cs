using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>
/// Memory audit log (Apr 5, 2026 — added after the auto-corrector deleted
/// 128 valid memories with no recovery). Every create/update/delete/merge
/// is appended here so the affected row can be restored.
/// </summary>
public interface IMemoryAuditRepository
{
    void Add(MemoryAuditEntity entry);

    Task<MemoryAuditEntity?> GetByIdAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryAuditEntity>> GetRecentAsync(int limit, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryAuditEntity>> GetForMemoryAsync(Guid memoryId, CancellationToken ct = default);
}
