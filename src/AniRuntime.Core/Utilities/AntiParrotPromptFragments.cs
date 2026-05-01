namespace AniRuntime.Core.Utilities;

/// <summary>
/// Theme J Phase J.5e (May 1, 2026) — shared anti-parrot prompt
/// fragments used as a single source of truth for the verbatim-7-token
/// rule across producers.
///
/// **Why a shared primitive**: Vibe Loop V1.2's
/// <c>ClosedConversationSummarizer.BuildGistPrompt</c> inlines the
/// anti-parrot rule as plain text in its system prompt. The gate-side
/// <c>AntiParrotInvariant</c> enforces the same rule structurally
/// post-generation. When the rule changes (threshold, phrasing, scope),
/// both sites must change in lockstep — otherwise the producer-side
/// constraint diverges from the validator-side check. Hoisting the
/// rule text + threshold here makes both sites reference the same
/// definition.
///
/// **Apr 30 consolidation conversation** (Mark → Claude): the right
/// architectural answer was to extract this primitive at J.5e time,
/// not earlier — extracting before J.4 would have churned. Now that
/// J.4 + J.5a + J.5b + J.5c are in place, V1.2 rejoins the gate's
/// invariant family by referencing the same fragments here.
/// </summary>
public static class AntiParrotPromptFragments
{
    /// <summary>
    /// The token-count threshold for verbatim-lift detection. Must
    /// match <c>AntiParrotInvariant.VerbatimNGramThreshold</c>. When
    /// this changes here, the gate's threshold changes by reference.
    /// </summary>
    public const int VerbatimNGramThreshold = 7;

    /// <summary>
    /// Producer-side prompt instruction line that names the verbatim
    /// constraint. Embedded in producer system prompts (V1.2 gist
    /// summarizer, future migrations) so the LLM is asked to comply
    /// with the same rule the gate's invariant enforces post-hoc.
    /// </summary>
    /// <param name="contactName">Name of the contact (typically "Mark") used in the rule line.</param>
    public static string NoVerbatimContactTurnsRule(string contactName) =>
        $"DO NOT quote any contact ({contactName}) turn verbatim. Do not lift phrases of "
        + $"{VerbatimNGramThreshold} or more consecutive words from any of his messages.";
}
