namespace AniRuntime.Core.Models;

/// <summary>
/// Vibe Loop V1.5 (May 2, 2026) — one closed-conversation record's
/// contribution to the retrieval-time bias function.
///
/// Importance × exp(-age / halfLife) is the bias weight; importance is
/// derived from turn count, outcome valence magnitude, and register
/// saturation depth (V1.5.0 Lever 1: importance-weighted decay mirroring
/// <see cref="EmotionalContribution"/> tiers).
///
/// <see cref="MarkRegister"/> is preserved on the contribution so the
/// MMR-style diversity rerank (V1.5.0 Lever 1, similarity threshold 0.85)
/// can compare candidates without re-loading the underlying record.
///
/// <see cref="OutcomeValence"/> is sourced from Ani's register delta
/// (V1.5.0 Lever 3: self-regulation framing). Mark's register delta is
/// observable in telemetry only and never enters this contribution
/// surface — designed asymmetry against the manipulation / mirroring
/// failure mode.
/// </summary>
public sealed record VibeBiasContribution(
    Guid     RecordId,
    float    BiasWeight,
    float    Importance,
    string   HalfLifeTier,
    float    AgeHours,
    float[]  MarkRegister,
    float    OutcomeValence);
