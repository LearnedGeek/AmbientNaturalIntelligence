namespace AniRuntime.Core.Models;

/// <summary>
/// Vibe Loop V1.5 (May 2, 2026) — output of
/// <c>IVibeBiasService.ComputeBiasAsync</c>. Surfaces both the full
/// candidate set (for telemetry / dashboard diversity-histogram
/// rendering) and the post-MMR-rerank top-N (for V1.5b prompt insertion;
/// in V1.5a the top-N is logged but not consumed).
///
/// <see cref="RecommendedStrategyRegister"/> is the weighted-average
/// 9-register vector across <see cref="SurfacedTopN"/> — used in V1.5a
/// telemetry to log the divergence triple
/// <c>(mood_register, vibe_recommended_strategy_register, response_register_actual)</c>.
/// </summary>
public sealed record VibeBiasResult(
    IReadOnlyList<VibeBiasContribution> AllCandidates,
    IReadOnlyList<VibeBiasContribution> SurfacedTopN,
    float[]                             RecommendedStrategyRegister,
    string                              DiversityScoreReason);
