using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// EF Core entity for <c>emotional_state_history</c> — append-only
/// time-series of emotional snapshots, written via the EmotionalProcessor.
/// </summary>
[Table("emotional_state_history")]
public class EmotionalStateHistoryEntity
{
    [Key]
    [Column("id")]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("warmth")]
    public float Warmth { get; set; }

    [Column("energy")]
    public float Energy { get; set; }

    [Column("concern")]
    public float Concern { get; set; }

    [Column("playfulness")]
    public float Playfulness { get; set; }

    [Column("contact_gap_tension")]
    public float ContactGapTension { get; set; }

    [Column("recorded_at")]
    public DateTimeOffset RecordedAt { get; set; }
}
