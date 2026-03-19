namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Composite interface — extends all five segregated memory interfaces.
/// Consumers should depend on the narrowest interface they need:
///   IMemoryPersistence — write operations
///   IMemorySearch      — semantic and type-based retrieval
///   IStateStore        — state document reads
///   IMemoryAnalytics   — metrics, loops, contradictions
///   IMemoryMaintenance — cleanup and resolution
///
/// IMemoryService exists for backward compatibility and for DI registration
/// of the single SqliteMemoryService implementation. New consumers should
/// prefer the focused interfaces.
/// </summary>
public interface IMemoryService : IMemoryPersistence, IMemorySearch, IStateStore, IMemoryAnalytics, IMemoryMaintenance
{
}
