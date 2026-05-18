using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>Feature 15 memory contradiction flagging.</summary>
public interface IMemoryContradictionRepository
{
    void Add(MemoryContradictionEntity contradiction);
    void Update(MemoryContradictionEntity contradiction);
    Task<MemoryContradictionEntity?> GetAsync(Guid newMemoryId, Guid existingMemoryId, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryContradictionEntity>> GetUnresolvedAsync(CancellationToken ct = default);
}
