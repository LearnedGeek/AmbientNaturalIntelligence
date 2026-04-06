using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Maintenance operations — resolve loops/contradictions, cleanup decayed data.
/// Split from IMemoryService (ISP) — only the cognitive cycle orchestrator
/// needs these; most consumers never call them.
/// </summary>
public interface IMemoryMaintenance
{
    Task ResolveOpenLoopAsync(Guid id, CancellationToken ct = default);
    Task ResolveContradictionAsync(Guid newMemoryId, Guid existingMemoryId, CancellationToken ct = default);
    Task CleanupDecayedContributionsAsync(CancellationToken ct = default);
    Task ExpireContributionAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Feature 37: Retroactive link building — scans existing memories and creates
    /// memory_links for related records. Also merges detected duplicates.
    /// Returns (mergeCount, linkCount) for reporting.
    /// </summary>
    Task<(int MergeCount, int LinkCount)> RebuildMemoryLinksAsync(CancellationToken ct = default);

    /// <summary>Total count of links in the memory graph.</summary>
    Task<int> GetLinkCountAsync(CancellationToken ct = default);

    /// <summary>Feature 39: Returns all memory links for graph visualization.</summary>
    Task<IReadOnlyList<MemoryLink>> GetAllLinksAsync(CancellationToken ct = default);

    /// <summary>Get recent audit log entries for dashboard or ///audit command.</summary>
    Task<List<AuditEntry>> GetRecentAuditEntriesAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>Restore a deleted memory from its audit entry.</summary>
    Task<bool> RestoreFromAuditAsync(long auditId, CancellationToken ct = default);
}
