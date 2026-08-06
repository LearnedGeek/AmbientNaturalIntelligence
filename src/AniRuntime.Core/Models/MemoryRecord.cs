namespace AniRuntime.Core.Models;

public class MemoryRecord
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
