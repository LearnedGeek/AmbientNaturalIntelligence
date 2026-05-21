namespace AniRuntime.Core.Utilities;

/// <summary>
/// Theme J Phase J.5h (2026-05-21, Issue #47) — shared producer-side
/// prompt fragments enforcing source attribution. Sibling to
/// <see cref="AntiParrotPromptFragments"/>.
///
/// **The empirical problem this addresses.** The 2026-05-21 audit (see
/// `ANI-Research-Log.md` 2026-05-21 entry) found that
/// <c>ClosedConversationSummarizer</c> fabricated Mark-side narration in
/// 26 of 26 Ani-only outreach closures since May 14. The gist prompt
/// framed the task as *"summary of the conversation between Mark and Ani,
/// focus on what shifted between them"* — when given a one-sided thread,
/// the LLM complied by inventing Mark's side to fulfill the
/// "between them" framing.
///
/// **The architectural answer** (per Mark's 2026-05-21 redirect: *"if
/// we're missing attribution as a large gap, how can the summarizer
/// work correctly… we shouldn't have to handhold the process because
/// we're chasing specific scenarios"*) is a class-wide attribution
/// invariant that holds regardless of input arity. Composed into every
/// producer that narrates conversational content:
/// <c>ClosedConversationSummarizer</c>, <c>InnerThoughtPhase</c>
/// write-side, future narrators (ReflectionGistService, outreach if it
/// ever narrates rather than directly addresses).
///
/// Paired with the post-hoc <c>SourceAttributionInvariant</c> at the
/// <c>CognitiveOutputGate</c>: prompt asks the model to follow the rule,
/// invariant catches violations when it doesn't.
/// </summary>
public static class SourceAttributionPromptFragments
{
    /// <summary>
    /// The class-wide attribution rule. Spells out the principle
    /// (claims about a speaker require turns from that speaker) and the
    /// shape it takes for closed-conversation summaries (no fabricated
    /// contact-side narration when the contact has no turns).
    /// </summary>
    /// <param name="contactName">Name of the contact (typically "Mark").</param>
    /// <param name="aniName">Display name of the agent (typically "Ani").</param>
    public static string AttributionRule(string contactName, string aniName = "Ani") =>
        $"ATTRIBUTION RULE — claims must be grounded in actual turns. "
      + $"Do NOT narrate {contactName}'s words, feelings, actions, presence, or internal state "
      + $"unless {contactName} actually has turns in the input. If {contactName} has zero turns, "
      + $"the exchange is one-sided ({aniName} reaching out without a response yet): summarize "
      + $"only what {aniName} sent and explicitly note that {contactName} has not responded. "
      + $"Do not invent {contactName}'s reactions to fulfill a 'between them' framing. "
      + $"The same rule applies to {aniName}: do not narrate {aniName}-side content that isn't "
      + $"grounded in {aniName}'s actual turns.";
}
