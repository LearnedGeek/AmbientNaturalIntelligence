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

    /// <summary>
    /// Feature 41: Delete a memory record by ID. Used by diagnostic auto-correction
    /// to remove InnerThought memories that are driving retrieval loops.
    /// Only call for regenerable content (InnerThought) — never for Episodic/conversation data.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// AC5: Store a confabulation flag for pattern analysis.
    /// `topicCategory` is the tag label when invoked via ///tag (e.g. "confabulation",
    /// "temporal confusion", "repetition"); null or empty when invoked via ///flag.
    /// `notes` is free-form additional context and is typically unused today.
    /// </summary>
    Task SaveConfabulationFlagAsync(
        string contactMessage,
        string aniReply,
        string? topicCategory = null,
        string? notes = null,
        CancellationToken ct = default);

    /// <summary>
    /// Feature 32: Returns the N most recent memories across all types.
    /// Excludes reflection-sourced memories to prevent synthesis loops.
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetRecentAsync(int limit = 10, CancellationToken ct = default);
}
