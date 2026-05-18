using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Outreach;

/// <inheritdoc cref="IOutboundThreadRecorder"/>
public sealed class OutboundThreadRecorder : IOutboundThreadRecorder
{
    private readonly IConversationService _conversations;
    private readonly ILogger<OutboundThreadRecorder> _log;

    public OutboundThreadRecorder(IConversationService conversations, ILogger<OutboundThreadRecorder> log)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _log           = log           ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task RecordAsync(string content, CancellationToken ct)
    {
        try
        {
            var thread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false);
            if (thread is null)
            {
                thread = new ConversationThread
                {
                    InitiatedBy   = Roles.Ani,
                    StartedAt     = DateTimeOffset.UtcNow,
                    LastMessageAt = DateTimeOffset.UtcNow,
                };
                await _conversations.SaveThreadAsync(thread, ct).ConfigureAwait(false);
                _log.LogDebug("Outbound created new conversation thread: {ThreadId}", thread.Id);
            }

            await _conversations.AddMessageAsync(thread.Id, new ConversationMessage
            {
                Role    = Roles.Ani,
                Content = content,
                SentAt  = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to record outbound in conversation thread — dispatch already succeeded, continuing");
        }
    }
}
