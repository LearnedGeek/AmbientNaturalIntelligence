using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

public interface IConversationService
{
    Task<ConversationThread?> GetActiveThreadAsync(CancellationToken ct = default);
    Task<ConversationThread?> GetThreadAsync(Guid threadId, CancellationToken ct = default);
    Task<List<ConversationThread>> GetRecentThreadsAsync(int limit = 10, CancellationToken ct = default);
    Task SaveThreadAsync(ConversationThread thread, CancellationToken ct = default);
    Task AddMessageAsync(Guid threadId, ConversationMessage message, CancellationToken ct = default);
    Task CloseThreadAsync(Guid threadId, CancellationToken ct = default);
}
