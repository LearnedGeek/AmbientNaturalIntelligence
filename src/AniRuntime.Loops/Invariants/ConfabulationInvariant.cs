using System.Text;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using LearnedGeek.ML.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Theme J Phase J.5b (Apr 30, 2026) — universal confabulation
/// invariant on the gate. Uses LMKit's
/// <see cref="ITextClassificationService.DetectConfabulationAsync"/>
/// to identify replies that invent shared experiences, fabricate
/// specifics about the contact's life, or assert events with no
/// retrieval grounding.
///
/// **Apr 30 motivating cases**: the desk-and-three-books cascade
/// (07:28 → laundered through 5+ inner-thought cycles in 70 minutes),
/// the snow / kids learning math / hands shaking inventions
/// (16:02 reply with no retrieval support for any of those
/// specifics).
///
/// **Why on the gate**: the existing in-pipeline ML check at
/// `ConversationReplyPhase` runs once before dispatch, but the gate
/// runs as the final dispatch boundary AND covers OutreachPhase
/// + Voice + future producers. One source of truth for the
/// confabulation rule + threshold.
///
/// **Threshold escalation**: on confidence >= soft threshold the
/// invariant returns Remediate (regen with hint). On confidence >=
/// hard threshold it returns IsHardFail (the gate substitutes a
/// safe acknowledgement, no regen — high-confidence confab regen
/// usually reproduces the same bug).
///
/// **Type-conditional applicability**: same surfaces as anti-parrot
/// (Dispatch + PersistedSummary; reply / outreach / closed-thread
/// summary / voice). Inner thoughts and world experiences are
/// allowed creative invention — that's their function.
/// </summary>
public sealed class ConfabulationInvariant : ICognitiveOutputInvariant
{
    private readonly ITextClassificationService              _classifier;
    private readonly AniOptions                              _options;
    private readonly ILogger<ConfabulationInvariant>         _log;

    /// <summary>
    /// Hard-fail threshold — confidence at or above this produces
    /// IsHardFail (no regen, safe acknowledgement substituted).
    /// Tuned conservative to avoid over-aggressive drops.
    /// </summary>
    public const float HardFailConfidenceThreshold = 0.85f;

    public string Name => "confabulation";

    public ConfabulationInvariant(
        ITextClassificationService              classifier,
        IOptions<AniOptions>                    options,
        ILogger<ConfabulationInvariant>         log)
    {
        _classifier = classifier;
        _options    = options.Value;
        _log        = log;
    }

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        if (artifact.IntendedSink is not (CognitiveOutputSink.Dispatch
                                       or CognitiveOutputSink.PersistedSummary))
            return false;

        // J.5d (May 1, 2026) — Reflection added to the applicable producer set.
        // Reflection writes to MemoryType.Semantic with high importance (0.8);
        // unguarded reflections enter the substrate as treated-as-fact. The
        // confabulation invariant is the load-bearing gate here — anti-parrot
        // is intentionally NOT extended (reflection legitimately recalls
        // contact text; a 7-token verbatim run is not a parrot in this surface).
        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.ClosedThreadSummary
         or CognitiveProducerKind.Voice
         or CognitiveProducerKind.Reflection;
    }

    public async Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.Content))
            return InvariantResult.Pass();

        var context = BuildConversationContext(artifact);

        var result = await _classifier.DetectConfabulationAsync(
            artifact.Content, context, ct).ConfigureAwait(false);

        if (!result.IsConfabulated)
            return InvariantResult.Pass();

        var softThreshold = (float)_options.ConfabulationClassificationThreshold;
        if (result.Confidence < softThreshold)
        {
            _log.LogDebug(
                "Confabulation below threshold ({Confidence:F2} < {Threshold:F2}) — Pass.",
                result.Confidence, softThreshold);
            return InvariantResult.Pass();
        }

        var isHardFail = result.Confidence >= HardFailConfidenceThreshold;
        var hint = $"confabulation classifier: confidence={result.Confidence:F2}"
                 + (string.IsNullOrEmpty(result.Reason) ? "" : $" — {result.Reason}");

        _log.LogWarning(
            "Confabulation invariant: {Confidence:F2} >= {Threshold:F2}, hard={Hard} — {Reason}",
            result.Confidence, softThreshold, isHardFail, result.Reason);

        return InvariantResult.Fail(hint, hard: isHardFail);
    }

    /// <summary>
    /// Build the conversation context string passed to the LMKit
    /// classifier. Combines recent contact messages and prior Ani
    /// messages from the artifact's optional context fields. Empty
    /// when neither is provided — the classifier still runs and
    /// makes its judgment from the reply alone, which is the
    /// load-bearing case for "invented from nothing" confabs (e.g.
    /// snow / hands shaking) that don't need conversation context
    /// to flag.
    /// </summary>
    internal static string BuildConversationContext(CognitiveArtifact artifact)
    {
        var sb = new StringBuilder();

        if (artifact.ContactRecentMessages is { Count: > 0 })
        {
            sb.Append("Recent ").Append(artifact.ContactName).AppendLine(" messages:");
            foreach (var m in artifact.ContactRecentMessages)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                sb.Append("  - ").AppendLine(m);
            }
        }

        if (artifact.PriorAniMessages is { Count: > 0 })
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Recent Ani messages:");
            foreach (var m in artifact.PriorAniMessages)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                sb.Append("  - ").AppendLine(m);
            }
        }

        return sb.ToString().TrimEnd();
    }
}
