using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Unified Surface (F-3) U4 (2026-08-24) — extracts per-quote
/// attribution claims from an already-composed inner thought. Implementations
/// use a general-purpose model (Qwen 14B in production) rather than the
/// Ani fine-tune because the fine-tune fights structured output shape (see
/// <c>InnerThoughtClaimExtractionPromptCommand</c> XML doc for the empirical
/// justification).
///
/// <para>
/// <b>Contract.</b> Given a composed thought, return zero or more
/// <see cref="ContentClaim"/> entries describing embedded attributions.
/// An empty list is a valid result — means the thought contains no
/// speaker-ascribed claims (pure self-reflection or sensory description).
/// The extractor MUST NOT throw on parse failure or model unavailability;
/// return an empty list and log the failure so the cognitive cycle
/// continues unimpeded. Same fail-open contract as
/// <see cref="IRegisterClassifier"/> and the metadata recognizer.
/// </para>
///
/// <para>
/// <b>Source-record grounding is deferred.</b> Returned claims carry
/// <c>SourceRecordId: null</c> + <c>AttributionTrust: "unverified"</c>.
/// Resolving claims against substrate records happens downstream (deferred
/// per F-3 design plan Q3 to a later phase).
/// </para>
/// </summary>
public interface IInnerThoughtClaimExtractor
{
    /// <summary>
    /// Extract attribution claims from the given thought.
    /// </summary>
    /// <param name="thought">The composed inner thought.</param>
    /// <param name="characterName">The character whose thought this is (for disambiguation).</param>
    /// <param name="contactName">The primary contact name (for second-person / by-name references).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Extracted claims. Empty list is a valid result.</returns>
    Task<IReadOnlyList<ContentClaim>> ExtractAsync(
        string            thought,
        string            characterName,
        string            contactName,
        CancellationToken ct = default);
}
