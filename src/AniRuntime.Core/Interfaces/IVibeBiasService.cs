using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Vibe Loop V1.5 (May 2, 2026) — retrieval-time biasing surface.
/// Reads recent <see cref="ClosedConversationRecord"/> instances from
/// <see cref="IClosedConversationStore"/>, computes importance-weighted
/// decay-adjusted bias contributions per V1.5.0 Lever 1, filters by
/// similarity-to-current-state per V1.5.0 Lever 2, applies MMR-style
/// diversity rerank, and returns a <see cref="VibeBiasResult"/>.
///
/// V1.5a wires this in observational-only — the result is logged for
/// telemetry but the prompt does NOT consume it. V1.5b activates
/// prompt consumption gated on the V1.5a observation window producing
/// a fat-tailed diversity histogram and a measured pre-bias correlation
/// baseline.
///
/// **Self-regulation framing (V1.5.0 Lever 3):** the outcome valence
/// driving the bias is sourced from Ani's register delta over each
/// closed conversation, never Mark's. This is the designed asymmetry
/// against the manipulation / mirroring failure mode the V1.5 plan
/// names as a Paper 3 contribution candidate.
/// </summary>
public interface IVibeBiasService
{
    /// <summary>
    /// Compute the V1.5 retrieval bias for the given contact at the
    /// given current state.
    /// </summary>
    /// <param name="contactName">
    /// Currently always <c>Roles.Mark</c>; parameter exists for future
    /// multi-contact extension. Records are not contact-scoped in V1
    /// (Mark is the only correspondent), so this is currently
    /// informational only.
    /// </param>
    /// <param name="contextNow">
    /// Current Mark-register vector at composition time, plus the
    /// timestamp to compute decay against.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<VibeBiasResult> ComputeBiasAsync(
        string                contactName,
        MarkRegisterContext   contextNow,
        CancellationToken     ct = default);
}
