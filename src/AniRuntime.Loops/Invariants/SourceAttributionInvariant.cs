using System.Text.RegularExpressions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Theme J Phase J.5h (2026-05-21, Issue #47) — class-wide attribution
/// invariant. Fails when an artifact narrates contact-side content
/// (Mark's words / feelings / actions / presence) on a source thread that
/// contains zero contact turns.
///
/// **Empirical anchor.** The 2026-05-21 audit (research log entry) found
/// 26-of-26 one-sided closed-conversation summaries fabricated Mark-side
/// narration: *"Mark felt seen"*, *"Mark's voice softened as he
/// recalled"*, *"Mark and Ani shared a tender moment"* — Mark having
/// said and done none of those things because he had no turns in the
/// thread. Substrate corruption was visible at consumer time (outreach
/// composer reading the corrupted gist), but the load-bearing fix is
/// at the producer.
///
/// **Pairing with the prompt fragment.** This invariant runs as a
/// post-hoc check at the <see cref="ICognitiveOutputGate"/>. The
/// primary defense lives in
/// <see cref="Core.Utilities.SourceAttributionPromptFragments.AttributionRule"/>
/// — composed into producer system prompts so the LLM is asked to
/// follow the rule. This invariant catches violations when the LLM
/// ignores the instruction.
///
/// **Applicability:**
/// - <see cref="CognitiveProducerKind.ClosedThreadSummary"/>: applies
///   when the source thread had zero contact turns. Producers signal
///   this by populating <see cref="CognitiveArtifact.ContactRecentMessages"/>
///   with an empty list (not null) — null = invariant cannot run.
/// - <see cref="CognitiveProducerKind.InnerThought"/>: applies on the
///   same signal — inner thoughts are allowed to imagine, but narration
///   of factual contact-side events without a substrate source is the
///   same class of fabrication.
/// - Other producer kinds: skipped. ConversationReply and Outreach are
///   direct-address surfaces (Ani writes TO Mark, not ABOUT him);
///   third-person narration in those surfaces is a different failure
///   class.
///
/// **Detection shape.** Regex set targeting the empirical 26-of-26
/// patterns — contact-name as actor (Mark + cognition/speech/action verb),
/// possessive narration (Mark's voice/heart/etc), and joint-actor framing
/// (Mark and Ani &lt;verb&gt;). Conservative — designed to false-negative
/// (let some fabrication through) rather than false-positive (block
/// legitimate gists). The prompt fragment is the primary defense; this
/// is the backstop.
/// </summary>
public sealed class SourceAttributionInvariant : ICognitiveOutputInvariant
{
    public string Name => "source-attribution";

    private static readonly string[] ActorVerbs =
    {
        // Cognition / interior-state verbs — fabricating Mark's internal experience
        "thought","felt","sensed","knew","wondered","realized","remembered","recalled",
        "noticed","understood","heard","saw","wanted","liked","loved","cared",
        // Speech verbs — fabricating Mark's utterances
        "said","asked","told","replied","responded","expressed","whispered","spoke",
        "talked","mentioned","agreed","suggested","sighed","laughed","smiled",
        "nodded","paused",
        // Action verbs — fabricating Mark's physical presence
        "came","went","walked","sat","stood","moved","reached","brought","made",
        "sent","received","got","started","stopped","began","ended","opened",
        "closed","held","touched","kissed","leaned","arrived","entered","left",
        "stayed","softened","brightened","tensed",
    };

    private static readonly Regex ContactActorVerbPattern =
        new(@"\b(?<contact>\w+)\s+(" + string.Join("|", ActorVerbs) + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContactPossessivePattern =
        new(@"\b(?<contact>\w+)'s\s+\w+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JointActorPattern =
        new(@"\b(?<a>\w+)\s+and\s+(?<b>\w+)\s+(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        // Producer-kind gate.
        if (artifact.ProducerKind is not (
            CognitiveProducerKind.ClosedThreadSummary or
            CognitiveProducerKind.InnerThought))
            return false;

        // Signal: producer populated ContactRecentMessages explicitly with
        // an empty list = "no contact turns in source." Null = invariant
        // cannot run (soft-skip).
        if (artifact.ContactRecentMessages is null) return false;
        return artifact.ContactRecentMessages.Count == 0;
    }

    public Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Content))
            return Task.FromResult(InvariantResult.Pass());

        var contact = artifact.ContactName;
        if (string.IsNullOrWhiteSpace(contact))
            return Task.FromResult(InvariantResult.Pass());

        // Pattern 1 — contact-name as actor of a cognition/speech/action verb.
        // Example: "Mark felt seen", "Mark's voice softened", "Mark recalled".
        foreach (Match m in ContactActorVerbPattern.Matches(artifact.Content))
        {
            if (string.Equals(m.Groups["contact"].Value, contact, StringComparison.OrdinalIgnoreCase))
            {
                var hint =
                    $"output narrates {contact}-side content (\"{m.Value}\") but the source thread "
                  + $"had no {contact} turns. Rewrite to describe only what was actually sent. "
                  + $"If {contact} has not responded, say so explicitly rather than inventing his side.";
                return Task.FromResult(InvariantResult.Fail(hint));
            }
        }

        // Pattern 2 — contact-name possessive narration.
        // Example: "Mark's voice", "Mark's heart skipped a beat".
        foreach (Match m in ContactPossessivePattern.Matches(artifact.Content))
        {
            if (string.Equals(m.Groups["contact"].Value, contact, StringComparison.OrdinalIgnoreCase))
            {
                var hint =
                    $"output narrates {contact}-side experience (\"{m.Value}\") but the source thread "
                  + $"had no {contact} turns. Possessive references to {contact}'s body, voice, or "
                  + $"feelings are fabrication when {contact} hasn't been present in the input.";
                return Task.FromResult(InvariantResult.Fail(hint));
            }
        }

        // Pattern 3 — joint-actor framing ("Mark and Ani <verb>").
        // Example: "Mark and Ani shared a tender moment", "Mark and Ani sat together".
        foreach (Match m in JointActorPattern.Matches(artifact.Content))
        {
            var aVal = m.Groups["a"].Value;
            var bVal = m.Groups["b"].Value;
            var isContactJoint =
                string.Equals(aVal, contact, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bVal, contact, StringComparison.OrdinalIgnoreCase);
            if (isContactJoint)
            {
                var hint =
                    $"output frames a joint action (\"{m.Value}\") but the source thread had no "
                  + $"{contact} turns. \"X and Y did Z\" requires both to have been present.";
                return Task.FromResult(InvariantResult.Fail(hint));
            }
        }

        return Task.FromResult(InvariantResult.Pass());
    }
}
