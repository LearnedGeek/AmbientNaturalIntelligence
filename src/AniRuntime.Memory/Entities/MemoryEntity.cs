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

    /// <summary>
    /// Feature 44 Phase I.3 (2026-08-05) — register-family label emitted by
    /// the qwen3:14b metadata recognizer at InnerThought write time.
    /// Populated forward; NULL on pre-shipping records pending backfill.
    /// Storage is free-form TEXT so taxonomy revisions don't require a
    /// schema migration — the enum mapping lives in
    /// <see cref="AniRuntime.Core.Models.ImpactCategoryDefaults.ToRegisterFamily"/>.
    /// </summary>
    [Column("register")]
    public string? Register { get; set; }

    // ── F-2 Phase 1 P2 (2026-08-21) — attribution columns ─────────────────
    // Five new fields making attribution first-class per the F-2 Phase 1
    // design plan. Populated at ingest by P6 producer wiring; existing
    // records get heuristic backfill in P3. Zero-risk additive migration —
    // all columns nullable or with default; existing reads unaffected.

    /// <summary>
    /// F-2 Phase 1 (2026-08-21) — who authored the content within this
    /// record. Default <c>Unknown</c> (int 0) — every record should be
    /// explicitly attributed at ingest by P6 producer wiring. Backfill (P3)
    /// populates existing records via heuristic table (tier + type + source).
    /// See <see cref="AniRuntime.Core.Interfaces.AttributedTo"/>.
    /// </summary>
    [Column("attributed_to")]
    public AniRuntime.Core.Interfaces.AttributedTo AttributedTo { get; set; }
        = AniRuntime.Core.Interfaces.AttributedTo.Unknown;

    /// <summary>
    /// F-2 Phase 1 — when the utterance happened (UTC). Null for
    /// canonical/timeless attribution (character-seed content — no specific
    /// utterance moment). Distinct from <see cref="OccurredAt"/> which is
    /// the record's event-time, not the utterer's speaking time.
    /// </summary>
    [Column("attributed_at")]
    public DateTimeOffset? AttributedAt { get; set; }

    /// <summary>
    /// F-2 Phase 1 — FK to the source <see cref="Id"/> when the source is
    /// another persisted <c>memories</c> row (e.g. a reflection synthesized
    /// from earlier memories references its source records). Null when the
    /// source is ephemeral (chat-history turn, live inbound event) — see
    /// <see cref="AttributedSourceDescriptor"/>.
    /// </summary>
    [Column("attributed_source_id")]
    public Guid? AttributedSourceRecordId { get; set; }

    /// <summary>
    /// F-2 Phase 1 — free-text descriptor for ephemeral / non-record sources
    /// (e.g. <c>"twilio-inbound:SM&lt;sid&gt;"</c>,
    /// <c>"character-seed:mark.profile"</c>,
    /// <c>"chat-history-turn:2026-08-20T16:29"</c>).
    /// Convention: <see cref="AttributedSourceRecordId"/> and this field
    /// should not both be non-null; producers pick one shape per record.
    /// </summary>
    [Column("attributed_source_desc")]
    public string? AttributedSourceDescriptor { get; set; }

    /// <summary>
    /// F-2 Phase 1 — verification state of the attribution.
    /// <list type="bullet">
    ///   <item><c>"verified"</c> — trusted (canonical source or FK/descriptor
    ///     resolves to a known utterance)</item>
    ///   <item><c>"unverified"</c> — inferred, source cannot be checked</item>
    ///   <item><c>"unverified-historical"</c> — pre-F-2 backfilled record;
    ///     internal content claims cannot be retroactively verified. Surfaced
    ///     at retrieval-render time so composer LLMs weight accordingly.</item>
    /// </list>
    /// String rather than enum for forward-compat with future trust categories
    /// without schema migration.
    /// </summary>
    [Column("attribution_trust")]
    public string AttributionTrust { get; set; } = "unverified";
}
