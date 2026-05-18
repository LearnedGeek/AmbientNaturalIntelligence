using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// AC5 confabulation feedback. Stores flagged responses for pattern
/// analysis. Mark tags messages via the <c>///flag</c> command at runtime;
/// this is where they land.
/// </summary>
[Table("confabulation_flags")]
public class ConfabulationFlagEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("flagged_at")]
    public DateTimeOffset FlaggedAt { get; set; }

    [Column("contact_message")]
    public string ContactMessage { get; set; } = string.Empty;

    [Column("ani_reply")]
    public string AniReply { get; set; } = string.Empty;

    [Column("topic_category")]
    public string? TopicCategory { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("canonical_category")]
    public string? CanonicalCategory { get; set; }
}
