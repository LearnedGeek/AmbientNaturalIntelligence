using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme M follow-on (2026-05-14) — renders substrate slices into prompt
/// text with explicit epistemic framing. Each method has ONE responsibility
/// (SRP): render a specific kind of substrate content with the framing that
/// tells the model how to treat it (Mark-asserted vs. Ani-prior vs. self-
/// world latitude).
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
}
