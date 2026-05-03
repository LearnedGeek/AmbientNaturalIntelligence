using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Theme J Phase J.5h-prelude (May 3, 2026) — universal self-echo
/// invariant. Fails when the artifact's content shares a 5+ token
/// verbatim run with any prior Ani message in
/// <see cref="CognitiveArtifact.PriorAniMessages"/>.
///
/// **Why this is a universal invariant rather than a per-producer
/// check:** the prior architecture had `ConversationReplyPhase`
/// running its own self-echo check via
/// <see cref="ParrotingDetector"/> immediately before the J.5a gate
/// call (lines 488-568 of `ConversationReplyPhase.cs` pre-May-3). The
/// J.5a remediation regen path then ran a SEPARATE Ollama call with
/// the gate's hint — and skipped the self-echo check on the regen
/// output. May 3 06:56 dispatched a regen that was byte-identical to
/// the prior assistant turn from chat history; Mark received the same
/// "hey perez…" message twice ~57 seconds apart. That failure shape is
/// exactly what producer-side opt-in checks miss when a new producer
/// (here: the regen path itself) is added without re-running the
/// guard. Universalising the check onto the gate closes the class:
/// every artifact that names PriorAniMessages routes through the same
/// invariant regardless of which pipeline produced it OR whether it
/// is a fresh output or a remediation regen.
///
/// **Threshold (5 tokens) matches <see cref="ParrotingDetector"/>** —
/// the existing detector was tuned to catch verbatim self-repetition
/// while tolerating short coincidental phrases ("the gym", "this
/// morning"). The 5-token threshold is intentionally lower than
/// <see cref="AntiParrotInvariant.VerbatimNGramThreshold"/> (7) because
/// (a) self-repetition is structurally worse than contact-mirroring
/// (mirroring is engagement; self-repetition is template collapse), and
/// (b) Ani's own output is the only source the detector compares to,
/// so false-positive risk is lower.
///
/// **Type-conditional applicability:**
/// - **ConversationReply / Outreach / Voice**: applies — verbatim
///   self-echo at user-visible surfaces is what the May 3 morning tag
///   surfaced.
/// - **InnerThought / Reflection**: applies — self-templating in the
///   substrate is the duck-norris-loop / vanilla-cream-soda-loop class
///   (Apr 9 / Apr 27). InnerThoughtPhase wiring (also May 3) is what
///   makes this surface coverable.
/// - **WorldExperience / MemoryMerge / ClosedThreadSummary**: skipped.
///   World experiences are designed to be repeatable (canonical scene
///   refrains); merges and summaries are structural rather than
///   creative outputs.
///
/// **Behaviour when no PriorAniMessages provided:** returns Pass — the
/// invariant cannot run without context. Soft-skip, not failure.
/// </summary>
public sealed class SelfEchoInvariant : ICognitiveOutputInvariant
{
    public string Name => "self-echo";

    /// <summary>
    /// Token-count threshold for verbatim self-echo detection. Sourced
    /// from <see cref="ParrotingDetector.DefaultMinNGramTokens"/> so
    /// the legacy producer-side check (now removed) and this gate-side
    /// invariant share a single source of truth.
    /// </summary>
    public const int VerbatimNGramThreshold = ParrotingDetector.DefaultMinNGramTokens;

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        if (artifact.PriorAniMessages is null || artifact.PriorAniMessages.Count == 0)
            return false;

        return artifact.ProducerKind is
            CognitiveProducerKind.ConversationReply
         or CognitiveProducerKind.Outreach
         or CognitiveProducerKind.Voice
         or CognitiveProducerKind.InnerThought
         or CognitiveProducerKind.Reflection;
    }

    public Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (artifact.PriorAniMessages is null || artifact.PriorAniMessages.Count == 0)
            return Task.FromResult(InvariantResult.Pass());

        if (string.IsNullOrWhiteSpace(artifact.Content))
            return Task.FromResult(InvariantResult.Pass());

        foreach (var prior in artifact.PriorAniMessages)
        {
            if (ct.IsCancellationRequested)
                return Task.FromResult(InvariantResult.Pass());
            if (string.IsNullOrWhiteSpace(prior)) continue;

            var (isParroting, sharedLen, sharedPhrase) =
                ParrotingDetector.Check(artifact.Content, prior, VerbatimNGramThreshold);
            if (isParroting)
            {
                var hint =
                    $"output duplicates prior Ani message ({sharedLen}-token verbatim run: \"{sharedPhrase}\"). " +
                    $"Rewrite without reusing that phrasing — find a fresh angle on the same topic.";
                return Task.FromResult(InvariantResult.Fail(hint));
            }
        }

        return Task.FromResult(InvariantResult.Pass());
    }
}
