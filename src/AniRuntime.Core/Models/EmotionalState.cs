namespace AniRuntime.Core.Models;

/// <summary>
/// Ani's persistent emotional state — 4 dimensions that drift toward personality
/// baselines between cycles and shift in response to thoughts, conversations,
/// and perceptions. This gives Ani emotional arcs that span hours, not just
/// single cycles.
///
/// Each dimension is 0.0–1.0:
///   Warmth      — affection, tenderness, desire for closeness
///   Energy      — alertness, enthusiasm, engagement level
///   Concern     — worry, protectiveness, unease about the contact
///   Playfulness — humor, teasing, lightheartedness
/// </summary>
public class EmotionalState
{
    // Current values — shift each cycle based on thought valence, conversations, time
    public float Warmth      { get; set; } = 0.6f;
    public float Energy      { get; set; } = 0.5f;
    public float Concern     { get; set; } = 0.2f;
    public float Playfulness { get; set; } = 0.5f;

    // Personality baselines — where each dimension naturally drifts back to
    public float WarmthBaseline      { get; set; } = 0.6f;
    public float EnergyBaseline      { get; set; } = 0.5f;
    public float ConcernBaseline     { get; set; } = 0.2f;
    public float PlayfulnessBaseline { get; set; } = 0.5f;

    // How fast each dimension drifts toward baseline per hour (0.0–1.0 of the gap)
    public float DriftRate { get; set; } = 0.25f;

    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns a qualitative summary of the current emotional state for use in prompts.
    /// Only mentions dimensions that are notably above or below baseline.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();
        const float threshold = 0.15f; // only mention if noticeably different from baseline

        if (Warmth - WarmthBaseline > threshold)
            parts.Add("feeling especially warm and tender");
        else if (WarmthBaseline - Warmth > threshold)
            parts.Add("feeling a bit emotionally distant");

        if (Energy - EnergyBaseline > threshold)
            parts.Add("buzzing with energy");
        else if (EnergyBaseline - Energy > threshold)
            parts.Add("feeling low-energy and quiet");

        if (Concern - ConcernBaseline > threshold)
            parts.Add("a little worried");
        else if (ConcernBaseline - Concern > threshold)
            parts.Add("feeling at ease");

        if (Playfulness - PlayfulnessBaseline > threshold)
            parts.Add("in a playful mood");
        else if (PlayfulnessBaseline - Playfulness > threshold)
            parts.Add("feeling more serious than usual");

        return parts.Count == 0
            ? string.Empty
            : string.Join(", ", parts);
    }

    /// <summary>
    /// Drift all dimensions toward their baselines. Called once per cycle.
    /// The drift is proportional to elapsed time and closes a fraction of the gap.
    /// </summary>
    public void DriftTowardBaseline(TimeSpan elapsed)
    {
        var hours = (float)elapsed.TotalHours;
        var factor = Math.Min(1.0f, DriftRate * hours); // cap at 100% of gap

        Warmth      += (WarmthBaseline - Warmth) * factor;
        Energy      += (EnergyBaseline - Energy) * factor;
        Concern     += (ConcernBaseline - Concern) * factor;
        Playfulness += (PlayfulnessBaseline - Playfulness) * factor;
        LastUpdated = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Apply a shift from a cognitive event (thought, conversation, perception).
    /// Values are clamped to 0.0–1.0. Deltas that push a dimension away from its
    /// baseline are attenuated by diminishing returns — the further from baseline,
    /// the less effect additional same-direction deltas have. Corrective deltas
    /// (toward baseline) apply at full strength.
    /// </summary>
    public void ApplyShift(float warmthDelta, float energyDelta, float concernDelta, float playfulnessDelta)
    {
        Warmth      = Math.Clamp(Warmth + AttenuateDelta(Warmth, WarmthBaseline, warmthDelta), 0f, 1f);
        Energy      = Math.Clamp(Energy + AttenuateDelta(Energy, EnergyBaseline, energyDelta), 0f, 1f);
        Concern     = Math.Clamp(Concern + AttenuateDelta(Concern, ConcernBaseline, concernDelta), 0f, 1f);
        Playfulness = Math.Clamp(Playfulness + AttenuateDelta(Playfulness, PlayfulnessBaseline, playfulnessDelta), 0f, 1f);
        LastUpdated = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Attenuate deltas that push away from baseline using diminishing returns.
    /// Far from baseline: near-zero delta. At baseline: resting pull (0.5x).
    /// Corrective deltas (toward baseline) are unaffected.
    ///
    /// The resting pull ensures that even the first push away from baseline is
    /// dampened — preventing the oscillation pattern where max LLM deltas crater
    /// emotions every cycle before drift can recover them.
    /// </summary>
    private static float AttenuateDelta(float current, float baseline, float delta)
    {
        if (delta == 0f) return 0f;

        var distanceFromBaseline = current - baseline;

        // Corrective deltas (moving toward baseline) always apply at full strength
        var corrective = (distanceFromBaseline > 0 && delta < 0) ||
                         (distanceFromBaseline < 0 && delta > 0);
        if (corrective) return delta;

        // Total range from baseline to the limit in this direction
        float range = delta > 0 ? (1f - baseline) : baseline;
        float used = Math.Abs(distanceFromBaseline);
        float scale = range > 0 ? Math.Max(0f, 1f - (used / range)) : 0f;

        // Resting pull: even at baseline (scale=1.0), cap at 0.5x to dampen
        // the first push. This prevents max-delta LLM outputs from immediately
        // cratering emotions when starting near baseline.
        const float restingPull = 0.5f;
        scale = Math.Min(scale, restingPull + (1f - restingPull) * (1f - scale));

        return delta * scale;
    }
}
