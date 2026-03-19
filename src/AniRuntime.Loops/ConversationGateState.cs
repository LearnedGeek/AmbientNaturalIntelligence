using AniRuntime.Core.Interfaces;

namespace AniRuntime.Loops;

/// <summary>
/// Thread-safe singleton holding conversation gate state.
/// Registered as a singleton in DI — both ConversationReplyPhase
/// and AniHeartbeatService depend on IConversationGateState.
/// </summary>
public class ConversationGateState : IConversationGateState
{
    private DateTimeOffset? _lastEvaluatedMessageAt;
    private readonly object _lock = new();

    public DateTimeOffset? LastEvaluatedMessageAt
    {
        get { lock (_lock) return _lastEvaluatedMessageAt; }
        set { lock (_lock) _lastEvaluatedMessageAt = value; }
    }
}
