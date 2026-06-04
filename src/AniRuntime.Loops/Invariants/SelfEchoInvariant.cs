using AniRuntime.Core;
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

    /// <summary>
    /// FC-003 position-aware threshold (2026-05-14): a shared verbatim
    /// run is treated as a HABITUAL OPENER (allowed in active thread
    /// continuation) when it (a) starts at position 0 of both messages,
    /// (b) is ≤ <see cref="OpenerTokenCap"/> tokens, and (c) leaves
    /// substantial novel content after it in the new artifact.
    ///
    /// The cap matches Mark's empirical opener pattern (May 12 ~20:33
    /// "mmm— baby, hey. yeah i" — 5 tokens; FC-003a fixture's "hey
    /// honey yeah i was just" — 6 tokens). Habitual openers above this
    /// length are vanishingly rare in conversational text; the cap is
    /// intentionally conservative.
    /// </summary>
    public const int OpenerTokenCap = 6;

    /// <summary>
    /// FC-003: minimum character count of novel content after the shared
    /// opener in the new artifact for opener-repetition to qualify as
    /// allowed continuation (rather than mostly-the-prior-message). For
    /// FC-003b (byte-identical regen) the artifact equals the prior so
    /// no novel suffix exists; the check correctly falls through to fail.
    /// </summary>
    public const int OpenerNovelSuffixMinChars = 20;

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
            // 2026-06-03 — exclude the static safe-acknowledgement from the
            // self-echo comparison pool. Once any prior turn dispatches the
            // SafeAck fallback, the SafeAck text enters PriorAniMessages and
            // every subsequent SafeAck attempt (which is the same constant
            // string) trips this gate, producing a cascade where the model
            // keeps SafeAck-ing because its prior SafeAck primes the
            // detector. The cascade was visible in the 20260603-180719-full
            // sweep (karen-binding turn 3, others). The static fallback is
            // never *generated* output we want to vary; it's a structural
            // dispatch artifact. Skip it explicitly.
            if (IsSafeAcknowledgement(prior)) continue;

            var (isParroting, sharedLen, sharedPhrase) =
                ParrotingDetector.Check(artifact.Content, prior, VerbatimNGramThreshold);
            if (isParroting)
            {
                // FC-003 position-aware check (2026-05-14): habitual openers
                // in active thread continuation are NOT parroting. If the
                // shared run is short (≤ OpenerTokenCap), positioned at the
                // start of both messages, AND the new artifact has substantial
                // novel content after the opener, treat as conversational
                // continuation and pass. Full-content parrots (FC-003b) and
                // mid-message verbatim runs still fail.
                if (IsHabitualOpenerRepetition(artifact.Content, prior, sharedPhrase, sharedLen))
                    continue;

                var hint =
                    $"output duplicates prior Ani message ({sharedLen}-token verbatim run: \"{sharedPhrase}\"). " +
                    $"Rewrite without reusing that phrasing — find a fresh angle on the same topic.";
                return Task.FromResult(InvariantResult.Fail(hint));
            }
        }

        return Task.FromResult(InvariantResult.Pass());
    }

    /// <summary>
    /// Returns true when the prior message is the canonical
    /// <see cref="GateFallbacks.SafeAcknowledgement"/> dispatched on cascade
    /// failure. Compared trimmed and case-insensitive. Internal for testing.
    /// </summary>
    internal static bool IsSafeAcknowledgement(string prior)
    {
        if (string.IsNullOrWhiteSpace(prior)) return false;
        return prior.Trim().Equals(
            GateFallbacks.SafeAcknowledgement.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// FC-003 position-aware check: returns true when the shared verbatim
    /// run is at the start of both messages, is within the opener token
    /// cap, and the new artifact has substantial novel content beyond the
    /// shared opener. Internal for testing.
    /// </summary>
    internal static bool IsHabitualOpenerRepetition(
        string content, string prior, string? sharedPhrase, int sharedLen)
    {
        if (string.IsNullOrWhiteSpace(sharedPhrase)) return false;
        if (sharedLen > OpenerTokenCap) return false;

        // The shared run must be at position 0 in both messages.
        if (!content.StartsWith(sharedPhrase, StringComparison.OrdinalIgnoreCase)) return false;
        if (!prior.StartsWith(sharedPhrase, StringComparison.OrdinalIgnoreCase)) return false;

        // The new artifact must have substantive content after the opener
        // (rules out FC-003b byte-identical regen — sharedPhrase IS the
        // full content there, so no novel suffix exists).
        var novelSuffixLength = content.Length - sharedPhrase.Length;
        return novelSuffixLength >= OpenerNovelSuffixMinChars;
    }
}
