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
