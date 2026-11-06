namespace AniRuntime.Core.Models;

/// <summary>
/// Agentic Lens Layer 2 Phase 2a (Apr 2026): three-axis motivation score
/// drawn from Ryan &amp; Deci Self-Determination Theory.
///
/// Parallel to <see cref="AniRuntime.Core.MotivationScorer.Score"/>'s scalar
/// output — same composition for <see cref="Relatedness"/>, plus
/// <see cref="Autonomy"/> and <see cref="Competence"/> axes computed from
/// the same per-cycle inputs.
///
/// Live consumption as of 2026-11-05: Theme R.4 (commit <c>7ba5a62</c>,
/// 2026-05-24) wired the vector into three composer prompts via
/// <c>MotivationEmphasis.Select</c>, so the Phase 2a "emits this vector
/// for logging only" framing is no longer accurate — the vector now shapes
/// prompt composition.
///
/// What has NOT shipped: Phase 2b parallel desire drift. See
/// <c>CognitiveCyclePipeline.cs:613,640</c> — only
/// <see cref="Relatedness"/> is extracted and passed to
/// <c>DesireEngine.ApplyDriftAsync</c>; <see cref="Autonomy"/> and
/// <see cref="Competence"/> are recorded on the motivation tracker and
/// consumed by R.4 prompt emphasis, but do not drive
/// <c>DesireToConnect</c>. Desire drift remains relatedness-only.
///
/// See <c>AniRuntime.Core.MotivationScorer</c> for the construction-defect
/// note on the Autonomy axis (Life Engine Opus 5.5 review §Q2 — autonomy
/// and relatedness are anticorrelated by shared-scalar construction, not
/// by independent measurement).
///
/// Ranges:
///   - <see cref="Relatedness"/> ∈ [0.3, 1.5] — preserves existing scalar
///     contract; never fully zero so the caregiver axis always contributes
///     some drift per cycle (existing behaviour).
///   - <see cref="Autonomy"/>   ∈ [0.0, 1.5] — can be zero when no
///     self-state signal is present in the cycle.
///   - <see cref="Competence"/> ∈ [0.0, 1.5] — can be zero when the
///     retrieval pool contains no World-origin substrate.
///
/// See docs/spec/ANI-Agentic-Lens-Layer2-Plan.md §2.2 for the per-axis
/// formulas and design rationale, and <c>spec/ANI-Life-Engine-Design.md</c>
/// §6 Candidate A.−1 for the current observation-window gate.
/// </summary>
public readonly record struct MotivationVector(
    float Relatedness,
    float Autonomy,
    float Competence);
