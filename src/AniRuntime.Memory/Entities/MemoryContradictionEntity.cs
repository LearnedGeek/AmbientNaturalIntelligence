using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// Feature 15: Memory contradiction flagging. Composite primary key on
/// (NewMemoryId, ExistingMemoryId). Configured in
/// <see cref="AniDbContext.OnModelCreating"/>.
/// </summary>
[Table("memory_contradictions")]
public class MemoryContradictionEntity
{
    [Column("new_memory_id")]
    public Guid NewMemoryId { get; set; }

    [Column("existing_memory_id")]
    public Guid ExistingMemoryId { get; set; }

    [Column("reason")]
    public string Reason { get; set; } = string.Empty;

    [Column("similarity")]
    public float Similarity { get; set; }

    [Column("flagged_at")]
    public DateTimeOffset FlaggedAt { get; set; }

    [Column("is_resolved")]
    public bool IsResolved { get; set; }
}
