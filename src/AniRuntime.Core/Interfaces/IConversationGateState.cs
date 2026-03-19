namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Shared state for conversation reply gating — tracks whether the most recent
/// inbound message has already been evaluated for a reply decision.
///
/// Used by ConversationReplyPhase (writes) and AniHeartbeatService (reads)
/// to coordinate silence reconsideration without direct coupling.
/// </summary>
public interface IConversationGateState
{
    /// <summary>
    /// The SentAt timestamp of the last inbound message that was evaluated
    /// for a reply decision. Null if no message has been evaluated yet
    /// or if the last evaluation resulted in a reply (gate cleared).
    /// </summary>
    DateTimeOffset? LastEvaluatedMessageAt { get; set; }
}
