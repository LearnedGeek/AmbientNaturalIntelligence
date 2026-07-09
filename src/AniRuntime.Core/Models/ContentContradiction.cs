namespace AniRuntime.Core.Models;

/// <summary>
/// Issue #93 Phase 3 (2026-07-06) — self-audit verdict returned by
/// <see cref="Interfaces.IContentContradictionClassifier"/>. Answers the
/// question "does this Interior content contradict anything in Ani's
/// confirmed substrate?"
///
/// Same shape as <see cref="TagIntentVerdict"/> — three-outcome enum with
/// confidence scalar and reason. Both classifiers use the identical
/// LLM-plus-structured-JSON pattern; only the question and answer bucket
/// change.
///
/// **Downstream consumers** (both future; interface exists now so tests
/// stay strict-mock-clean when they land):
/// <list type="bullet">
///   <item><c>SubstrateConsistencyInvariant</c> — write-side invariant on
///     <c>CognitiveOutputGate</c> that flags a new Interior artifact as
///     <c>validity='invalid_contradiction'</c> when a contradiction is
///     detected. Producer-side per Theme J discipline.</item>
///   <item><c>--audit-interior</c> eval mode — sweep tool that runs the
///     same check retroactively against existing 20,626 Interior records
///     and applies the same invalidity flag. Preserves records for
///     research per Mark's 2026-07-06 directive.</item>
/// </list>
/// </summary>
public enum ContradictionOutcome
{
    /// <summary>
    /// Classifier failed / timed out / returned unparseable output. Caller
    /// treats as fail-open: DO NOT invalidate on Unknown — a contradiction
    /// call must be positive to trigger the substrate mutation. Matches
    /// the fail-open discipline of every other classifier in the runtime.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The Interior content is consistent with (or at least not contradicted
    /// by) the confirmed substrate shown to the classifier. No mutation.
    /// </summary>
    Grounded = 1,

    /// <summary>
    /// The Interior content directly contradicts one or more of the
    /// confirmed substrate records shown. The <see cref="ContentContradictionVerdict.Quote"/>
    /// field carries the specific contradiction the LLM identified so
    /// audit + Emergence can see WHAT contradicted WHAT.
    /// </summary>
    Contradicts = 2,

    /// <summary>
    /// The Interior content is orthogonal to the confirmed substrate — it
    /// makes no factual claims that could be checked against the shown
    /// substrate. Pure emotional / register / atmospheric content. No
    /// mutation. Distinct from <see cref="Grounded"/>: Neutral means
    /// "nothing to check" rather than "checked and consistent."
    /// </summary>
    Neutral = 3,
}

/// <summary>
/// Structured verdict from <see cref="Interfaces.IContentContradictionClassifier"/>.
/// Shape mirrors <see cref="TagIntentVerdict"/> with an extra
/// <see cref="Quote"/> field carrying the specific contradiction excerpt
/// when <see cref="Outcome"/> is <see cref="ContradictionOutcome.Contradicts"/>.
/// </summary>
/// <param name="Outcome">Classified contradiction verdict.</param>
/// <param name="Confidence">Model-reported confidence in [0.0, 1.0]. Low
/// confidence should route to no-mutation at the caller boundary.</param>
/// <param name="Quote">When <see cref="Outcome"/> is
/// <see cref="ContradictionOutcome.Contradicts"/>, the specific
/// confirmed-substrate excerpt the LLM identified as contradicting the
/// Interior content. Null on Grounded / Neutral / Unknown.</param>
/// <param name="Reason">One-line justification the classifier emitted,
/// kept for audit + eval-harness inspection.</param>
public sealed record ContentContradictionVerdict(
    ContradictionOutcome Outcome,
    float                Confidence,
    string?              Quote,
    string?              Reason);
