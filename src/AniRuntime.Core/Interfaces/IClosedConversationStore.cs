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
    /// not found.
    /// </summary>
    Task<ClosedConversationRecord?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Look up a closed-conversation record by the conversation_threads.id
    /// it was generated from. There is at most one record per thread (one
    /// close event); returns null if the thread has not been closed yet
    /// or pre-dates Vibe Loop V1.
    /// </summary>
    Task<ClosedConversationRecord?> GetByThreadIdAsync(Guid threadId, CancellationToken ct = default);

    /// <summary>
    /// Most recent N closed-conversation records, ordered by
    /// <see cref="ClosedConversationRecord.ClosedAt"/> descending. Used by
    /// outreach prompt composition (V1.4) to render the gist of the most
    /// recent closed conversation when no active thread exists.
    /// </summary>
    Task<IEnumerable<ClosedConversationRecord>> GetRecentAsync(int limit = 10, CancellationToken ct = default);
}
