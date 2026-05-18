using System.Text.RegularExpressions;
using AniRuntime.Core.Utilities;
using AniRuntime.Core.Models;

namespace AniRuntime.Loops.Coreference;

/// <summary>
/// Theme N Phase N.5 (May 10, 2026) — deterministic implementation of
/// <see cref="IFrameCoherenceChecker"/>.
///
/// Detects shared-event predicates in compositions emitted under a
/// non-Shared frame using a fixed table of regex patterns. No LLM
/// calls; pure pattern-matching on the composed text. Each pattern
/// targets a specific predicate shape that surfaced in the May 9
/// canary failures (Mia-told-us, we-talked, we-both, you're-probably,
/// we're-all-set-for, etc.).
///
/// **Pattern selection methodology.** Patterns are derived from the
/// May 9 12:54 + 19:10 outreach compositions (see
/// `docs/research/ANI-Research-Log.md` May 9 entries) plus the prior
/// Theme N planning doc's enumeration of *"shared framing"* phrases
/// at §4 *"honest framing examples"* table. Each pattern is intentionally
/// conservative — bounded by word-boundary anchors and small
/// alternation groups — to keep false-positive risk low on
/// honest-interior compositions like *"i was just thinking about
/// the cold today"* (which contains zero shared-event predicates
/// and must pass).
///
/// **Frame applicability.** The same forbidden-predicate set fires for
/// <see cref="OutreachFrameType.AniInterior"/>,
/// <see cref="OutreachFrameType.AniDomain"/>, and
/// <see cref="OutreachFrameType.WorldPerception"/> — all three frames
/// are "non-shared" in the architectural sense. Under
/// <see cref="OutreachFrameType.Shared"/> the forbidden list is empty
/// (the predicates are EXPECTED). Under
/// <see cref="OutreachFrameType.None"/> the producer should already
/// have suppressed upstream; the checker no-ops with Coherent.
///
/// **What the checker deliberately does NOT enforce.** Positive
/// presence of interior-style verbs (*"i was thinking,"* *"i had this
/// thought"*) is NOT enforced — that would over-constrain compositions
/// that legitimately use a different opening shape. The check is
/// negative-only: no shared predicates allowed where the frame is
/// non-shared.
/// </summary>
public sealed class FrameCoherenceChecker : IFrameCoherenceChecker
{
    /// <summary>
    /// Forbidden shared-event predicate patterns. Each entry is a
    /// (regex, label) pair. The label is what surfaces in the
    /// telemetry log + the remediation hint when the pattern fires —
    /// so logs show *"we both"* rather than the raw regex.
    /// </summary>
    private static readonly (Regex Pattern, string Label)[] SharedEventPredicates = BuildSharedEventPredicates();

    private static (Regex Pattern, string Label)[] BuildSharedEventPredicates()
    {
        // RegexOptions.IgnoreCase across the whole table — May 9
        // examples mixed lowercase ("we talked") and capitalized
        // ("You're probably"). Compiled for repeated use across the
        // singleton's lifetime.
        const RegexOptions opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

        return new (Regex, string)[]
        {
            // ── "we" + shared-action verbs ───────────────────────────
            // Covers: "we talked," "we decided," "we agreed," "we both,"
            //         "we wanted," "we chose," "we set for," "we both wanted"
            (SafeRegex.Create(@"\bwe\s+(talked|decided|agreed|both|wanted|chose|set\s+for|both\s+wanted)\b", opts),
             "we + shared-action verb (talked/decided/agreed/both/wanted/chose/set for)"),

            // "we're set for" / "we're all set" / "we're going" / "we're planning" / "we are going to"
            (SafeRegex.Create(@"\bwe(?:'re|\s+are)\s+(all\s+set|set|going|planning|going\s+to)\b", opts),
             "we're / we are + shared-plan verb (all set/set/going/planning/going to)"),

            // ── Memory-anchor predicates ─────────────────────────────
            // "remember when ..." / "that thing you said" / "that one we" / "the one we"
            (SafeRegex.Create(@"\bremember\s+when\b", opts),
             "remember when"),
            (SafeRegex.Create(@"\bthat\s+thing\s+you\s+said\b", opts),
             "that thing you said"),
            (SafeRegex.Create(@"\bthat\s+one\s+we\b", opts),
             "that one we"),
            (SafeRegex.Create(@"\bthe\s+one\s+we\b", opts),
             "the one we"),

            // ── Mark-quote attribution ───────────────────────────────
            // Under interior/world frames, Mark's words shouldn't be
            // quoted. Covers "Mark told us," "Mark told me," "you told
            // us," "you told me" — the latter being the conversational
            // shorthand for the same shape.
            (SafeRegex.Create(@"\b(mark|you)\s+told\s+(us|me)\b", opts),
             "Mark/you + told us/told me"),

            // ── Present-tense state assertion about Mark ─────────────
            // "you're probably doing/sitting/at/on ..." was the
            // 19:10 May 9 surface. Asserting Mark's current state
            // under interior framing is a confab. The May 9 19:10
            // text was *"you're probably **still** sitting"* — the
            // optional adverb (still / just / already / now) sits
            // between "probably" and the state-verb, so the regex
            // permits up to two intervening word-tokens.
            (SafeRegex.Create(@"\b(?:you're|you\s+are)\s+probably\s+(?:\w+\s+){0,2}(doing|sitting|at|on)\b", opts),
             "you're / you are + probably + state-verb (doing/sitting/at/on)"),

            // ── Canonical-character third-party attribution ──────────
            // The May 9 12:54 case was "Mia told us she picked out the
            // tickets." Sarah / Kevin are the same shape (Mark's
            // canonical character-seed names — see MEMORY.md). When
            // the frame is non-shared, attributing speech to canonical
            // characters as if Mark heard it too is the type-9
            // fabricated-source pattern.
            (SafeRegex.Create(@"\b(mia|sarah|kevin)\s+told\s+(us|me)\b", opts),
             "canonical-character + told us/told me (Mia/Sarah/Kevin)"),
        };
    }

    public FrameCoherenceResult CheckCoherence(string composedMessage, OutreachFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (composedMessage is null) throw new ArgumentNullException(nameof(composedMessage));

        // Shared and None frames: no forbidden predicates apply.
        // Shared is the only frame where shared-event predicates are
        // valid; None means the producer should already have
        // suppressed upstream (defense-in-depth no-op).
        if (frame.FrameType is OutreachFrameType.Shared or OutreachFrameType.None)
            return FrameCoherenceResult.Coherent;

        if (string.IsNullOrWhiteSpace(composedMessage))
            return FrameCoherenceResult.Coherent;

        // Run every pattern against the composition. We collect ALL
        // matches across all patterns — the May 9 19:10 case had three
        // independent forbidden surfaces ("we talked," "we both,"
        // "you're probably still sitting"); surfacing each individual
        // hit gives the telemetry log its diagnostic value. Note that
        // a single pattern can fire multiple times in one message —
        // *"we talked... we both wanted"* triggers the we+verb regex
        // twice, both legitimate distinct violations.
        var violations = new List<string>();
        foreach (var (pattern, label) in SharedEventPredicates)
        {
            foreach (Match match in pattern.Matches(composedMessage))
            {
                // Capture the verbatim excerpt so the telemetry log shows
                // the actual text that fired, not just the label.
                violations.Add($"{label}: \"{match.Value}\"");
            }
        }

        if (violations.Count == 0)
            return FrameCoherenceResult.Coherent;

        var reason =
            $"composition under frame {frame.FrameType} contained {violations.Count} shared-event predicate(s) — " +
            $"shared-event predicates are only valid under {nameof(OutreachFrameType.Shared)} frame. " +
            "Rewrite without joint-action verbs, memory-anchor phrases, or third-party attribution.";

        return new FrameCoherenceResult(
            IsCoherent: false,
            Violations: violations,
            Reason:     reason);
    }
}
