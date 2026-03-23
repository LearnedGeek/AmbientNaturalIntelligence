using AniRuntime.Core;

namespace AniRuntime.Core.Models;

public class ConversationThread
{
    public Guid                      Id            { get; set; } = Guid.NewGuid();
    public DateTimeOffset            StartedAt     { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset            LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
    public bool                      IsActive      { get; set; } = true;
    public string                    InitiatedBy   { get; set; } = Roles.Mark; // "ani" | "mark"
    public List<ConversationMessage> Messages      { get; set; } = new();
}

public class ConversationMessage
{
    public string         Role    { get; set; } = Roles.Mark; // "ani" | "mark"
    public string         Content { get; set; } = string.Empty;
    public DateTimeOffset SentAt  { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Cached embedding for echo guard — computed once, reused across checks.
    /// Not persisted to DB; populated lazily during conversation reply processing.
    /// </summary>
    public float[]? CachedEmbedding { get; set; }
}
