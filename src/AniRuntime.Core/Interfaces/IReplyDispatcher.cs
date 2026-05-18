using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Dispatches a composed + remediated reply through the originating
/// channel and applies the post-dispatch bookkeeping that always follows:
/// natural-delay pacing, thread persistence, conversation-state update,
/// and the desire-engine reset.
///
/// <para>
/// Extracted from <c>ConversationReplyPipeline</c> in §5.4d SOLID
/// refactor (2026-05-18). Removes three pipeline deps:
/// <see cref="IReplyChannelResolver"/>, <c>DesireEngine</c>, and the
/// dead-injected <c>AniActionDispatcher</c>.
/// </para>
/// </summary>
public interface IReplyDispatcher
{
    /// <summary>
    /// Send the reply, persist it, update conversation state, reset desire.
    /// Applies a randomized natural-feel delay before sending (real people
    /// don't reply in 4 seconds).
    /// </summary>
    Task DispatchAsync(
        string reply,
        ConversationThread thread,
        ConversationMessage replyMessage,
        string contactName,
        string originChannelId,
        CancellationToken ct);
}
