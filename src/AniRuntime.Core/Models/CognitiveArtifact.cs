namespace AniRuntime.Core.Models;

/// <summary>
/// Theme J Phase J.4 (Apr 30, 2026) — the unit of cognitive output that
/// flows through <see cref="Interfaces.ICognitiveOutputGate"/>.
///
/// Carries the produced text plus enough metadata for invariants to
/// type-condition their applicability. Each producer migrating through
/// J.5 builds a <see cref="CognitiveArtifact"/> at its output boundary
/// and hands it to the gate; the gate dispatches type-applicable
/// invariants and returns a verdict.
///
/// **Why a sealed class with init-only properties** rather than a
/// record: the optional-context surface is intentionally additive — new
/// invariants can read additional context fields without breaking
/// callers, and init-only enforces immutability without requiring
/// every property at construction time.
/// </summary>
public sealed class CognitiveArtifact
{
    /// <summary>The produced text the gate is evaluating.</summary>
    public required string Content { get; init; }

    /// <summary>Which producer pipeline emitted this artifact.</summary>
    public required CognitiveProducerKind ProducerKind { get; init; }

    /// <summary>Where the artifact is headed once gated.</summary>
    public required CognitiveOutputSink IntendedSink { get; init; }

    /// <summary>The contact's display name (defaults to "Mark").</summary>
    public string ContactName { get; init; } = Roles.Mark;

    /// <summary>
    /// Walltime when the output was generated. Used by temporal-attribution
    /// invariants and for log correlation. Default = UtcNow at construction.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    // ── Optional context — invariants read what they need ──────────────────

    /// <summary>
    /// Recent messages from the contact (verbatim, in chronological order).
    /// Anti-parrot invariant uses this to detect 7+ token verbatim lifts.
    /// Null = invariant cannot run (skipped, not failed).
    /// </summary>
    public IReadOnlyList<string>? ContactRecentMessages { get; init; }

    /// <summary>
    /// Recent prior outputs by Ani. Echo-guard invariant uses this to
    /// detect cross-cycle output duplication.
    /// </summary>
    public IReadOnlyList<string>? PriorAniMessages { get; init; }

    /// <summary>
    /// The system prompt text the producer used to generate this output.
    /// Prompt-template-leak invariant uses this to check for directive-
    /// phrase paraphrasing, but the invariant also has a fallback
    /// fixed-pattern list so it can run with this null.
    /// </summary>
    public string? SystemPromptText { get; init; }

    /// <summary>
    /// May 2, 2026 (Door C universalization) — the inner-thought / inner-
    /// monologue that produced this output, when applicable. Used by
    /// <c>InnerThoughtBleedInvariant</c> to detect when a contact-facing
    /// message *only makes sense if you'd seen the writer's inner thought*.
    /// Outreach has this naturally (the recent inner thought drove the
    /// outreach decision); conversation reply and voice typically don't
    /// (the conversation history drove the reply, not an inner thought).
    /// Null = run the bleed invariant with reader-perspective sensibility
    /// only, no inner-thought comparison.
    /// </summary>
    public string? WriterInnerThought { get; init; }

    /// <summary>
    /// Door B Truth-Verification, Sub-claim 4 (May 3, 2026) — canonical
    /// addressee names recognized as legitimate greeting targets. Used by
    /// <c>AddresseeNameInvariant</c> to fail outputs that greet someone
    /// the system can't identify (e.g. May 3 10:55 "hey perez…" fabricated
    /// a Spanish-flavored nickname out of substrate priming on Mark's
    /// Spanish-class context). Producers populate this from
    /// <c>CharacterStateDoc.PrimaryContactName</c> plus any canonical
    /// nicknames known to be legitimate (real friends/family from
    /// character seeds, established pet-names from prior sessions). The
    /// invariant ALSO accepts <see cref="ContactName"/> implicitly as a
    /// belt-and-suspenders fallback. Null = the invariant runs against
    /// ContactName alone, which catches "hey perez" but would
    /// false-positive on legitimate canonical nicknames the producer
    /// failed to surface; producers SHOULD populate this when they have
    /// access to the character state.
    /// </summary>
    public IReadOnlyList<string>? CanonicalAddresseeNames { get; init; }

    /// <summary>
    /// Theme O Phase O.1 (May 10, 2026) — the source-frame this artifact
    /// composed against, when known. Populated by the Theme N frame-selection
    /// pre-stage handler; read by post-stage handlers like the frame-coherence
    /// invariant (Theme N N.5). Null = the producer did not run frame
    /// selection (legacy producers pre-O.3 migration, or producer-kinds the
    /// selector does not apply to).
    /// </summary>
    public OutreachFrame? Frame { get; init; }

    /// <summary>
    /// FC-010 architectural primitive (2026-05-14) — the engagement mode
    /// the reply path is using when responding to a follow-up about prior
    /// dispatched content. See <see cref="ReplyContinuationMode"/> for the
    /// engagement paths.
    ///
    /// Null (default) = not a continuation reply; standard invariant
    /// strictness applies. Non-null = the producer selected an engagement
    /// mode; invariants that respect the mode (notably
    /// <c>SelfEchoInvariant</c>) may relax their checks.
    ///
    /// **Status.** The property exists; producer-side selection logic is
    /// the next FC-010 phase. Until producers set the property, it stays
    /// null and runtime behavior is identical to pre-FC-010.
    /// </summary>
    public ReplyContinuationMode? ContinuationMode { get; init; }

    /// <summary>
    /// Phase K.1a (2026-07-02) — flag set to true by the ConversationReply
    /// pipeline when the composer that produced this artifact was a "thin"
    /// composer (SafePath, VirtualIntimacy, or Lean-Normal): one that runs
    /// without substrate injection.
    ///
    /// <para>Consumed by <c>FrontierVerifierHandler.AppliesTo</c>: when
    /// true, the frontier verifier is skipped for this artifact. Rationale:
    /// the verifier's q1-q5 checks compare composed claims against
    /// injected substrate to detect fabrication. On thin-composer paths
    /// there is <em>no substrate injection by design</em>, so those checks
    /// structurally produce false positives — modal framing gets flagged
    /// as unsupported shared-events (q1), rhetorical questions as
    /// unsupported present-tense assertions (q2), terms of endearment as
    /// third-party references (q3). Empirical anchor: 2026-07-02 K.1
    /// weather-question trace, where an in-character reply was blocked by
    /// three simultaneous verifier violations that were all category
    /// errors under thin-composer semantics.</para>
    ///
    /// <para>Local judgment invariants (anti-parrot, addressee-name,
    /// direct-address, temporal-substrate, confabulation, prompt-template-
    /// leak) still fire — they operate on composed-text properties, not on
    /// substrate correspondence, so their applicability doesn't depend on
    /// whether substrate was injected.</para>
    /// </summary>
    public bool ComposerIsThin { get; init; } = false;
}

/// <summary>
/// The producer pipeline that emitted the artifact. Drives invariant
/// applicability via <see cref="Interfaces.ICognitiveOutputInvariant.AppliesTo"/>.
/// One enum value per migrating producer; add as J.5 sub-phases land.
/// </summary>
public enum CognitiveProducerKind
{
    ConversationReply       = 1,
    Outreach                = 2,
    InnerThought            = 3,
    WorldExperience         = 4,
    Reflection              = 5,
    MemoryMerge             = 6,
    ClosedThreadSummary     = 7,
    Voice                   = 8,
}

/// <summary>
/// Where the artifact is headed once it leaves the gate. Drives
/// invariant strictness — outputs going to Mark (Dispatch) get the
/// strictest checks; outputs persisted to memory tiers used as
/// downstream-treated-as-fact (Reflection → Semantic) also strict;
/// outputs persisted as Episodic substrate or context-only get
/// looser invariants.
/// </summary>
public enum CognitiveOutputSink
{
    /// <summary>External delivery to the contact (SMS, voice).</summary>
    Dispatch                 = 1,

    /// <summary>Persisted to memory as Episodic-tier (verbatim continuity).</summary>
    PersistedMemory          = 2,

    /// <summary>Persisted as Semantic / structured summary (treated as fact downstream).</summary>
    PersistedSummary         = 3,

    /// <summary>Held only in the current cycle's context, not persisted.</summary>
    ContextOnly              = 4,
}
