using AniRuntime.Core.Interfaces;

namespace AniRuntime.Eval;

/// <summary>
/// <see cref="IReplyChannel"/> that captures replies in-memory instead of
/// dispatching them via SMS or any external transport. Singleton — the
/// captured-replies list is read by the eval driver after the cognitive
/// cycle completes.
///
/// <para>
/// Combined with <see cref="CapturingReplyChannelResolver"/>, this
/// completely intercepts the reply-delivery boundary: the production
/// pipeline runs unchanged through composition + invariants + verifier +
/// dispatcher, but the final SMS send is replaced with an in-memory
/// capture. No Twilio API calls, no real-world side effects.
/// </para>
/// </summary>
public sealed class CapturingReplyChannel : IReplyChannel
{
    public string ChannelId => "eval-capture";

    public List<CapturedReply> CapturedReplies { get; } = new();

    public Task SendReplyAsync(string message, CancellationToken ct = default)
    {
        CapturedReplies.Add(new CapturedReply(message, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}

public sealed record CapturedReply(string Message, DateTimeOffset CapturedAt);
