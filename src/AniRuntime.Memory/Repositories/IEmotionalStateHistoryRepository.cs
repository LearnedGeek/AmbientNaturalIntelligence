using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>Append-only time-series of emotional state snapshots.</summary>
public interface IEmotionalStateHistoryRepository
{
    void Add(EmotionalStateHistoryEntity snapshot);
    Task<IReadOnlyList<EmotionalStateHistoryEntity>> GetSinceAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<IReadOnlyList<EmotionalStateHistoryEntity>> GetRecentAsync(int hours, CancellationToken ct = default);
}
