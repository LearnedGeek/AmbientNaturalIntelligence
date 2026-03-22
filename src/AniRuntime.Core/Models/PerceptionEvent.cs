namespace AniRuntime.Core.Models;

public class PerceptionEvent
{
    public string             SourceName    { get; set; } = string.Empty;
    public PerceptionCategory Category      { get; set; }
    public string             Summary       { get; set; } = string.Empty;
    public float              ContactRelevance { get; set; }
    public DateTimeOffset     OccurredAt    { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Identifies the channel this event arrived on (e.g., "sms", "dashboard").
    /// Used by ConversationReplyPhase + IReplyChannelResolver to dispatch replies
    /// through the same channel (SRP: reply generation ≠ delivery).
    /// Null for non-communication events (weather, RSS, time).
    /// </summary>
    public string? OriginChannelId { get; set; }
}

public enum PerceptionCategory
{
    Environment,
    Calendar,
    Content,
    Communication,
    Social
}
