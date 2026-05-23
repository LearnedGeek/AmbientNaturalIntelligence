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
    /// `topicCategory` is the freeform researcher note from Mark's ///tag command
    /// (e.g. "confabulation", "temporal confusion", "repetition").
    /// `notes` is free-form additional context and is typically unused today.
    /// `canonicalCategory` is the taxonomized research-analysis label assigned later
    /// during analysis, not at tag time. See docs/research/tag-canonical-mapping.md
    /// for the taxonomy and mapping rules.
    /// </summary>
    Task SaveConfabulationFlagAsync(
        string contactMessage,
        string aniReply,
        string? topicCategory = null,
        string? notes = null,
        string? canonicalCategory = null,
        CancellationToken ct = default);

    /// <summary>
    /// Issue #62 (2026-05-23) — substrate-correction propagation for `///tag`
    /// walk-back. Finds the most recent Episodic conversation record whose
    /// content matches <paramref name="aniReplyContent"/> (the formatted
    /// "I said to Mark: ..." / "Ani said: ..." form) and sets its
    /// <c>validity</c> column to <c>'invalid_confabulation'</c>. Default
    /// retrieval surfaces filter on validity = 'valid' so the record stops
    /// surfacing as substrate.
    ///
    /// <para>Returns the number of records updated (typically 1; 0 if no
    /// matching Episodic record exists — e.g. the turn predated the
    /// conversation-Episodic write path or was already invalidated).</para>
    /// </summary>
    Task<int> MarkConversationTurnInvalidAsync(
        string aniReplyContent, CancellationToken ct = default);

    /// <summary>
    /// Feature 32: Returns the N most recent memories across all types.
    /// Excludes reflection-sourced memories to prevent synthesis loops.
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetRecentAsync(int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Phase 6 v1.2 (May 17, 2026) — returns records that have decayed past
    /// the recency threshold and are eligible for compression into a
    /// reflection gist. Used by <see cref="ReflectionPhase"/>'s decay-driven
    /// summarization path. See <c>docs/spec/design/ANI-MemoryReform-Design.md</c>
    /// v1.2 Refinement 1 (decay-threshold trigger) + Refinement 2 (lifecycle).
    ///
    /// Eligibility criteria (applied in implementation):
    ///   - tier != 'Anchored' (anchored records are decay-exempt)
    ///   - tier != 'Compressed' (already compressed; idempotent)
    ///   - source_name NOT IN ('reflection', 'character-seed') — don't
    ///     compress reflections (avoid summary-of-summary loops) or
    ///     character seeds (canonical Mark-supplied identity)
    ///   - importance &lt; <see cref="AniOptions.DecayEligibilityImportanceThreshold"/>
    ///     (high-importance moments survive verbatim as "significant
    ///     moments" per v1.2 design)
    ///   - recency score has decayed past
    ///     <see cref="AniOptions.DecayEligibilityRecencyThreshold"/>
    ///     (typically 0.30 — about 1.7 type-aware half-lives past creation)
    ///
    /// Returns oldest-first, capped at <paramref name="limit"/>.
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetDecayEligibleAsync(int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// Phase 6 v1.2 (May 17, 2026) — marks records as compressed into a
    /// reflection gist. Sets <c>tier='Compressed'</c> for each source ID and
    /// creates <c>compressed_into</c> memory-links from the gist to the
    /// sources for provenance. Compressed records are excluded from
    /// retrieval (see <c>SqliteMemoryService.ComputeRetrievalScore</c> +
    /// search query filters) but preserved in the table for audit/rollback.
    /// </summary>
    Task MarkCompressedAsync(IEnumerable<Guid> sourceIds, Guid gistId, CancellationToken ct = default);
}
