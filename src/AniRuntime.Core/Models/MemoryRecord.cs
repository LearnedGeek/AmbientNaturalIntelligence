using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Utilities;

namespace AniRuntime.Core.Models;

public class MemoryRecord : IRetrievalEnvelope, IAnchoredMemoryEnvelope, IAttributedContent<MemoryRecord>
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

    // Feature 16: Decay Tier — foundation memories (Anchored) never fade;
    // Standard memories decay normally. Previously named MemoryTier.
    public DecayTier      DecayTier   { get; set; } = DecayTier.Standard;

    /// <summary>
    /// Why this memory is anchored. Should be one of the canonical values
    /// in <see cref="AnchorReasonValues"/> (origin / foundation /
    /// architectural-growth / lessons-as-history). NULL is acceptable for
    /// pre-May-3-2026 records pending curation OR for non-anchored memories
    /// (DecayTier=Standard). Schema is TEXT for forward-compatibility.
    /// </summary>
    public string?        AnchorReason { get; set; }
    public DateTimeOffset? AnchoredAt { get; set; }

    // Epistemic Grounding (Apr 10): Provenance tier controls which retrieval
    // pool this memory belongs to. Facts = grounded truth about Mark/world,
    // Episodic = verbatim conversation record, Interior = Ani's inner life.
    // Orthogonal to DecayTier — a memory can be Anchored+Facts, Standard+Interior, etc.
    public EpistemicTier  Provenance  { get; set; } = EpistemicTier.Episodic;

    // Feature 15: Memory contradiction flagging — when a new memory semantically
    // conflicts with an existing one, both are flagged for manual review
    public Guid?          ContradictsMemoryId { get; set; }
    public string?        ContradictionReason { get; set; }
    public DateTimeOffset? FlaggedAt { get; set; }

    // Issue #93 (2026-07-06): confirmation stamp for the positive half of the
    // substrate-correction loop. NULL = unconfirmed (Interior / world-experience
    // / reflection). Non-null = a real-world event confirmed this content.
    // Facts+Episodic retroactively receive ConfirmedAt=CreatedAt,
    // ConfirmedBy='canonical' at Phase 1 backfill; Mark ///tag intents classified
    // as "confirm" (Phase 2) will set ConfirmedAt=<tag time>, ConfirmedBy='mark-tag'.
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string?         ConfirmedBy { get; set; }

    /// <summary>
    /// Feature 44 Phase I.3 (2026-08-05) — register-family classification for
    /// this record. Populated at write time by the Posture-S+1 hybrid path
    /// (qwen3:14b metadata recognizer emits <c>register</c>) so downstream
    /// retrieval mechanisms (wandering-mind register-diversity slot) can
    /// query "give me a record whose register differs from the current
    /// attractor" without another LLM roundtrip.
    ///
    /// <para>
    /// NULL is expected on:
    /// - all records created before this field shipped (pending backfill),
    /// - records persisted via legacy metadata paths that never emit register,
    /// - Facts / Episodic records where register classification is not applicable.
    /// </para>
    ///
    /// <para>
    /// Values are the free-form register strings that qwen3:14b emits
    /// (e.g. "warmth", "longing", "playfulness"). The
    /// <see cref="ImpactCategoryDefaults.ToRegisterFamily"/> mapping folds
    /// these into the <see cref="RegisterFamily"/> enum for diversity
    /// comparisons; storing the raw string preserves substrate for
    /// forward-compatible taxonomy revisions.
    /// </para>
    /// </summary>
    public string?        Register { get; set; }

    // ── F-2 Phase 1 P2 (2026-08-21) — attribution fields ────────────────
    // Public read/write for producer wiring in P6. Interface members below
    // expose them via IAttributedContent<MemoryRecord> for consumer access.
    // Default values: Unknown/null/null/null/"unverified" — every record
    // should be explicitly attributed at ingest by P6 producers; backfill
    // (P3) populates existing records via heuristic (tier + type + source).

    /// <summary>
    /// Who authored the content within this record. Default
    /// <see cref="Interfaces.AttributedTo.Unknown"/>. See
    /// <see cref="IAttributedContent{T}.AttributedTo"/> XML doc for the
    /// full authorship-vs-provenance distinction.
    /// </summary>
    public AttributedTo AttributedTo { get; set; } = AttributedTo.Unknown;

    /// <summary>When the utterance happened (UTC). Null for canonical/timeless attribution.</summary>
    public DateTimeOffset? AttributedAt { get; set; }

    /// <summary>FK to source <c>MemoryRecord.Id</c> when source is another persisted record.</summary>
    public Guid? AttributedSourceRecordId { get; set; }

    /// <summary>Free-text descriptor for ephemeral / non-record sources.</summary>
    public string? AttributedSourceDescriptor { get; set; }

    /// <summary>
    /// Verification state — <c>"verified"</c> / <c>"unverified"</c> /
    /// <c>"unverified-historical"</c>. Default <c>"unverified"</c>; new
    /// records set to <c>"verified"</c> at producer wiring in P6; backfill
    /// (P3) marks historical Interior records as <c>"unverified-historical"</c>.
    /// </summary>
    public string AttributionTrust { get; set; } = "unverified";

    // ── IRetrievalEnvelope / IProvenancedContent<MemoryRecord> ─────────────
    // F-1 Phase 5 (2026-08-18). All fields already exist on MemoryRecord —
    // this is pure interface implementation with zero new persisted state.
    // Because MemoryRecord is a class (not record) there is no equality-
    // behavior change to worry about here (Phase 4 reviewer-caught the
    // record-equality change on ConsciousSubstrateGist; classes are exempt).

    /// <inheritdoc />
    /// <remarks>
    /// The envelope's Content is the whole MemoryRecord (self-reference)
    /// so consumers that need the raw <see cref="Content"/> text OR the
    /// temporal <see cref="OccurredAt"/> OR the raw metadata can read them
    /// directly from the wrapped payload. Not a string wrapping the
    /// content field alone.
    /// </remarks>
    MemoryRecord IProvenancedContent<MemoryRecord>.Content => this;

    /// <inheritdoc />
    /// <remarks>
    /// Composed from <see cref="Provenance"/> + <see cref="SourceName"/>
    /// so the dotted-lowercase tag identifies both the epistemic tier and
    /// the producer that emitted the underlying record
    /// (e.g. <c>"retrieval.facts.rss"</c>, <c>"retrieval.interior.ani"</c>,
    /// <c>"retrieval.episodic.mark"</c>). SourceName defaults to
    /// <c>"unknown"</c> when null — most pre-Phase-6 records have null
    /// SourceName, so downstream aggregation shouldn't crash on that case.
    /// </remarks>
    string IProvenancedContent<MemoryRecord>.SourceType
        => $"retrieval.{Provenance.ToString().ToLowerInvariant()}.{(SourceName ?? "unknown").ToLowerInvariant()}";

    /// <inheritdoc />
    string IProvenancedContent<MemoryRecord>.Producer => "RetrievalContextBuilder";

    /// <inheritdoc />
    /// <remarks>
    /// Uses the existing <see cref="CreatedAt"/> field which is set at
    /// record construction and persisted to the DB. Mutable per the
    /// pre-Phase-5 record contract (EF write path sets it), but treated
    /// as stable-point-in-time by all readers.
    /// </remarks>
    DateTimeOffset IProvenancedContent<MemoryRecord>.CreatedAt => CreatedAt;

    /// <inheritdoc />
    /// <remarks>
    /// Reuses the existing <see cref="Embedding"/> field so callers doing
    /// envelope-based semantic dedup (Phase 2 pattern) can pool retrieval
    /// envelopes with active-trigger envelopes on the same cosine surface.
    /// </remarks>
    float[]? IProvenancedContent<MemoryRecord>.SemanticKey => Embedding;

    /// <inheritdoc />
    EpistemicTier IRetrievalEnvelope.Provenance => Provenance;

    // ── IAnchoredMemoryEnvelope ────────────────────────────────────────────
    // F-1 Phase 7 (2026-08-19). Voice derived from Provenance + content
    // signals; no schema change, no LLM call. Called only for records in
    // the AnchoredMemories pool (DecayTier=Anchored), but returning a
    // classification for any MemoryRecord is harmless — non-anchored
    // consumers ignore the property.

    /// <inheritdoc />
    AnchoredMemoryVoice IAnchoredMemoryEnvelope.Voice
        => AnchoredMemoryVoiceClassifier.Classify(this);

    // ── IAttributedContent<MemoryRecord> ────────────────────────────────
    // F-2 Phase 1 P2 (2026-08-21). Explicit-interface members mirror the
    // Phase 5 IRetrievalEnvelope pattern — public read/write fields above
    // are the ingest / mutation surface; interface members are the
    // read-only consumer surface. Content is self-reference (whole record
    // is the wrapped payload) matching the IRetrievalEnvelope shape.

    /// <inheritdoc />
    MemoryRecord IAttributedContent<MemoryRecord>.Content => this;

    /// <inheritdoc />
    AttributedTo IAttributedContent<MemoryRecord>.AttributedTo => AttributedTo;

    /// <inheritdoc />
    DateTimeOffset? IAttributedContent<MemoryRecord>.AttributedAt => AttributedAt;

    /// <inheritdoc />
    Guid? IAttributedContent<MemoryRecord>.AttributedSourceRecordId => AttributedSourceRecordId;

    /// <inheritdoc />
    string? IAttributedContent<MemoryRecord>.AttributedSourceDescriptor => AttributedSourceDescriptor;

    /// <inheritdoc />
    string IAttributedContent<MemoryRecord>.AttributionTrust => AttributionTrust;
}

/// <summary>
/// Feature 15: A flagged contradiction pair — two memories that semantically conflict.
/// Surfaced in the dashboard for manual review rather than auto-resolved.
/// </summary>
public class MemoryContradiction
{
    public Guid NewMemoryId { get; set; }
    public Guid ExistingMemoryId { get; set; }
    public string NewContent { get; set; } = string.Empty;
    public string ExistingContent { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public float Similarity { get; set; }
    public DateTimeOffset FlaggedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsResolved { get; set; }
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

/// <summary>
/// Feature 16: Controls memory decay behavior.
/// Previously named MemoryTier — renamed Apr 10, 2026 when EpistemicTier was
/// introduced for the tier separation architecture, so the two orthogonal
/// tier concepts are distinguishable.
/// </summary>
public enum DecayTier
{
    Standard,    // Normal memories — importance scoring + decay apply normally
    Anchored,    // Foundation memories — decay disabled, always included in context
    Compressed,  // Phase 6 v1.2 (May 17, 2026) — record was folded into a
                 // reflection gist and is excluded from retrieval. The
                 // record itself is preserved for audit/rollback but no
                 // longer surfaces in conversation context. Provenance from
                 // the gist back to compressed sources is maintained via the
                 // memory_links table with relationship type
                 // 'compressed_into'. See docs/spec/design/ANI-MemoryReform-Design.md
                 // v1.2 Refinement 2 (lifecycle: gist replaces sources).
}

/// <summary>
/// Epistemic Grounding (Apr 10, 2026): Controls which retrieval pool a memory
/// belongs to and how it renders in the generation prompt. The three tiers are
/// orthogonal to DecayTier.
///
/// Facts: "What is true about Mark and the world" — character seeds, perception
///   events, user-asserted content. The ONLY tier that grounds factual assertions.
///   Cannot be populated by Ani's generated content.
///
/// Episodic: "What was said" — verbatim conversation record (both sides), with
///   attribution and timestamps. Retrieved for conversation continuity, never
///   treated as factual grounding.
///
/// Interior: "Who you are and what you feel" — inner thoughts, world-experience
///   reflections, self-concept, mood, associations, Ani's interpretations of Mark.
///   Full creative latitude. Structurally isolated from the fact pool.
///
/// See docs/spec/design/ANI-Epistemic-Grounding-Architecture.md for full design.
/// </summary>
public enum EpistemicTier
{
    Facts,      // External truth — character seeds, perception, user-asserted
    Episodic,   // Verbatim conversation record
    Interior    // Ani's inner life — reflections, mood, interpretations
}
