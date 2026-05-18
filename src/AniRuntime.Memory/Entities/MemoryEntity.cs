using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AniRuntime.Core.Models;

namespace AniRuntime.Memory.Entities;

/// <summary>
/// EF Core entity for the <c>memories</c> table — the main substrate store.
/// Mirrors the existing schema exactly so EF can read existing production
/// data without migration. Value conversions for Guid↔TEXT, float[]↔BLOB,
/// DateTimeOffset↔ISO 8601 TEXT, and enum↔TEXT are configured in
/// <see cref="AniDbContext"/>.
/// </summary>
[Table("memories")]
public class MemoryEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("type")]
    public MemoryType Type { get; set; }

    [Column("content")]
    public string Content { get; set; } = string.Empty;

    [Column("raw_json")]
    public string? RawJson { get; set; }

    [Column("importance")]
    public float Importance { get; set; }

    [Column("relational_valence")]
    public float RelationalValence { get; set; }

    [Column("embedding")]
    public float[]? Embedding { get; set; }

    [Column("is_resolved")]
    public bool IsResolved { get; set; }

    [Column("source_name")]
    public string? SourceName { get; set; }

    [Column("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("resolved_at")]
    public DateTimeOffset? ResolvedAt { get; set; }

    [Column("tier")]
    public DecayTier Tier { get; set; } = DecayTier.Standard;

    [Column("anchor_reason")]
    public string? AnchorReason { get; set; }

    [Column("anchored_at")]
    public DateTimeOffset? AnchoredAt { get; set; }

    [Column("provenance")]
    public EpistemicTier Provenance { get; set; } = EpistemicTier.Episodic;
}
