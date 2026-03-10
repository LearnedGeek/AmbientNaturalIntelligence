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
///   Concern     — worry, protectiveness, unease about Mark
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
    public float DriftRate { get; set; } = 0.15f;

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
            parts.Add("a little worried about Mark");
        else if (ConcernBaseline - Concern > threshold)
            parts.Add("feeling at ease about Mark");

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
    /// Values are clamped to 0.0–1.0.
    /// </summary>
    public void ApplyShift(float warmthDelta, float energyDelta, float concernDelta, float playfulnessDelta)
    {
        Warmth      = Math.Clamp(Warmth + warmthDelta, 0f, 1f);
        Energy      = Math.Clamp(Energy + energyDelta, 0f, 1f);
        Concern     = Math.Clamp(Concern + concernDelta, 0f, 1f);
        Playfulness = Math.Clamp(Playfulness + playfulnessDelta, 0f, 1f);
        LastUpdated = DateTimeOffset.UtcNow;
    }
}
