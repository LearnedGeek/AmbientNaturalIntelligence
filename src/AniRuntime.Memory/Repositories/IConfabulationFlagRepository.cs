using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>AC5 confabulation feedback (Mark's ///flag command).</summary>
public interface IConfabulationFlagRepository
{
    void Add(ConfabulationFlagEntity flag);
    Task<IReadOnlyList<ConfabulationFlagEntity>> GetRecentAsync(int limit, CancellationToken ct = default);
    Task<IReadOnlyList<ConfabulationFlagEntity>> GetByCategoryAsync(string category, CancellationToken ct = default);
}
