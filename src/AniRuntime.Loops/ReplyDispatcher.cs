using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <inheritdoc cref="IReplyDispatcher"/>
public sealed class ReplyDispatcher : IReplyDispatcher
{
    private readonly IReplyChannelResolver _channels;
    private readonly IConversationService _conversations;
    private readonly DesireEngine _desire;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<ReplyDispatcher> _log;

    public ReplyDispatcher(
        IReplyChannelResolver channels,
        IConversationService conversations,
        DesireEngine desire,
        IOptions<AniOptions> aniOptions,
        ILogger<ReplyDispatcher> log)
    {
        _channels      = channels      ?? throw new ArgumentNullException(nameof(channels));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _desire        = desire        ?? throw new ArgumentNullException(nameof(desire));
        _aniOptions    = aniOptions.Value;
        _log           = log           ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task DispatchAsync(
        string reply,
        ConversationThread thread,
        ConversationMessage replyMessage,
        string contactName,
        string originChannelId,
        CancellationToken ct)
    {
        // Step 3: Natural reply delay — real people don't reply in 4 seconds.
        var minDelay = _aniOptions.ConversationMinReplySeconds;
        var maxDelay = _aniOptions.ConversationMaxReplySeconds;
        var elapsed = (DateTimeOffset.UtcNow - thread.Messages[^1].SentAt).TotalSeconds;
        var targetDelay = minDelay + Random.Shared.NextDouble() * (maxDelay - minDelay);
        var remaining = targetDelay - elapsed;
        if (remaining > 0)
        {
            _log.LogDebug("Waiting {Seconds:F0}s before replying (natural delay)", remaining);
            await Task.Delay(TimeSpan.FromSeconds(remaining), ct).ConfigureAwait(false);
        }

        // Step 4: Dispatch reply via originating channel (SRP: reply generation ≠ delivery).
        var channel = _channels.Resolve(originChannelId);
        await channel.SendReplyAsync(reply, ct).ConfigureAwait(false);

        // Phase 3: Update structured conversation state from Ani's reply
        thread.State.UpdateFromMessage(reply, Roles.Ani, contactName);

        // Step 5: Persist Ani's reply to DB (already added to in-memory thread before echo guard).
        // Update content in case echo guard / remediation replaced it.
        replyMessage.Content = reply;
        await _conversations.AddMessageAsync(thread.Id, replyMessage, ct).ConfigureAwait(false);

        // Update desire — conversation reply doesn't count toward daily outreach limit.
        await _desire.ResetAfterConversationReplyAsync(ct).ConfigureAwait(false);
    }
}
