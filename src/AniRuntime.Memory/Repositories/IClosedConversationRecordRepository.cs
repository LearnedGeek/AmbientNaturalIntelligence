using AniRuntime.Memory.Entities;

namespace AniRuntime.Memory.Repositories;

/// <summary>
/// Vibe Loop V1 closed-conversation records — gist + register snapshots +
/// outcome-signal seed produced when a thread is closed. Persistence-only
/// surface here; the V1.2 summarizer remains a separate domain service
/// that produces the entity, then this repo stages it for SaveChangesAsync.
/// </summary>
public interface IClosedConversationRecordRepository
{
    void Add(ClosedConversationRecordEntity record);
    void Update(ClosedConversationRecordEntity record);

    Task<ClosedConversationRecordEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<ClosedConversationRecordEntity?> GetByThreadIdAsync(Guid threadId, CancellationToken ct = default);

    Task<IReadOnlyList<ClosedConversationRecordEntity>> GetRecentAsync(
        int limit, CancellationToken ct = default);
}
