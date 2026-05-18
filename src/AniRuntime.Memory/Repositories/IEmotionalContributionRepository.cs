using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>
/// Per-thought emotional contribution decay model (EM1-9 from Paper 1).
/// Each cognitive event produces one contribution with independent half-life.
/// </summary>
public interface IEmotionalContributionRepository
{
    void Add(EmotionalContributionEntity contribution);
    void Update(EmotionalContributionEntity contribution);
    Task<EmotionalContributionEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<EmotionalContributionEntity>> GetActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EmotionalContributionEntity>> GetSinceAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
