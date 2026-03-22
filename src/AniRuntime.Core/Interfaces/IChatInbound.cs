namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Abstraction for injecting inbound messages into the cognitive pipeline.
/// Used by the dashboard chat UI to send messages without depending on
/// the Perception project or Twilio-specific types.
/// </summary>
public interface IChatInbound
{
    /// <summary>
    /// Enqueues a message for processing by the next cognitive cycle.
    /// The message flows through the full pipeline (intent extraction,
    /// memory search, reply generation) just like an SMS would.
    /// </summary>
    void EnqueueMessage(string message);
}
