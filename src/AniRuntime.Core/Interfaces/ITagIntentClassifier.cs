using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Issue #93 Phase 2 (2026-07-06) — LLM-backed classifier that reads Mark's
/// <c>///tag &lt;note&gt;</c> input in the context of the Ani reply it
/// anchors against and returns a structured
/// <see cref="TagIntentVerdict"/>.
///
/// **Why an interface.** The regex site at <c>TagCommand.cs:109</c> was a
/// single-consumer classification — the interface exists to enforce
/// strict-mock discipline in unit tests (per Theme K methodology) and to
/// let the eval harness call the same production classifier from
/// <c>AniRuntime.Eval --classify-tag</c>.
///
/// **Failure contract.** Any transport / parse / timeout failure returns
/// <see cref="TagIntent.Unknown"/> — never throws. Caller treats Unknown as
/// fail-open: save the flag for audit, apply no substrate mutation. Matches
/// the routing-classifier / verifier fallback shape (both fail-open on
/// downstream errors).
/// </summary>
public interface ITagIntentClassifier
{
    /// <summary>
    /// Classify Mark's tag note against the anchored Ani reply.
    /// </summary>
    /// <param name="tagNote">The trimmed post-<c>///tag </c> content Mark
    /// typed. Free-form. Never null / empty (caller filters those before
    /// reaching this method).</param>
    /// <param name="aniReply">The last Ani reply that the tag anchored
    /// against, verbatim. Never null / empty.</param>
    /// <param name="markPriorMessage">Mark's message immediately prior to
    /// <paramref name="aniReply"/>, verbatim. Empty string when no prior
    /// message could be located (out-of-band tag).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TagIntentVerdict> ClassifyAsync(
        string tagNote,
        string aniReply,
        string markPriorMessage,
        CancellationToken ct);
}
