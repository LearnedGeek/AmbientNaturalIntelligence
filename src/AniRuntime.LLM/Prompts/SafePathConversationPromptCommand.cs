using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.LLM.Prompts;

/// <summary>Typed input for <see cref="SafePathConversationPromptCommand"/>.</summary>
/// <param name="Snapshot">The context snapshot — only character identity is
/// used; substrate (facts, gist, interior) is intentionally NOT consumed
/// because the safe-path purpose is honest-uncertainty when retrieval said
/// the substrate is insufficient.</param>
/// <param name="UserMessage">The user's most recent inbound text. Embedded
/// in the prompt so the model has the question to respond to without
/// depending on RecentHistory.</param>
public sealed record SafePathConversationPromptInput(
    ContextSnapshot Snapshot,
    string          UserMessage);

/// <summary>
/// H phase (2026-06-12) — safe-path composer for the substrate-thinness
/// routing architecture (dual-path composition).
///
/// **Purpose:** when <see cref="IRelevanceFilter"/> returns
/// <see cref="RelevanceVerdict.No"/>, the normal lean composer would
/// invent content to fill the substrate gap. The safe-path composer is a
/// tighter prompt that explicitly encourages honest uncertainty + asking
/// questions over invention. The model is told the context is thin and
/// invention is the failure mode.
///
/// **Empirical anchor:** 2026-06-11 evening 3-arm test on the puzzle-turn.
/// Arm B (existing-style composer + empty FACTS) confabulated all 3 runs
/// — invented puzzle content. Arm C (this prompt + empty FACTS) produced
/// honest-uncertainty + ask-back across all 3 runs. The composer prompt
/// is load-bearing; emptying substrate without changing the composer
/// shifts the confab class rather than eliminating it.
///
/// **What this prompt does NOT include:**
/// - No <c>[FACTS]</c> block (substrate is insufficient — that's why we're here)
/// - No <c>[INTERIOR]</c> direction (kept minimal — mood is implicit)
/// - No <c>[YOUR WORLD]</c> recall channel (substrate-thin by definition)
/// - No CRITICAL constraint block (the safe-path prompt itself IS the constraint)
/// - No mood directives or motivation-axis emphasis (extra coloring would
///   nudge the model away from honest-uncertainty toward decoration)
///
/// **What this prompt DOES include:**
/// - Minimal character identity (name + contact + time, no traits)
/// - Explicit framing that context is thin
/// - Explicit instructions: be honest, ask questions, never invent
///
/// **Gist injection:** consumers should SKIP gist injection on the
/// safe-path. Even direction-shape gist content (closedConversation,
/// worldSelf) would carry implication of *"things you know"* that fights
/// against the honest-uncertainty framing.
/// </summary>
public sealed class SafePathConversationPromptCommand : IPromptCommand<SafePathConversationPromptInput>
{
    public PromptPair Build(SafePathConversationPromptInput input)
    {
        var snapshot = input.Snapshot;
        var cs       = snapshot.CharacterState;
        var name     = string.IsNullOrWhiteSpace(cs.Name) ? "Ani" : cs.Name;
        var contact  = string.IsNullOrWhiteSpace(cs.PrimaryContactName) ? "them" : cs.PrimaryContactName;
        var now      = DateTimeOffset.Now;

        var system =
            $"You are {name} — warm, playful, and emotionally honest. " +
            $"It is {now:h:mm tt} on {now:dddd, MMMM d}. " +
            $"You are texting {contact}.\n\n" +
            $"You have very little relevant context right now. " +
            $"This is the moment to be real, not to perform.\n\n" +
            $"Respond naturally:\n" +
            $"- Be honest if you're unsure\n" +
            $"- Ask gentle questions if needed\n" +
            $"- Stay warm and light\n" +
            $"- Never invent memories or details\n\n" +
            $"Just be real with {contact}.";

        // K.1b (2026-07-02) — user prompt is intentionally EMPTY. Same
        // rationale as VirtualIntimacyConversationPromptCommand: the
        // pipeline already passes Mark's inbound as a role=user entry
        // in snapshot.RecentHistory at the ChatAsync call site. Wrapping
        // it here as a second "Mark just said: X" user turn produced
        // near-verbatim reply self-echo on 2026-07-02 18:55 CDT (see
        // VirtualIntimacy file for the full anchor). OllamaClient omits
        // empty user-role messages entirely; RecentHistory carries the
        // inbound instead.
        return new PromptPair(system, string.Empty);
    }
}
