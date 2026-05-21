using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Vibe Loop V1 (Apr 29, 2026) — read/write surface for
/// <see cref="ClosedConversationRecord"/> instances. Narrow interface
/// per the Mar 19 ISP split discipline; doesn't bloat
/// <c>IConversationService</c> (active-thread concern) with closed-record
/// concerns.
///
/// V1.1 ships write + basic read (by id, by thread, recent N). V1.5 will
/// add valence-sorted retrieval and similarity search; those go on a
/// dedicated method when the bias function actually needs them, not
/// pre-emptively.
/// </summary>
public interface IClosedConversationStore
{
    /// <summary>
    /// Persist a closed-conversation record. Idempotent on
    /// <see cref="ClosedConversationRecord.Id"/> — re-saving with the same
    /// id replaces the existing row (UPSERT semantics).
    /// </summary>
    Task SaveAsync(ClosedConversationRecord record, CancellationToken ct = default);

    /// <summary>
    /// Look up a single closed-conversation record by its id, or null if
    /// not found. Default-filters to <c>validity = 'valid'</c> (J.5h-data,
    /// Issue #47) so substrate consumers never see quarantined records.
    /// For audit / paper-figure access to quarantined records, use
    /// <see cref="GetByIdIncludingInvalidAsync"/>.
    /// </summary>
    Task<ClosedConversationRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Audit-path lookup that bypasses the validity filter. Returns
    /// quarantined records too. J.5h-data (Issue #47) consumer surface for
    /// re-summarization workflows and paper-figure broken-vs-fixed
    /// comparisons. Not used by V1.5 bias, outreach grounding, or any
    /// other substrate consumer.
    /// </summary>
    Task<ClosedConversationRecord?> GetByIdIncludingInvalidAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Look up a closed-conversation record by the conversation_threads.id
    /// it was generated from. There is at most one record per thread (one
    /// close event); returns null if the thread has not been closed yet
    /// or pre-dates Vibe Loop V1. Default-filters to <c>validity = 'valid'</c>.
    /// </summary>
    Task<ClosedConversationRecord?> GetByThreadIdAsync(Guid threadId, CancellationToken ct = default);

    /// <summary>Audit-path companion to <see cref="GetByThreadIdAsync"/>; bypasses validity filter.</summary>
    Task<ClosedConversationRecord?> GetByThreadIdIncludingInvalidAsync(Guid threadId, CancellationToken ct = default);

    /// <summary>
    /// Most recent N closed-conversation records, ordered by
    /// <see cref="ClosedConversationRecord.ClosedAt"/> descending. Used by
    /// outreach prompt composition (V1.4) to render the gist of the most
    /// recent closed conversation when no active thread exists.
    /// Default-filters to <c>validity = 'valid'</c>.
    /// </summary>
    Task<IEnumerable<ClosedConversationRecord>> GetRecentAsync(int limit = 10, CancellationToken ct = default);

    /// <summary>Audit-path companion to <see cref="GetRecentAsync"/>; bypasses validity filter.</summary>
    Task<IEnumerable<ClosedConversationRecord>> GetRecentIncludingInvalidAsync(int limit = 10, CancellationToken ct = default);
}
