using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused contract for the
/// Feature 30 three-tier dedup-merge decision + execution, the Apr 21
/// rumination guard, and the cross-type profile correction. Extracted
/// from <c>SqliteMemoryService.SaveAsync</c> + its private helpers
/// (FindMergeCandidate, MergeMemories, IsRumination, TryCrossTypeProfileCorrection)
/// so the persistence service can own "I save records" without owning the
/// merge-decision policy.
///
/// <para>
/// Three responsibilities:
/// </para>
/// <list type="number">
///   <item><see cref="IsRuminationAsync"/> — Apr 21 cluster-saturation
///     detection in the 0.60-0.85 cosine gap that Feature 30 doesn't
///     cover. Returns true when the incoming record has
///     ≥<c>RuminationClusterMinSize</c> similar records at similarity
///     ≥<c>RuminationSimilarityThreshold</c> within the configured
///     window. Caller should skip the save.</item>
///   <item><see cref="FindMergeCandidateAsync"/> — three-tier cosine
///     classification: ≥0.95 = exact duplicate (skip), 0.85-0.95 =
///     merge candidate (LLM-mediated merge into existing), &lt;0.85 =
///     no match (insert as new).</item>
///   <item><see cref="MergeAsync"/> — LLM-mediated merge of two
///     contents into one consolidated record, with re-embedding and an
///     audit-log entry. Returns the merged content or null on
///     failure.</item>
///   <item><see cref="TryProfileCorrectionAsync"/> — cross-type
///     correction: when a Perception/Episodic record with the contact
///     as speaker semantically matches an existing Semantic "About
///     Mark"/"Shared experience"/"Interest:" profile memory, merges
///     the newer info into the profile.</item>
/// </list>
/// </summary>
public interface IMemoryMergePolicy
{
    Task<bool> IsRuminationAsync(MemoryRecord record, CancellationToken ct = default);

    /// <summary>
    /// Issue #56 (2026-05-22) — exact-content duplicate check across the full
    /// table for dedupable types. Faster and stricter than
    /// <see cref="FindMergeCandidateAsync"/> (which scans only the last 50 records
    /// of the same type via cosine similarity). When the incoming record's
    /// (Type, Content, SourceName) exactly matches an existing row, returns the
    /// existing row's id so the caller can short-circuit the insert.
    ///
    /// <para>Closes the failure mode behind the 5-copy Mia character-seed
    /// records in production: the cosine merge policy's 50-row window can miss
    /// older seeds when later writes have buried them, but exact-string match
    /// on (Type, Content, SourceName) catches re-seed events deterministically
    /// regardless of table size.</para>
    /// </summary>
    Task<Guid?> FindExactContentDuplicateAsync(MemoryRecord record, CancellationToken ct = default);

    Task<MergeCandidate?> FindMergeCandidateAsync(MemoryRecord record, CancellationToken ct = default);

    /// <summary>
    /// Runs an LLM merge of <paramref name="existingContent"/> +
    /// <paramref name="newContent"/>, re-embeds the result, updates the
    /// existing memory row in place, and writes an audit-log entry.
    /// Returns the merged content on success, null on failure.
    ///
    /// <c>occurred_at</c> is intentionally preserved on the existing row
    /// — bumping it on every merge breaks the Park et al. recency model
    /// (Feature 20). The audit log records the merge timestamp if "when
    /// was this merged?" is needed.
    /// </summary>
    Task<string?> MergeAsync(
        Guid existingId, string existingContent, string newContent,
        CancellationToken ct = default);

    Task TryProfileCorrectionAsync(MemoryRecord record, CancellationToken ct = default);
}

/// <summary>
/// Result of a merge-candidate search. <see cref="IsExactDuplicate"/>
/// signals the caller should skip the save entirely; otherwise the
/// caller should LLM-merge the new content into the existing record.
/// </summary>
public sealed record MergeCandidate(Guid ExistingId, string ExistingContent, bool IsExactDuplicate);
