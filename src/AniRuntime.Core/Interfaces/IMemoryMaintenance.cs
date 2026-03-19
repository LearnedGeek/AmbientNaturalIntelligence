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
}
