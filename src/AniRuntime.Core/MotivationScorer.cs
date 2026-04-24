using AniRuntime.Core.Models;

namespace AniRuntime.Core;

/// <summary>
/// Feature 33 (Liu et al. 2025): Motivation scoring for inner thoughts.
///
/// Each thought is scored on three dimensions (adapted from Liu et al.'s
/// proactive agent framework):
///   1. Relevance — how connected is this thought to the contact?
///   2. Information gap — is this thought novel or just repetition?
///   3. Expected impact — would sharing this thought deepen the relationship?
///
/// Unlike the original paper's approach (LLM-scored), this implementation
/// derives motivation from signals already computed in the cognitive cycle:
/// relational valence, emotional severity, and emotional state. This avoids
/// adding an extra LLM call per cycle, consistent with the pipeline
/// simplification principle (March 23, 2026).
///
/// The motivation score (0.0–1.5) multiplies the desire drift for that cycle.
/// High-motivation thoughts accelerate desire; low-motivation thoughts
/// contribute less to outreach pressure.
///
/// Agentic Lens Layer 2 Phase 2a (Apr 2026): <see cref="ScoreVector"/>
/// returns a three-axis <see cref="MotivationVector"/> drawn from Ryan &amp;
/// Deci SDT (relatedness / autonomy / competence). The existing scalar
/// <see cref="Score"/> is preserved verbatim and now delegates to the
/// <see cref="MotivationVector.Relatedness"/> component — identical
/// behaviour, no drift-math change. Phase 2a emits the vector for logging
/// only; Phases 2b–2d consume the non-relatedness axes.
/// </summary>
public static class MotivationScorer
{
    /// <summary>
    /// Computes a motivation multiplier for a thought based on existing signals.
    /// Returns a value in [0.3, 1.5] — no thought is fully ignored (0.3 min),
    /// and exceptional thoughts get up to 1.5x desire acceleration.
    ///
    /// Preserved verbatim as the relatedness-only scalar API. Delegates to
    /// <see cref="ScoreVector"/> with <c>worldOriginFraction = 0</c> since
    /// competence is not observable from the scalar call site.
    /// </summary>
    public static float Score(float relationalValence, float severity, EmotionalState emotions)
        => ScoreVector(relationalValence, severity, emotions, worldOriginFraction: 0f).Relatedness;

    /// <summary>
    /// Computes a three-axis motivation vector for a thought.
    ///
    ///   - Relatedness ∈ [0.3, 1.5]: caregiver-directed motivation.
    ///     Same composition as the existing scalar — distance-from-neutral
    ///     valence × 0.45 + severity × 0.30 + emotional impact × 0.25,
    ///     mapped from [0, 1] into [0.3, 1.5].
    ///
    ///   - Autonomy ∈ [0, 1.5]: self-state-directed motivation. Rises
    ///     when the thought carries emotional intensity (severity &gt; 0)
    ///     but low caregiver-valence (near-neutral relational valence).
    ///     Inverse pattern of Relatedness on the valence axis, weighted
    ///     by severity. See plan §2.2.
    ///
    ///   - Competence ∈ [0, 1.5]: world-engagement motivation. Rises
    ///     proportionally with the fraction of the retrieval pool
    ///     classified as <see cref="RetrievalOrigin.World"/> (Layer 1
    ///     classifier). When no World-origin memories are present the
    ///     competence axis contributes zero.
    ///
    /// All three axes normalise to share the drift-modulator contract at
    /// the DesireEngine layer. <paramref name="worldOriginFraction"/> is
    /// expected in [0, 1]; values outside are clamped.
    /// </summary>
    public static MotivationVector ScoreVector(
        float relationalValence,
        float severity,
        EmotionalState emotions,
        float worldOriginFraction)
    {
        // ── Shared signals used across axes ────────────────────────────────
        // Distance from neutral valence — high when thought is clearly about
        // the contact (love or dislike), near zero when thought is detached.
        var valenceIntensity = Math.Abs(relationalValence - 0.5f) * 2f; // [0, 1]

        // Inverse: high when valence is near neutral.
        var valenceNeutrality = 1f - valenceIntensity; // [0, 1]

        var warmthAbove = Math.Max(0f, emotions.Warmth - emotions.WarmthBaseline);
        var playAbove   = Math.Max(0f, emotions.Playfulness - emotions.PlayfulnessBaseline);
        var positiveRelationalImpact = Math.Min(1f, (warmthAbove + playAbove) * 1.5f);

        // ── Relatedness — preserved from original Score() composition ──────
        // relevance × 0.45 + novelty × 0.30 + impact × 0.25, mapped [0,1] → [0.3, 1.5]
        var relatednessRaw = (valenceIntensity * 0.45f)
                           + (severity         * 0.30f)
                           + (positiveRelationalImpact * 0.25f);
        var relatedness = 0.3f + (relatednessRaw * 1.2f);

        // ── Autonomy — emotional intensity on non-caregiver subject ────────
        // High when the thought has emotional weight but is not about the
        // contact. Near zero when the thought is flat or clearly caregiver-
        // directed. Mapped [0, 1] → [0, 1.5].
        var autonomyRaw = severity * valenceNeutrality; // [0, 1]
        var autonomy = autonomyRaw * 1.5f;

        // ── Competence — presence of World-origin substrate in retrieval ───
        // Proportional to the fraction of the retrieval pool classified as
        // RetrievalOrigin.World. Zero when no World substrate is present.
        // Mapped [0, 1] → [0, 1.5].
        var clampedWorldFraction = Math.Clamp(worldOriginFraction, 0f, 1f);
        var competence = clampedWorldFraction * 1.5f;

        return new MotivationVector(relatedness, autonomy, competence);
    }
}
