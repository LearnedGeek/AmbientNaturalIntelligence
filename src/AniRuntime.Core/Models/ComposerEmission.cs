using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Foundation Unified Surface (F-3) U2 (2026-08-24) — default concrete
/// implementation of <see cref="IComposerEmission{T}"/>. Composers use
/// this record to return their emission envelope unless they need a
/// bespoke type.
///
/// <para>
/// <b>Why a default concrete type.</b> The F-1 envelope pattern
/// (Phase 8a–8f) gave each producer its own concrete envelope type
/// (<c>WorldSeedEnvelope</c>, <c>OutreachDecisionEnvelope</c>, etc.).
/// That level of ceremony fits when each producer wraps a distinct
/// payload shape. Composer emissions almost always wrap a
/// <c>string</c> (the LLM's prose output), so one generic default
/// covers most composers cleanly. A composer that needs a bespoke
/// emission type (e.g., reflection returning a structured summary
/// record) is free to declare its own type implementing
/// <see cref="IComposerEmission{T}"/> directly.
/// </para>
///
/// <para>
/// <b>Property order</b> matches the interface for readability at call
/// sites; construction is positional so migrations don't have to worry
/// about optional fields when they're not needed.
/// </para>
/// </summary>
public sealed record ComposerEmission<T>(
    T                     Content,
    CognitiveProducerKind ComposerRole,
    DateTimeOffset        EmittedAt,
    AttributedTo          AttributedTo,
    string                AttributionTrust,
    string?               AttributedSourceDescriptor = null)
    : IComposerEmission<T>;

/// <summary>
/// Foundation Unified Surface (F-3) U2 (2026-08-24) — default concrete
/// implementation of <see cref="IClaimBearingEmission{T}"/>. Composers
/// that emit structured output including per-claim attribution (inner-
/// thought and reflection) use this record unless they need a bespoke
/// type.
///
/// <para>
/// <b>Empty <see cref="Claims"/> is valid</b> — a claim-bearing composer
/// that emits self-content only on a given cycle returns an empty list.
/// Consumers should not treat empty-vs-non-empty as a state change
/// signal; the composer declares what it declares.
/// </para>
/// </summary>
public sealed record ClaimBearingEmission<T>(
    T                            Content,
    CognitiveProducerKind        ComposerRole,
    DateTimeOffset               EmittedAt,
    AttributedTo                 AttributedTo,
    string                       AttributionTrust,
    IReadOnlyList<ContentClaim>  Claims,
    string?                      AttributedSourceDescriptor = null)
    : IClaimBearingEmission<T>;
