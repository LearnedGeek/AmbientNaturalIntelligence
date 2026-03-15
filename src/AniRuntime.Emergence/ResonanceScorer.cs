using AniRuntime.Core.Models;

namespace AniRuntime.Emergence;

/// <summary>
/// Computes a scalar ResonanceScore (0.0–1.0) for each cognitive cycle.
/// Initial formula uses equal weights across four components — tuned via
/// EmergenceLog analysis once enough data accumulates.
///
/// E1 starts with pattern_match = 0 (no known resonances yet). The first
/// weeks of data calibrate the baseline. This is by design.
/// </summary>
public static class ResonanceScorer
{
    /// <summary>
    /// Score a single cycle observation. Pure function — no side effects.
    /// </summary>
    public static float Score(CycleObservation obs)
    {
        var emotional  = EmotionalMagnitude(obs);
        var novelty    = NoveltySignal(obs);
        var outreach   = OutreachQuality(obs);
        var relational = RelationalSignal(obs);

        // Equal weights initially — tunable after E1 calibration
        return Math.Clamp(
            0.25f * emotional +
            0.25f * novelty +
            0.25f * outreach +
            0.25f * relational,
            0f, 1f);
    }

    /// <summary>
    /// Max absolute delta across the four emotional dimensions, normalized.
    /// A cycle with large emotional movement scores high.
    /// </summary>
    internal static float EmotionalMagnitude(CycleObservation obs)
    {
        var maxDelta = Math.Max(
            Math.Max(Math.Abs(obs.WarmthDelta), Math.Abs(obs.EnergyDelta)),
            Math.Max(Math.Abs(obs.WorryDelta), Math.Abs(obs.PlayfulnessDelta)));

        // Normalize: 0.35 delta = 1.0 (Global tier max). Clamp to 0–1.
        return Math.Clamp(maxDelta / 0.35f, 0f, 1f);
    }

    /// <summary>
    /// Novelty based on inner thought presence and valence extremity.
    /// High valence (positive or negative) signals something noteworthy.
    /// Absence of inner thought = low novelty.
    /// </summary>
    internal static float NoveltySignal(CycleObservation obs)
    {
        if (string.IsNullOrEmpty(obs.InnerThought))
            return 0f;

        // High absolute valence = more novel/noteworthy
        var valenceSignal = Math.Abs(obs.RelationalValence);

        // Reflection presence adds novelty — the cycle was rich enough to reflect
        var reflectionBonus = obs.Reflection is not null ? 0.2f : 0f;

        return Math.Clamp(valenceSignal + reflectionBonus, 0f, 1f);
    }

    /// <summary>
    /// Outreach quality: did the cycle produce meaningful external action?
    /// </summary>
    internal static float OutreachQuality(CycleObservation obs)
    {
        if (obs.OutreachOutcome == "send")
            return 1.0f;

        // Conversation reply is meaningful action
        if (obs.WasConversationCycle && obs.ReplyMessage is not null)
            return 0.8f;

        // Desire crossed threshold but was suppressed — notable tension
        if (obs.DesireThresholdCrossed && obs.OutreachOutcome is not null)
            return 0.4f;

        // Silence choice — meaningful restraint
        if (obs.ChoseSilence)
            return 0.3f;

        return 0f;
    }

    /// <summary>
    /// Relational signal: conversation exchanges, high desire, or high severity
    /// indicate relational significance.
    /// </summary>
    internal static float RelationalSignal(CycleObservation obs)
    {
        var score = 0f;

        // Direct conversation is inherently relational
        if (obs.WasConversationCycle)
            score += 0.5f;

        // High desire = relational pressure
        if (obs.DesireToConnect > 0.7f)
            score += 0.3f;

        // High severity = emotionally significant
        if (obs.Severity > 0.7f)
            score += 0.3f;

        return Math.Clamp(score, 0f, 1f);
    }
}
