namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme P Phase P.1 (May 11, 2026) — cross-class verification client.
///
/// Per the Theme P plan-doc §1, the architectural insight is that a model
/// that generates a claim with high confidence will verify that same claim
/// with high confidence — they share the same weights. Cross-class
/// independence means the verifier comes from a different training lineage
/// than the generator so confidence patterns do not transfer.
///
/// **Architectural shape (corrected May 11 21:36 CDT — see plan-doc §9.1):**
/// local generator + local judgment gates remain unchanged; the cloud
/// verifier is ADDED as defense-in-depth. The
/// <c>FrontierVerifierHandler</c> consumes this interface as a separate
/// Post-stage <c>ICognitivePipelineHandler</c> that runs in parallel to
/// the existing local invariants. Not a replacement path. Not a fallback
/// path. Both stacks fire on every dispatch.
///
/// **Failure mode = graceful degradation.** Implementations SHOULD throw
/// on transport / parse / timeout errors so the
/// <c>FrontierVerifierHandler</c> can swallow the failure and return
/// <c>Continue</c>. The local judgment gates remain active on the same
/// dispatch — cloud absence reduces defense-in-depth by one layer rather
/// than skipping judgment entirely. The contract is: success = a
/// structured verdict; any unrecoverable failure = exception.
/// </summary>
public interface IFrontierVerifierClient
{
    /// <summary>
    /// Run the consolidated cross-class verification call. Returns a
    /// <see cref="FrontierVerifierResult"/> capturing all five
    /// per-question verdicts plus an aggregated summary verdict.
    /// </summary>
    Task<FrontierVerifierResult> VerifyAsync(
        FrontierVerifierRequest request,
        CancellationToken       ct);
}

/// <summary>
/// Theme P Phase P.1 (May 11, 2026) — verification request payload.
/// Mirrors plan-doc §3: the verifier receives the composed message + a
/// pre-formatted Mark-asserted substrate block + a pre-formatted canonical
/// substrate block + current context + addressee. Substrate strings are
/// pre-rendered by the handler so the client never has to know about
/// <c>MemoryRecord</c> shapes.
/// </summary>
/// <param name="ComposedMessage">The artifact's content (the message the
/// generator produced and the dispatch pipeline is about to send).</param>
/// <param name="MarkAssertedSubstrate">Pre-formatted Mark-asserted records
/// from <c>ContextSnapshot.GroundedFacts</c> (Facts-tier: character seeds,
/// perception events, user-asserted content). One string with each record
/// on its own line, recency-first, capped to top 10. Empty string when
/// the snapshot's Facts-tier pool is empty — no fallback to other
/// substrate sources (plan-doc §9.1).</param>
/// <param name="CanonicalSubstrate">Pre-formatted records from
/// <c>ContextSnapshot.AnchoredMemories</c> (foundation memories that
/// never fade — character seeds, world layer canon). One string with each
/// record on its own line, capped to 10. Empty string when no anchored
/// records exist.</param>
/// <param name="CurrentTime">Wall-clock time the artifact was produced.</param>
/// <param name="CurrentDayOfWeek">Day of week derived from the local-time
/// projection of <see cref="CurrentTime"/>. Passed explicitly so the
/// verifier doesn't have to interpret an ISO 8601 stamp.</param>
/// <param name="AddresseeCanonicalName">The canonical contact name the
/// message is addressed to (typically <c>"Mark"</c>). Sourced from
/// <c>ContextSnapshot.CharacterState.PrimaryContactName</c>.</param>
/// <param name="KnownContacts">Comma-separated list of canonical contact
/// names from <c>ContextSnapshot.CharacterState.CanonicalContacts</c>.
/// Empty string when none are seeded.</param>
public sealed record FrontierVerifierRequest(
    string         ComposedMessage,
    string         MarkAssertedSubstrate,
    string         CanonicalSubstrate,
    DateTimeOffset CurrentTime,
    string         CurrentDayOfWeek,
    string         AddresseeCanonicalName,
    string         KnownContacts);

/// <summary>
/// Theme P Phase P.1 (May 11, 2026) — verification response payload.
///
/// <see cref="AnyViolation"/> mirrors the plan-doc §3 aggregation rule:
/// if any question's <c>violation</c> is true, the handler short-circuits
/// with <c>Remediate</c>. <see cref="SummaryVerdict"/> is the verifier's
/// own aggregated verdict — one of <c>"pass"</c>, <c>"remediate"</c>, or
/// <c>"fail"</c>. The handler uses <see cref="AnyViolation"/> as the
/// authoritative gate signal (matches §3) and <see cref="SummaryVerdict"/>
/// is surfaced as additional telemetry.
/// </summary>
public sealed record FrontierVerifierResult(
    bool                            AnyViolation,
    IReadOnlyList<QuestionVerdict>  Questions,
    string                          SummaryVerdict,
    string?                         AggregatedReason);

/// <summary>
/// Theme P Phase P.1 (May 11, 2026) — per-question verdict from the
/// cross-class verifier. Each of the five plan-doc §3 questions produces
/// one of these. <see cref="Quote"/> is the offending span from the
/// composed message (null when no violation); <see cref="Reason"/> is the
/// verifier's free-text explanation (null when no violation).
/// </summary>
/// <param name="QuestionNumber">1–5, matching the question order in §3.</param>
/// <param name="QuestionLabel">Short tag identifying the question:
/// <c>"shared-event"</c> (q1), <c>"present-tense-state"</c> (q2),
/// <c>"third-party-reference"</c> (q3), <c>"temporal-claim"</c> (q4),
/// <c>"inner-thought-bleed"</c> (q5).</param>
/// <param name="Violation">True when this question's check failed.</param>
/// <param name="Quote">The offending span from the composed message
/// (verbatim, as the verifier extracted it). Null on no-violation.</param>
/// <param name="Reason">Verifier's free-text explanation. Null on
/// no-violation.</param>
public sealed record QuestionVerdict(
    int     QuestionNumber,
    string  QuestionLabel,
    bool    Violation,
    string? Quote,
    string? Reason);
