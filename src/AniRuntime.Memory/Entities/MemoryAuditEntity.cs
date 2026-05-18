using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// Memory audit log — tracks every create/update/delete/merge for
/// rollback capability. Added Apr 5, 2026 after the auto-corrector
/// deleted 128 valid memories with no recovery.
/// </summary>
[Table("memory_audit")]
public class MemoryAuditEntity
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("memory_id")]
    public Guid MemoryId { get; set; }

    /// <summary>'create' | 'update' | 'delete' | 'merge'</summary>
    [Column("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>'cognitive-cycle' | 'auto-corrector' | 'merge' | 'manual' | 'import'</summary>
    [Column("source")]
    public string Source { get; set; } = string.Empty;

    [Column("content_before")]
    public string? ContentBefore { get; set; }

    [Column("content_after")]
    public string? ContentAfter { get; set; }

    [Column("type_before")]
    public int? TypeBefore { get; set; }

    [Column("type_after")]
    public int? TypeAfter { get; set; }

    [Column("importance_before")]
    public float? ImportanceBefore { get; set; }

    [Column("importance_after")]
    public float? ImportanceAfter { get; set; }

    [Column("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }
}
