namespace AniRuntime.Core.Models;

/// <summary>
/// A single entry in the memory audit log. Tracks every create, update,
/// delete, and merge operation on the memories table for rollback capability.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }
    public string MemoryId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;     // create, update, delete, merge
    public string Source { get; set; } = string.Empty;      // cognitive-cycle, auto-corrector, merge, manual
    public string? ContentBefore { get; set; }
    public string? ContentAfter { get; set; }
    public int? TypeBefore { get; set; }
    public int? TypeAfter { get; set; }
    public float? ImportanceBefore { get; set; }
    public float? ImportanceAfter { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
