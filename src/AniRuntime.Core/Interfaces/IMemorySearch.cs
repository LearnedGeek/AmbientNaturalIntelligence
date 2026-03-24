using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Semantic and type-based memory retrieval.
/// Split from IMemoryService (ISP) — search consumers don't need
/// state persistence or analytics methods.
/// </summary>
public interface IMemorySearch
{
    Task<IEnumerable<MemoryRecord>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(string query, MemoryType type, int topK = 5, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetByTypeAsync(MemoryType type, int limit = 50, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Feature 31: Returns all memories linked to the given memory (1-hop bidirectional).
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default);
}
