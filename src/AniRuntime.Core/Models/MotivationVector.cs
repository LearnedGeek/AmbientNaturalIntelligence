namespace AniRuntime.Core.Models;

/// <summary>
/// Agentic Lens Layer 2 Phase 2a (Apr 2026): three-axis motivation score
/// drawn from Ryan &amp; Deci Self-Determination Theory.
///
/// Parallel to <see cref="AniRuntime.Core.MotivationScorer.Score"/>'s scalar
/// output — same composition for <see cref="Relatedness"/>, plus new
/// <see cref="Autonomy"/> and <see cref="Competence"/> axes computed from
/// the same per-cycle inputs. Phase 2a emits this vector for logging only;
/// the cognitive cycle still drives drift from <see cref="Relatedness"/>
/// alone. Phase 2b extends <see cref="DesireState"/> to drift all three
/// axes in parallel; Phase 2c wires consumption actions.
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
/// formulas and design rationale.
/// </summary>
public readonly record struct MotivationVector(
    float Relatedness,
    float Autonomy,
    float Competence);
