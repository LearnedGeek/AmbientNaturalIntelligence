namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Abstraction for delivering a reply to the user via the originating channel.
/// SMS, dashboard, voice — each implements this interface.
/// ConversationReplyPhase dispatches through the channel without knowing
/// the transport mechanism (SRP: reply generation ≠ reply delivery).
/// </summary>
public interface IReplyChannel
{
    /// <summary>
    /// Identifies this channel (e.g., "sms", "dashboard", "voice").
    /// Used to match inbound messages to the correct reply path.
    /// </summary>
    string ChannelId { get; }

    /// <summary>
    /// Delivers a reply message to the user via this channel.
    /// For SMS: sends via Twilio. For dashboard: no-op (UI polls the thread).
    /// Media attachments (voice, image) are handled by enrichment before this call.
    /// </summary>
    Task SendReplyAsync(string message, CancellationToken ct = default);
}
