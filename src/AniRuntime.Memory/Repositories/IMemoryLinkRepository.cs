using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>Feature 31 A-MEM linked memory graph operations.</summary>
public interface IMemoryLinkRepository
{
    void Add(MemoryLinkEntity link);
    Task<IReadOnlyList<MemoryLinkEntity>> GetLinksForMemoryAsync(Guid memoryId, string? relationshipType, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryLinkEntity>> GetAllAsync(CancellationToken ct = default);
    Task<int> GetCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Bulk insert with conflict-ignore semantics — duplicates on
    /// (source, target, relationship) composite key are skipped.
    /// </summary>
    Task<int> AddManyAsync(IEnumerable<MemoryLinkEntity> links, CancellationToken ct = default);
}
