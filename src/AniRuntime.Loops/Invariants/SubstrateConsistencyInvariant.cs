using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Invariants;

/// <summary>
/// Issue #94 (2026-08-25) — write-side self-audit invariant that checks
/// substrate-writing artifacts (inner thought, reflection, world
/// experience) against Ani's confirmed substrate BEFORE they enter the
/// pool. Reads top-K neighbours from Facts + Episodic tiers via hybrid
/// retrieval, hands the artifact + neighbours to
/// <see cref="IContentContradictionClassifier"/>, and fails the
/// invariant when the classifier returns
/// <see cref="ContradictionOutcome.Contradicts"/> at or above the
/// configured confidence threshold.
///
/// <para>
/// <b>Motivation.</b> Mark's 2026-07-06 architectural directive:
/// <i>"we'll likely want Ani to do validation of her own. if she has an
/// inner thought that is counter to something I've validated, it should
/// adjust the invalid data through pruning or flagging appropriately."</i>
/// Prior to this invariant Ani's inner-thought generator could write
/// content contradicting confirmed facts about Mark's world; the
/// contradiction only surfaced later (if at all) through Mark's
/// <c>///tag</c> walk-back. Write-side check catches it before it enters
/// substrate, breaking the compounding class where a contradicted
/// inner-thought becomes retrieval substrate for subsequent cycles.
/// </para>
///
/// <para>
/// <b>Producer scope.</b> InnerThought / Reflection — the two
/// substrate-writing producer kinds actually emitted today.
/// ConversationReply / Outreach / Voice / ClosedThreadSummary go
/// through <c>FrontierVerifier</c> instead (contact-facing outputs
/// get a different guard shape). ReactiveShare is a compose-then-
/// dispatch producer, not a substrate write, and is not in scope for
/// this invariant. <c>WorldExperience</c> exists in the enum but no
/// producer currently builds an artifact with that kind — world-
/// experience thoughts arrive through the <c>InnerThought</c> producer
/// path today (PR #147 Devin review-fix, 2026-08-25 — Devin verified
/// no wiring site). If a dedicated WorldExperience producer is added
/// later, extend AppliesTo in that PR.
/// </para>
///
/// <para>
/// <b>Behaviour on fire.</b> Returns
/// <c>InvariantResult.Fail(hint, hard: true)</c> — hard fail, mapping
/// to <see cref="OutputGateVerdict.Fail"/> at the gate. Regenerating on
/// a substrate-contradiction verdict rarely helps because the
/// underlying grounding conflict is unchanged (same substrate, same
/// conflict); hard fail signals "drop, don't regen." The current gate
/// discipline for InnerThought (<c>InnerThoughtPhase.EvaluateAsync</c>)
/// and Reflection (<c>ReflectionPhase.TryRunAsync</c>) drops on any
/// non-Pass verdict, so today the soft-vs-hard distinction is
/// invisible in behaviour — but PR #147 Devin review-fix (2026-08-25)
/// flagged that the semantic intent should still be encoded
/// explicitly so a future consumer that regenerates on Remediate
/// doesn't accidentally re-roll a contradicted artifact. #94's
/// original design document proposes an enhancement — persist WITH
/// <c>Validity='invalid_contradiction'</c> so records are preserved
/// for research — which becomes a follow-up if wanted.
/// </para>
///
/// <para>
/// <b>Fail-open discipline.</b> When the classifier returns
/// <see cref="ContradictionOutcome.Unknown"/> (transport error, parse
/// failure, timeout) the invariant returns Pass. Same fail-open contract
/// as every other classifier in the runtime — a classifier problem must
/// not silently drop legitimate inner-thought output.
/// </para>
///
/// <para>
/// <b>Empty substrate.</b> When retrieval returns zero neighbours (rare
/// but possible on cold-start / query with no semantic anchor above the
/// cosine floor) the invariant returns Pass: there is nothing to
/// contradict. This is distinct from the classifier's Neutral verdict
/// (which means "the classifier had substrate but the content was
/// orthogonal") — a genuinely-empty substrate lookup short-circuits
/// before the classifier is called.
/// </para>
///
/// <para>
/// <b>Feature flag.</b> Gated by
/// <see cref="AniOptions.SubstrateConsistencyInvariantEnabled"/> —
/// default ON per Mark's ship-live discipline (2026-08-24). The flag
/// exists as a rollback lever because this invariant fires on the write
/// path and a noisy classifier could silently drain substrate; if the
/// fire rate against genuine content proves too high in production,
/// flip the flag off, tune the threshold, redeploy.
/// </para>
/// </summary>
public sealed class SubstrateConsistencyInvariant : ICognitiveOutputInvariant
{
    /// <summary>
    /// Top-K neighbours retrieved from each of Facts and Episodic tiers.
    /// Total substrate handed to the classifier is bounded above by
    /// 2 × <see cref="SubstrateTopK"/> after dedup. Sized small because
    /// the classifier's prompt scales with substrate length and the
    /// per-call cost is a few seconds on qwen3:14b — five per tier is
    /// enough to catch the direct-contradiction class without inflating
    /// latency per artifact.
    /// </summary>
    public const int SubstrateTopK = 5;

    private readonly IContentContradictionClassifier              _classifier;
    private readonly IMemorySearch                                _memory;
    private readonly AniOptions                                   _options;
    private readonly ILogger<SubstrateConsistencyInvariant>       _log;

    public string Name => "substrate-consistency";

    public SubstrateConsistencyInvariant(
        IContentContradictionClassifier              classifier,
        IMemorySearch                                memory,
        IOptions<AniOptions>                         options,
        ILogger<SubstrateConsistencyInvariant>       log)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _memory     = memory     ?? throw new ArgumentNullException(nameof(memory));
        _options    = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log        = log        ?? throw new ArgumentNullException(nameof(log));
    }

    public bool AppliesTo(CognitiveArtifact artifact)
    {
        if (artifact is null) return false;

        // Rollback lever — flag off means the invariant is completely
        // inert. Registration stays in place so re-enabling is a config
        // flip, not a code redeploy.
        if (!_options.SubstrateConsistencyInvariantEnabled)
            return false;

        // WorldExperience deliberately absent: no producer builds an
        // artifact with that kind today (PR #147 Devin review-fix
        // 2026-08-25). World-experience thoughts arrive via the
        // InnerThought producer path and are covered by the branch
        // below. If a dedicated WorldExperience producer lands later,
        // that PR extends this set.
        return artifact.ProducerKind is
            CognitiveProducerKind.InnerThought
         or CognitiveProducerKind.Reflection;
    }

    public async Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.Content))
            return InvariantResult.Pass();

        // Retrieve confirmed substrate: top-K semantic neighbours from
        // Facts + Episodic. Interior tier is excluded intentionally — we
        // are checking whether the new artifact contradicts CONFIRMED
        // substrate (Mark-validated facts + verbatim conversation), not
        // whether it contradicts other interior monologue.
        //
        // PR #147 Devin review-fix (2026-08-25): pass an explicit cosine
        // floor. Without it, SearchByTierAsync returns top-K regardless
        // of relevance against a populated substrate, which fed loosely-
        // related records to the classifier and made the empty-substrate
        // short-circuit below unreachable in production. Peer consumers
        // (FrontierVerifierHandler, EpistemicContextBuilder) pass their
        // own floors; this invariant has its own knob because the write-
        // path stakes differ from the read-path stakes.
        var minCosine = _options.SubstrateConsistencyMinCosineThreshold;
        var factsNeighbours = await _memory.SearchByTierAsync(
                artifact.Content, EpistemicTier.Facts, SubstrateTopK, ct,
                minCosine: minCosine)
            .ConfigureAwait(false);
        var episodicNeighbours = await _memory.SearchByTierAsync(
                artifact.Content, EpistemicTier.Episodic, SubstrateTopK, ct,
                minCosine: minCosine)
            .ConfigureAwait(false);

        var substrateLines = factsNeighbours
            .Concat(episodicNeighbours)
            .Select(s => s.Record?.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .Take(SubstrateTopK * 2)
            .Select(c => $"- {c}")
            .ToList();

        if (substrateLines.Count == 0)
        {
            _log.LogDebug(
                "substrate-consistency: no confirmed substrate neighbours for {Producer} — Pass (nothing to contradict).",
                artifact.ProducerKind);
            return InvariantResult.Pass();
        }

        var substrateContext = string.Join("\n", substrateLines);

        var verdict = await _classifier.ClassifyAsync(
                artifact.Content, substrateContext, ct)
            .ConfigureAwait(false);

        // Fail-open on Unknown — classifier problem should not silently
        // drop legitimate content. Same discipline as tag-intent.
        if (verdict.Outcome != ContradictionOutcome.Contradicts)
        {
            _log.LogDebug(
                "substrate-consistency: verdict={Outcome} confidence={Confidence:F2} — Pass.",
                verdict.Outcome, verdict.Confidence);
            return InvariantResult.Pass();
        }

        var threshold = _options.SubstrateContradictionThreshold;
        if (verdict.Confidence < threshold)
        {
            _log.LogDebug(
                "substrate-consistency: contradicts but below threshold ({Confidence:F2} < {Threshold:F2}) — Pass.",
                verdict.Confidence, threshold);
            return InvariantResult.Pass();
        }

        var quote = string.IsNullOrWhiteSpace(verdict.Quote) ? "(none)" : verdict.Quote!;
        var reason = verdict.Reason ?? string.Empty;

        // Structured log with a stable prefix for grep. Fires only on
        // gate-mutating contradictions (above threshold), so the log
        // frequency is a first-class research signal for how often Ani's
        // own generation contradicts her confirmed substrate.
        _log.LogWarning(
            "SUBSTRATE_CONTRADICTION producer={Producer} confidence={Confidence:F2} threshold={Threshold:F2} quote=\"{Quote}\" reason=\"{Reason}\"",
            artifact.ProducerKind, verdict.Confidence, threshold, quote, reason);

        var hint = $"contradicts confirmed substrate: \"{quote}\"";
        // PR #147 Devin review-fix (2026-08-25): hard fail. Regenerating
        // rarely helps for grounding conflicts — the substrate is
        // unchanged so a regen tends to reproduce the same conflict.
        // Hard fail encodes "drop, don't regen" so a future consumer
        // that respects the Remediate vs Fail distinction doesn't re-
        // roll a contradicted artifact.
        return InvariantResult.Fail(hint, hard: true);
    }
}
