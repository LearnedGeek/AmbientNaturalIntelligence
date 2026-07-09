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

    /// <summary>
    /// Issue #62 (2026-05-23) — substrate-correction propagation for `///tag`
    /// walk-back. Values: <c>"valid"</c> (default, surfaces in retrieval) or
    /// <c>"invalid_confabulation"</c> (excluded from default retrieval; preserved
    /// for audit/paper-figure comparison). Mirrors the J.5h <c>validity</c>
    /// column pattern on <c>closed_conversation_records</c>.
    /// </summary>
    [Column("validity")]
    public string Validity { get; set; } = "valid";

    /// <summary>
    /// Issue #93 (2026-07-06) — confirmation timestamp for the positive half of
    /// the substrate-correction loop. <c>NULL</c> = unconfirmed (Interior /
    /// world-experience / reflection). Non-null = a real-world event confirmed
    /// this record's content. Facts + Episodic backfill on 2026-07-06 sets
    /// this to <see cref="CreatedAt"/> with <see cref="ConfirmedBy"/>=<c>"canonical"</c>.
    /// Retrieval bias in <c>EfSemanticSearchComposer.ComputeRetrievalScore</c>
    /// multiplies the composite by <c>(1 + AniOptions.RetrievalConfirmationBoost)</c>
    /// when this is set, so confirmed content beats semantically-similar-but-
    /// unconfirmed Interior content on the same query.
    /// </summary>
    [Column("confirmed_at")]
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>
    /// Companion to <see cref="ConfirmedAt"/>. Canonical values:
    /// <list type="bullet">
    ///   <item><c>"canonical"</c> — backfilled from provenance (Facts + Episodic
    ///     get birth-stamp confirmation).</item>
    ///   <item><c>"mark-tag"</c> — LLM-classified Mark <c>///tag</c> confirmation
    ///     (Issue #93 Phase 2).</item>
    /// </list>
    /// NULL when <see cref="ConfirmedAt"/> is NULL.
    /// </summary>
    [Column("confirmed_by")]
    public string? ConfirmedBy { get; set; }
}
