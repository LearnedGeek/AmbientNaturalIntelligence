using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Issue #93 Phase 3 (2026-07-06) — LLM-backed self-audit classifier that
/// reads a piece of Interior content in the context of Ani's confirmed
/// substrate and returns a structured
/// <see cref="ContentContradictionVerdict"/>.
///
/// **Motivation.** Mark's 2026-07-06 architectural insight: Ani should
/// self-audit — if an inner thought contradicts confirmed substrate, the
/// substrate wins and the inner thought is invalidated (kept for research,
/// excluded from retrieval). Same shape as
/// <see cref="ITagIntentClassifier"/>: interface exists to enforce strict-
/// mock discipline in tests and let the eval harness call the production
/// classifier from <c>AniRuntime.Eval --classify-contradiction</c>.
///
/// **Failure contract.** Any transport / parse / timeout failure returns
/// <see cref="ContradictionOutcome.Unknown"/> — never throws. Caller
/// treats Unknown as fail-open: preserve the content, apply no invalidity
/// flag. Contradiction detection MUST be positive to mutate — same
/// asymmetry as tag-intent (an "invalidate" verdict has to be high-
/// confidence to touch validity).
/// </summary>
public interface IContentContradictionClassifier
{
    /// <summary>
    /// Check <paramref name="interiorContent"/> against
    /// <paramref name="confirmedSubstrate"/>.
    /// </summary>
    /// <param name="interiorContent">The Interior record's raw content
    /// (inner thought / reflection / world-experience). Verbatim.</param>
    /// <param name="confirmedSubstrate">Confirmed substrate records —
    /// typically the top-K semantic neighbors from Facts + Episodic.
    /// Formatted verbatim as one string, one record per line prefixed
    /// with <c>- </c>. Empty string is legal (there ARE Interior records
    /// with no confirmed neighbors above cosine threshold); the
    /// classifier will return <see cref="ContradictionOutcome.Neutral"/>
    /// or <see cref="ContradictionOutcome.Grounded"/> in that case
    /// since there is nothing to contradict.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ContentContradictionVerdict> ClassifyAsync(
        string            interiorContent,
        string            confirmedSubstrate,
        CancellationToken ct);

    /// <summary>
    /// Batched variant — classify N Interior/substrate pairs in a single
    /// LLM call. Used by the retroactive sweep to amortize per-call
    /// latency (~2-4s per call on qwen3:14b) across multiple records.
    /// Empirical throughput on a batch=5 configuration cuts a ~17h serial
    /// sweep of 20k records to ~4h.
    ///
    /// <para>Return order matches input order exactly. If the batch call
    /// fails or the model returns fewer verdicts than requested, missing
    /// slots receive <see cref="ContradictionOutcome.Unknown"/> so the
    /// caller can retry those records individually or fail-open.</para>
    ///
    /// <para>Caller is responsible for keeping the batch small enough that
    /// the composed prompt fits comfortably inside the model's context
    /// window (32K on qwen3:14b — batches of 5-10 are safe for typical
    /// Interior + top-K substrate sizes).</para>
    /// </summary>
    Task<IReadOnlyList<ContentContradictionVerdict>> ClassifyBatchAsync(
        IReadOnlyList<(string InteriorContent, string ConfirmedSubstrate)> items,
        CancellationToken ct);
}
