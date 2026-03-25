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
/// </summary>
public static class MotivationScorer
{
    /// <summary>
    /// Computes a motivation multiplier for a thought based on existing signals.
    /// Returns a value in [0.3, 1.5] — no thought is fully ignored (0.3 min),
    /// and exceptional thoughts get up to 1.5x desire acceleration.
    /// </summary>
    public static float Score(float relationalValence, float severity, EmotionalState emotions)
    {
        // 1. Relevance: high absolute valence = thought is about the contact
        //    Valence near 0.5 = neutral. Distance from 0.5 = relational intensity.
        var relevance = Math.Abs(relationalValence - 0.5f) * 2f; // 0.0–1.0

        // 2. Information gap: high severity = emotionally significant (novel, surprising)
        //    Low severity = routine ambient thought (common, repetitive)
        var novelty = severity; // 0.0–1.0

        // 3. Expected impact: compound of warmth + playfulness indicates
        //    the thought is in a positive relational register that the contact
        //    would appreciate hearing about. Worry without warmth suggests
        //    anxiety rather than care — less shareable.
        var warmthAbove = Math.Max(0f, emotions.Warmth - emotions.WarmthBaseline);
        var playAbove = Math.Max(0f, emotions.Playfulness - emotions.PlayfulnessBaseline);
        var impact = Math.Min(1f, (warmthAbove + playAbove) * 1.5f);

        // Weighted composite: relevance matters most (the thought is about them),
        // novelty second (it's worth sharing), impact third (it would land well)
        var motivation = (relevance * 0.45f) + (novelty * 0.30f) + (impact * 0.25f);

        // Map [0, 1] to [0.3, 1.5] — linear scaling
        // 0.0 motivation → 0.3x (still some drift, just reduced)
        // 0.5 motivation → 0.9x (near-normal drift)
        // 1.0 motivation → 1.5x (accelerated drift)
        return 0.3f + (motivation * 1.2f);
    }
}
