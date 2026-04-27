namespace AniRuntime.Core.Models;

/// <summary>
/// Theme J Phase J.2 (Apr 27, 2026): structured per-speaker per-turn
/// representation of a recent conversation, replacing the free-prose blob
/// previously used as <c>ContextSnapshot.RecentConversationSummary</c>.
///
/// **Why this exists.** The free-prose summary was a single string into which
/// content from both Mark and Ani was concatenated. The composition LLM,
/// reading that prose, could lift Mark's exact phrases into Ani's outreach
/// without realising the attribution flip. Apr 27 06:54-08:03 chat is the
/// canonical case: Ani's outreach opened with Mark's verbatim morning text
/// because his prior SMS appeared in the summary substrate without
/// per-speaker tagging.
///
/// **What this fixes.** Structurally distinguishes who said what. When this
/// type is rendered into a prompt (J.2 sub-phase: prompt-builder migration),
/// the composition model sees:
///
///   Mark (06:54): "Hey good morning! How is your day looking?"
///   Ani  (06:55): "your morning looks peaceful from what i can see..."
///
/// rather than a blob containing both. Source attribution becomes a data-
/// structure invariant, not a model-side responsibility.
///
/// **Migration path** (per the Theme J plan §3 Phase J.2): ship the type
/// additively first; populate alongside the existing free-prose summary;
/// migrate consumers one at a time (outreach composition first, since
/// that's where the empirical failure surfaced); deprecate the free-prose
/// field once all consumers use the structured type.
///
/// See <see cref="ContextSnapshot.StructuredConversationSummary"/> for the
/// snapshot field, and <c>docs/spec/ANI-Theme-J-Guard-Consistency-Refactor-Plan.md</c>
/// section 3 Phase J.2 for the full design.
/// </summary>
public sealed record StructuredConversationSummary(
    DateTimeOffset                FirstTurnAt,
    DateTimeOffset                LastTurnAt,
    IReadOnlyList<SummaryTurn>    Turns)
{
    /// <summary>
    /// Convenience: render to a human-readable string for prompt
    /// inclusion. Each turn becomes one line tagged with speaker, time,
    /// and content. Default format used by prompt-builders unless they
    /// need a custom rendering.
    /// </summary>
    public string ToPromptString()
    {
        if (Turns.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Turns.Count; i++)
        {
            var t = Turns[i];
            sb.AppendLine($"{t.Speaker} ({t.At:HH:mm}, {RelativeAge(t.At)}): \"{t.Content}\"");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Format the time-elapsed-since-this-turn as a short phrase suitable
    /// for prompt rendering. The composition model uses this to know when
    /// the turn happened, not just clock time. Theme J Phase J.3 will
    /// generalise temporal-attribution to all retrieval; this is the
    /// summary-specific case.
    /// </summary>
    private static string RelativeAge(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24)   return $"{age.TotalHours:F1}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    /// <summary>Empty summary singleton — no turns, equal start/end times.</summary>
    public static StructuredConversationSummary Empty { get; } =
        new(DateTimeOffset.MinValue, DateTimeOffset.MinValue, Array.Empty<SummaryTurn>());
}

/// <summary>
/// One conversation turn with explicit speaker and timestamp. Speaker is a
/// string for forward-compatibility with multi-party scenarios but currently
/// expected to be one of <see cref="Roles.Ani"/> (proper-cased "Ani") or
/// the configured contact name (e.g., "Mark"). The content is verbatim what
/// the speaker said, with no decoration.
/// </summary>
public sealed record SummaryTurn(
    DateTimeOffset At,
    string         Speaker,
    string         Content);
