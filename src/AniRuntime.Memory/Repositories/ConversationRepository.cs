using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Memory.Repositories;

public sealed class ConversationRepository : IConversationRepository
{
    private readonly AniDbContext _db;
    public ConversationRepository(AniDbContext db) => _db = db;

    // ── Threads ────────────────────────────────────────────────────────
    public void AddThread(ConversationThreadEntity thread)
        => _db.ConversationThreads.Add(thread);

    public void UpdateThread(ConversationThreadEntity thread)
        => _db.ConversationThreads.Update(thread);

    public Task<ConversationThreadEntity?> GetThreadAsync(
        Guid threadId, CancellationToken ct = default)
    {
        return _db.ConversationThreads.FirstOrDefaultAsync(t => t.Id == threadId, ct);
    }

    public Task<ConversationThreadEntity?> GetActiveThreadAsync(CancellationToken ct = default)
    {
        return _db.ConversationThreads
            .Where(t => t.IsActive)
            .OrderByDescending(t => t.LastMessageAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ConversationThreadEntity>> GetRecentThreadsAsync(
        int limit, CancellationToken ct = default)
    {
        return await _db.ConversationThreads
            .OrderByDescending(t => t.LastMessageAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    // ── Messages ───────────────────────────────────────────────────────
    public void AddMessage(ConversationMessageEntity message)
        => _db.ConversationMessages.Add(message);

    public async Task<IReadOnlyList<ConversationMessageEntity>> GetMessagesAsync(
        Guid threadId, CancellationToken ct = default)
    {
        return await _db.ConversationMessages
            .Where(m => m.ThreadId == threadId)
            .OrderBy(m => m.SentAt)
            .ToListAsync(ct);
    }

    public Task<int> GetMessageCountAsync(Guid threadId, CancellationToken ct = default)
    {
        return _db.ConversationMessages.CountAsync(m => m.ThreadId == threadId, ct);
    }
}
