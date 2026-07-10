using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;

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
    // Optional logger — when present, K.4b-diagnostic (2026-07-06) dumps
    // full comparison context on any fire so the mystery false-positive
    // class (12:10:40 CDT cherry-3000 reply that claimed a 58-token match
    // with an unrelated "trying not to cry" prior) can be diagnosed at
    // its next occurrence rather than speculated about after the fact.
    // Null when DI hasn't wired one; the invariant runs identically
    // either way.
    private readonly ILogger<SelfEchoInvariant>? _log;

    public SelfEchoInvariant() { _log = null; }
    public SelfEchoInvariant(ILogger<SelfEchoInvariant> log) { _log = log; }

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

        // 2026-07-10 scope reduction — retire self-echo from
        // ConversationReply / Voice / InnerThought / Reflection. Kept for
        // Outreach because the anchor failure this invariant was calibrated
        // against IS an outreach class: 2026-05-03 06:56 J.5a regen
        // dispatched a byte-identical copy of the prior outreach ~57s
        // later. That template-collapse pattern is real, distinct from
        // conversation continuity, and worth guarding.
        //
        // For ConversationReply within a live active thread, N-token
        // overlap between adjacent turns is often legitimate continuity
        // — coffee-mug thread, WILSON callback, "last sip" recurrence.
        // The invariant produced two consecutive SafeAcks on 2026-07-10
        // during real Mark chatting; empirical cost dominates. Mark's
        // observation: "it's been legitimately a problem and very rarely
        // catches something that is really a problem."
        //
        // For InnerThought / Reflection: universal-invariant Theme J
        // rationale (duck-norris / vanilla-cream-soda self-templating in
        // substrate) sounds compelling in principle, but empirically that
        // failure mode was addressed by the substrate-purge + gate-stack-
        // reduction work; there is no live empirical anchor of substrate
        // template-collapse still surfacing. Scoping out for now; can
        // re-add if we observe a specific regression.
        return artifact.ProducerKind is CognitiveProducerKind.Outreach;
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

                // 2026-07-10 low-information exemption — a shared verbatim run
                // that is nothing but common English scaffolding ("we spend
                // the rest of the", "then we can go", "so I was thinking") is
                // register-continuity between two topically-adjacent replies,
                // not template collapse. The May 3 "hey perez..." class this
                // invariant was calibrated against had rich content tokens in
                // the shared run — the shared phrase there carried meaning.
                // Empirical anchor: 2026-07-10 10:26 dashboard SafeAck where
                // Ani's second regen was a genuinely novel scene (crowded bar
                // + "she's my muse, don't touch") but shared 6 tokens
                // ("we spend the rest of the") with her prior sandcastle
                // reply — every token a stop-word or common English scaffold.
                if (IsLowInformationSharedRun(sharedPhrase))
                    continue;

                // K.4b-diagnostic (2026-07-06) — dump the full comparison
                // context so we can see what artifact.Content vs prior
                // actually looked like at the moment of the fire. Anchor:
                // 2026-07-06 12:10:40 CDT SafeAck where the "58-token
                // verbatim run" hint text didn't visibly overlap with the
                // composed reply, and no one could diagnose the false
                // positive from the log. This log line makes the next fire
                // diagnosable.
                if (_log is not null && _log.IsEnabled(LogLevel.Warning))
                {
                    _log.LogWarning(
                        "SELF_ECHO_DIAGNOSTIC sharedLen={SharedLen} sharedPhrase={SharedPhrase} " +
                        "artifactContentLen={ArtLen} priorLen={PriorLen} " +
                        "artifactContent={ArtContent} priorContent={PriorContent}",
                        sharedLen, sharedPhrase,
                        artifact.Content.Length, prior.Length,
                        artifact.Content, prior);
                }

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
    /// 2026-07-10 low-information exemption — returns true when the shared
    /// verbatim run is dominated by common English scaffolding tokens
    /// (pronouns, articles, prepositions, forms of "be" and "do", and a
    /// small list of very common function verbs / nouns like "spend",
    /// "rest", "time", "day", "night").
    ///
    /// <para>A shared run passes this filter (i.e. counts as low-info) when
    /// at least <see cref="LowInfoRatioThreshold"/> of its tokens are in
    /// the scaffold vocabulary. This targets English continuity phrasing
    /// between two topically-adjacent replies without exempting genuine
    /// content repetition (which would carry rare tokens the model
    /// specifically decided to reuse).</para>
    ///
    /// <para>Empirical anchor: 2026-07-10 10:26 shared run
    /// <c>"we spend the rest of the"</c> = 6 tokens, all scaffold →
    /// low-info → exempt. Contrast the May 3 case
    /// <c>"hey perez, sitting here thinking about"</c> where "perez",
    /// "sitting", "thinking" are content tokens → not exempt.</para>
    /// </summary>
    internal static bool IsLowInformationSharedRun(string? sharedPhrase)
    {
        if (string.IsNullOrWhiteSpace(sharedPhrase)) return false;

        var tokens = sharedPhrase
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':' },
                   StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        var scaffoldCount = tokens.Count(t => LowInfoScaffoldTokens.Contains(t));
        var ratio = (double)scaffoldCount / tokens.Length;
        return ratio >= LowInfoRatioThreshold;
    }

    /// <summary>
    /// Threshold for the low-information exemption. A shared run whose
    /// scaffold-token fraction is at or above this ratio is treated as
    /// English continuity phrasing rather than parroting. Set to 1.0 to
    /// require ALL tokens to be scaffold (strictest — matches the empirical
    /// anchor and rules out any content-token-carrying phrase from being
    /// exempted). Tuning knob for future observation-soak calibration.
    /// </summary>
    internal const double LowInfoRatioThreshold = 1.0;

    /// <summary>
    /// Common English function-word + very-common-scaffold vocabulary used
    /// by <see cref="IsLowInformationSharedRun"/>. Kept short and
    /// conservative — anything with genuine content signal
    /// (proper nouns, distinctive verbs, unusual nouns) must NOT appear
    /// here. If you're adding a token, first check that its appearance in
    /// a 5-8-token verbatim run would still be normal continuity rather
    /// than meaningful reuse.
    /// </summary>
    private static readonly HashSet<string> LowInfoScaffoldTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // Articles + demonstratives + quantifiers
        "a","an","the","this","that","these","those","some","any","all","every","no","none",
        // Pronouns
        "i","you","he","she","it","we","they","me","him","her","us","them",
        "my","your","his","its","our","their",
        "mine","yours","hers","ours","theirs",
        "myself","yourself","himself","herself","itself","ourselves","yourselves","themselves",
        // Prepositions + conjunctions
        "of","in","on","at","to","for","with","from","by","as","into","onto","upon",
        "about","over","under","between","through","during","before","after","since","until",
        "and","or","but","nor","so","yet","because","if","then","than","though","although",
        // Auxiliaries + copulas + forms of common verbs
        "am","is","are","was","were","be","been","being",
        "do","does","did","done","doing",
        "have","has","had","having",
        "will","would","shall","should","can","could","may","might","must","ought",
        // Very common time / continuity scaffold nouns + verbs
        "time","day","night","morning","evening","week","month","year",
        "way","rest","end","start","part","bit","thing","things","stuff",
        "spend","take","make","get","got","go","goes","went","come","came",
        // Common connective adverbs
        "just","also","only","still","already","again","together","alone",
        "here","there","now","then","today","tomorrow","yesterday",
        "very","really","quite","almost","maybe","perhaps",
    };

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
