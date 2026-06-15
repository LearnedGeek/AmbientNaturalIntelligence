using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// H phase (2026-06-12) — relevance filter for the substrate-thinness routing
/// architecture (dual-path composition).
///
/// **The architectural problem:** the existing composer always produces a
/// reply, regardless of whether retrieval surfaced substrate that actually
/// answers the user's message. When substrate is semantically similar but
/// topically irrelevant, the model fills the gap with invention. The
/// post-composition verifier catches some of these; the rest reach Mark
/// as confabulated content.
///
/// **The architectural shape:** before composition, the relevance filter
/// runs a lightweight binary judgment — "does the retrieved substrate
/// actually contain enough specific, relevant information to ground a
/// high-quality response to the user's message?" The routing decision
/// happens BEFORE the generator commits to a path.
///
/// - <see cref="RelevanceVerdict.Yes"/> → normal composer (full lean prompt + gist)
/// - <see cref="RelevanceVerdict.No"/>  → safe-path composer (honest-uncertainty, NO gist)
/// - <see cref="RelevanceVerdict.Unknown"/> → fail-open route to normal (filter call broke; log WARN, don't break UX)
///
/// **Failure mode:** implementations SHOULD return <see cref="RelevanceVerdict.Unknown"/>
/// on transport / parse / timeout errors rather than throwing. The pipeline
/// treats Unknown as fail-open (route to normal composer) because breaking
/// the conversation flow is worse UX than occasionally letting questionable
/// substrate reach the composer (the verifier still runs downstream).
///
/// **Empirical anchor:** 2026-06-11 puzzle-turn (heart-shaped lock) where
/// retrieval surfaced 5 Facts at high cosine (0.779) but none answered the
/// puzzle question. The composer invented "your students would love that one"
/// to fill the gap. The 3-arm test that same day showed empty-FACTS + safe-path
/// composer produces honest-uncertainty + ask-back reliably across runs.
///
/// **Sister to <see cref="IFrontierVerifierClient"/>:** the verifier is the
/// POST-composition gate; the relevance filter is the PRE-composition gate.
/// They protect different failure classes and run on every dispatch.
/// </summary>
public interface IRelevanceFilter
{
    /// <summary>
    /// Judge whether the retrieved substrate is sufficient to ground a
    /// high-quality response to the user's message.
    /// </summary>
    /// <param name="userMessage">The user's most recent inbound text.</param>
    /// <param name="facts">The substrate the composer would otherwise see.
    /// Typically <see cref="ContextSnapshot.GroundedFacts"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="RelevanceVerdict.Yes"/> or <see cref="RelevanceVerdict.No"/>
    /// on a successful judgment call. <see cref="RelevanceVerdict.Unknown"/>
    /// when the filter call failed (transport / parse / timeout); callers
    /// treat Unknown as fail-open.
    /// </returns>
    Task<RelevanceVerdict> JudgeAsync(
        string                       userMessage,
        IReadOnlyList<MemoryRecord>  facts,
        CancellationToken            ct);
}

/// <summary>
/// H phase (2026-06-12) — relevance filter verdict shape.
///
/// <list type="bullet">
///   <item><see cref="Yes"/> — substrate is sufficient; route to normal composer with full lean prompt + gist injection</item>
///   <item><see cref="No"/>  — substrate is insufficient; route to safe-path composer (honest-uncertainty, no gist)</item>
///   <item><see cref="Unknown"/> — filter call failed; pipeline fails open to normal composer with WARN log</item>
/// </list>
/// </summary>
public enum RelevanceVerdict
{
    /// <summary>Substrate contains specific, relevant information sufficient to ground a high-quality response.</summary>
    Yes,

    /// <summary>Substrate is thin, irrelevant, or vague; safe-path composer is the honest route.</summary>
    No,

    /// <summary>Filter call did not complete successfully. Caller fails open (route to normal composer + log WARN).</summary>
    Unknown,
}
