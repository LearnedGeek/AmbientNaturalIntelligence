using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Read operations for character, desire, emotional, and relationship state.
/// Split from IMemoryService (ISP) — state readers don't need search
/// or write operations.
/// </summary>
public interface IStateStore
{
    Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default);
    Task<DesireState> GetDesireStateAsync(CancellationToken ct = default);
    Task<EmotionalState> GetEmotionalStateAsync(CancellationToken ct = default);
    Task<RelationshipHealth> GetRelationshipHealthAsync(CancellationToken ct = default);
    Task<List<EmotionalStateSnapshot>> GetEmotionalHistoryAsync(int hours, CancellationToken ct = default);
}
