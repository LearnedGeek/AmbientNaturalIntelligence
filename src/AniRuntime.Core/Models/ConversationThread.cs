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

    /// <summary>
    /// Feature 34 (MemGPT): Cached summary of compressed older messages.
    /// Not persisted to DB — regenerated when needed during conversation.
    /// </summary>
    public string? CompressedSummary { get; set; }

    /// <summary>
    /// Feature 34: Number of messages the cached summary covers.
    /// If new messages push beyond this, the summary needs regeneration.
    /// </summary>
    public int CompressedSummaryUpToIndex { get; set; }
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
