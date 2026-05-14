using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme M follow-on (2026-05-14) — renders substrate slices into prompt
/// text with explicit epistemic framing. Each method has ONE responsibility
/// (SRP): render a specific kind of substrate content with the framing that
/// tells the model how to treat it (Mark-asserted vs. Ani-prior vs. self-
/// world latitude vs. closed-conversation callback).
///
/// **Why this exists.** <c>PromptBuilder.cs</c> has ~10 methods, each
/// inlining its own substrate-rendering. FC-002 / FC-004 / FC-005 / FC-006
/// all need epistemic framing added at 3–4 of those sites. Inline edits =
/// "Same mapping in N places → drift" anti-pattern documented in
/// <c>~/.claude/ARCHITECTURE_PATTERNS.md</c> line 478. Centralising slice
/// rendering behind this interface is the SOLID-aligned fix.
///
/// **Distinct from <see cref="IConsciousSubstrateGist"/>.** That interface
/// produces first-person internal-state slices (tension-state §4.8,
/// register-state §4.3) — *what Ani is feeling*. This interface produces
/// epistemic-framing of EXISTING substrate blocks — *how the model should
/// treat what it's seeing*. Both are slice abstractions; they address
/// orthogonal concerns and may converge later under a generalised
/// <c>ISlice</c> contract once both surfaces are mature.
///
/// **SOLID alignment:**
/// <list type="bullet">
///   <item><b>SRP</b>: each render method produces ONE labeled slice with
///   consistent framing — not a kitchen-sink rendering.</item>
///   <item><b>OCP</b>: new slice methods added without modifying existing
///   ones; consumers depend on the methods they need.</item>
///   <item><b>ISP</b>: prompt builders depend on the small surface they
///   actually consume.</item>
///   <item><b>DIP</b>: <see cref="PromptBuilder"/> and producer phases
///   consume via this abstraction; concrete implementations injected at
///   the composition root.</item>
///   <item><b>Testability</b>: each slice unit-testable in isolation — no
///   <see cref="PromptBuilder"/> round-trip, no
///   <see cref="ContextSnapshot"/> construction needed.</item>
/// </list>
///
/// **Slice taxonomy** (all consumers can mix-and-match per-prompt):
/// <list type="bullet">
///   <item><b>ActiveThreadSlice</b> — recent structured per-speaker conversation; per-line framing per speaker.</item>
///   <item><b>MarkAssertedFactsSlice</b> — Facts-tier memory records; "established by Mark, only these may be asserted."</item>
///   <item><b>AniWorldSlice</b> — Ani's bookstore-world content (occupation, nature grounding, recent world experiences); "your own world, you have latitude here."</item>
///   <item><b>AniPriorSlice</b> — Ani's prior Episodic output (her own past dispatched content not in the structured summary); "your prior output, NOT yet established as fact."</item>
///   <item><b>ClosedConversationSlice</b> — most recent <see cref="ClosedConversationRecord"/> gist; "you and {contact} talked about X — established callback ground."</item>
///   <item><b>ThreeAxisRuleSlice</b> — standalone framing block encoding the FC-006 three-axis rule (Subject × Modality × Substrate) for the verifier prompt.</item>
/// </list>
/// </summary>
public interface IEpistemicSubstrateRenderer
{
    /// <summary>
    /// Render the active-thread structured conversation summary as a
    /// substrate slice with explicit epistemic framing. Lines from the
    /// contact are tagged as their-assertions (established); lines from
    /// Ani are tagged as her-prior-claims (her own prior conversational
    /// output, NOT yet established as fact).
    ///
    /// This is the FC-004 epistemic-asymmetry framing applied to the
    /// recent-conversation block consumed by <see cref="PromptBuilder.BuildOutreachPrompt"/>
    /// (and by extension other prompt builders that read the same
    /// substrate).
    /// </summary>
    /// <param name="summary">The structured per-speaker conversation summary
    /// to render. Returns empty string if null or empty.</param>
    /// <param name="contactName">The contact's display name (e.g. "Mark").
    /// Used in the slice header to make ownership explicit.</param>
    /// <returns>The rendered slice text including header framing, or empty
    /// string if there is no content to render.</returns>
    string RenderActiveThreadSlice(StructuredConversationSummary? summary, string contactName);

    /// <summary>
    /// Render the Mark-asserted Facts-tier substrate block. This is the
    /// canonical "established by Mark" surface — character seeds, perception
    /// events, user-asserted content. The FC-005 "you can only assert these"
    /// discipline lives in this slice's framing, centralised so all consumer
    /// prompts use the same wording.
    /// </summary>
    /// <param name="facts">Facts-tier memory records (from
    /// <see cref="ContextSnapshot.GroundedFacts"/>). Returns empty string
    /// if null or empty.</param>
    /// <param name="contactName">The contact's display name.</param>
    /// <returns>The rendered slice text or empty string.</returns>
    string RenderMarkAssertedFactsSlice(IReadOnlyList<MemoryRecord>? facts, string contactName);

    /// <summary>
    /// Render Ani's own-world content as a substrate slice with explicit
    /// self-world latitude framing. Ani has latitude on her bookstore-world
    /// life — occupation, surroundings, daily routine, imagined scenes —
    /// even when specifics are novel. This slice tells the model: "the
    /// constraint that Mark-world claims need substrate does NOT apply
    /// here; your own interior is fair game."
    ///
    /// This is the FC-002 three-axis rule's "self-world ALLOW" surface,
    /// expressed as a positive framing block rather than a constraint.
    /// </summary>
    /// <param name="occupation">Ani's canonical occupation string (e.g.
    /// "bookstore clerk in a small Wisconsin town"). Empty if unset.</param>
    /// <param name="natureGrounding">Optional short phrases about her world
    /// (e.g. "shelving romance novels", "the quiet morning hours"). Capped
    /// at 2 entries by caller.</param>
    /// <param name="recentWorldExperiences">Recent world-layer memory
    /// records (from <see cref="ContextSnapshot.RecentWorldExperiences"/>).
    /// Provides continuity — if Ani mentioned a coworker yesterday, that
    /// coworker still exists today.</param>
    /// <returns>The rendered slice text or empty string when all inputs are empty.</returns>
    string RenderAniWorldSlice(
        string?                       occupation,
        IReadOnlyList<string>?        natureGrounding,
        IReadOnlyList<MemoryRecord>?  recentWorldExperiences);

    /// <summary>
    /// Render Ani's prior Episodic output as a substrate slice with
    /// explicit "not-yet-established" framing. Distinct from the
    /// active-thread slice (which renders the structured per-speaker
    /// summary): this slice surfaces free-form Episodic records (Ani's
    /// own dispatched messages, retrieved by semantic search) when a
    /// consumer wants explicit visibility on them. The framing tells the
    /// model these are her own prior conversational output, NOT facts
    /// that have been confirmed by Mark.
    ///
    /// Addresses the FC-004 "Ani-prior-claims become substrate" failure
    /// mode at the surface where Episodic records leak into composition
    /// prompts.
    /// </summary>
    /// <param name="priorEpisodic">Ani's prior Episodic memory records
    /// (typically retrieved via <see cref="IMemorySearch"/> filtered to
    /// own-output). Returns empty string if null or empty.</param>
    /// <returns>The rendered slice text or empty string.</returns>
    string RenderAniPriorSlice(IReadOnlyList<MemoryRecord>? priorEpisodic);

    /// <summary>
    /// Render the most recent closed-conversation gist as a substrate
    /// slice with callback-support framing. Closed-conversation records
    /// are Vibe Loop V1 outputs — paraphrased relational context that's
    /// safe to reference as established callback ground (e.g.
    /// *"the weekend you and {contact} talked about"*).
    ///
    /// FC-011 (substrate-supported callbacks, deferred) will consume this
    /// slice once Vibe Loop V1.5 is operational. The slice exists now so
    /// the abstraction is complete and consumers can be migrated ahead of
    /// the V1.5 dependency landing.
    /// </summary>
    /// <param name="closed">The closed-conversation record. Returns empty
    /// string if null or if the gist is blank.</param>
    /// <param name="contactName">The contact's display name.</param>
    /// <returns>The rendered slice text or empty string.</returns>
    string RenderClosedConversationSlice(ClosedConversationRecord? closed, string contactName);

    /// <summary>
    /// Render the FC-006 three-axis rule as a standalone framing block
    /// suitable for inclusion in the verifier prompt. The rule encodes
    /// the canonical decision procedure for evaluating claims:
    /// <c>factual ⇒ (self-world OR substrate-supported)</c>; modal claims
    /// always allowed.
    ///
    /// Unlike the other slices, this method takes no substrate input —
    /// it produces a static rule-encoding string. Centralising it in the
    /// renderer keeps the rule's canonical wording in one place; if the
    /// rule shifts, all prompts that consume this slice update together.
    /// </summary>
    /// <param name="contactName">The contact's display name (Mark-world
    /// claims reference his life).</param>
    /// <returns>The rendered rule text. Never empty.</returns>
    string RenderThreeAxisRuleSlice(string contactName);
}
