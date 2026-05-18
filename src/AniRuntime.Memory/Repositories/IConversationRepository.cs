using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>
/// Conversation threads + messages. One repository covers both because the
/// service-layer flows (open thread, append message + bump last_message_at,
/// close thread) need both in the same transaction.
///
/// Add* methods stage rows on the tracked context; the caller
/// (<c>EfConversationService</c> or another orchestrator) commits via
/// <c>AniDbContext.SaveChangesAsync</c> so a message append + thread bump
/// land as one Unit of Work.
/// </summary>
public interface IConversationRepository
{
    // ── Threads ────────────────────────────────────────────────────────
    void AddThread(ConversationThreadEntity thread);
    void UpdateThread(ConversationThreadEntity thread);

    Task<ConversationThreadEntity?> GetThreadAsync(Guid threadId, CancellationToken ct = default);

    Task<ConversationThreadEntity?> GetActiveThreadAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ConversationThreadEntity>> GetRecentThreadsAsync(
        int limit, CancellationToken ct = default);

    // ── Messages ───────────────────────────────────────────────────────
    void AddMessage(ConversationMessageEntity message);

    Task<IReadOnlyList<ConversationMessageEntity>> GetMessagesAsync(
        Guid threadId, CancellationToken ct = default);

    Task<int> GetMessageCountAsync(Guid threadId, CancellationToken ct = default);
}
