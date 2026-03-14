namespace AniRuntime.Core.Models;

public class MemoryRecord
{
    public Guid           Id          { get; set; } = Guid.NewGuid();
    public MemoryType     Type        { get; set; }
    public string         Content     { get; set; } = string.Empty;
    public string?        RawJson     { get; set; }
    public float          Importance  { get; set; }
    public float          RelationalValence { get; set; }
    public float[]?       Embedding   { get; set; }
    public bool           IsResolved  { get; set; }
    public string?        SourceName  { get; set; }
    public DateTimeOffset OccurredAt  { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt   { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }

    // Feature 16: Anchored Memory Tier — foundation memories that never fade
    public MemoryTier     Tier        { get; set; } = MemoryTier.Standard;
    public string?        AnchorReason { get; set; }
    public DateTimeOffset? AnchoredAt { get; set; }

    // Feature 15: Memory contradiction flagging — when a new memory semantically
    // conflicts with an existing one, both are flagged for manual review
    public Guid?          ContradictsMemoryId { get; set; }
    public string?        ContradictionReason { get; set; }
    public DateTimeOffset? FlaggedAt { get; set; }
}

/// <summary>
/// Feature 15: A flagged contradiction pair — two memories that semantically conflict.
/// Surfaced in the dashboard for manual review rather than auto-resolved.
/// </summary>
public class MemoryContradiction
{
    public Guid NewMemoryId { get; set; }
    public Guid ExistingMemoryId { get; set; }
    public string NewContent { get; set; } = string.Empty;
    public string ExistingContent { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public float Similarity { get; set; }
    public DateTimeOffset FlaggedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsResolved { get; set; }
}

public enum MemoryType
{
    Episodic,
    Semantic,
    OpenLoop,
    Commitment,
    InnerThought,
    Perception
}

public enum MemoryTier
{
    Standard,   // Normal memories — importance scoring + decay apply normally
    Anchored    // Foundation memories — decay disabled, always included in context
}
