namespace AniRuntime.Core.Models;

/// <summary>
/// A directional link between two memories in the memory graph.
/// Created by Feature 31 (A-MEM linked memory graph) at save time.
/// </summary>
public class MemoryLink
{
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public string Relationship { get; set; } = "relates_to";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
