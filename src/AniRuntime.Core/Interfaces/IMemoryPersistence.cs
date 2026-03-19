using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Write operations for memory records and state documents.
/// Split from IMemoryService (ISP) — consumers that only write
/// should not depend on 47 methods they never call.
/// </summary>
public interface IMemoryPersistence
{
    Task SaveAsync(MemoryRecord record, CancellationToken ct = default);
    Task SaveCharacterStateAsync(CharacterStateDoc doc, CancellationToken ct = default);
    Task SaveDesireStateAsync(DesireState state, CancellationToken ct = default);
    Task SaveEmotionalStateAsync(EmotionalState state, CancellationToken ct = default);
    Task SaveEmotionalContributionAsync(EmotionalContribution contribution, CancellationToken ct = default);
    Task SaveRelationshipHealthAsync(RelationshipHealth health, CancellationToken ct = default);
    Task AdjustImportanceAsync(Guid id, float delta, CancellationToken ct = default);
    Task AnchorMemoryAsync(Guid id, string reason, CancellationToken ct = default);
}
