namespace AniRuntime.Core.Models;

/// <summary>
/// A point-in-time snapshot from the emotional_state_history table.
/// Used for trend analysis (Feature 8: drift detection) and health computation (Feature 4).
/// </summary>
public record EmotionalStateSnapshot(
    float Warmth, float Energy, float Concern, float Playfulness,
    float ContactGapTension, DateTimeOffset RecordedAt);

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

    // Feature 17: Contact-gap tension — relational ache from prolonged absence.
    // Separate from Concern (which is about his welfare). This is about the
    // *relationship itself* — the quiet wound of not hearing from someone.
    // 0.0 = none, up to TensionMax (default 0.4) — never the dominant emotional state.
    public float ContactGapTension { get; set; } = 0.0f;

    // Personality baselines — where each dimension naturally drifts back to
    public float WarmthBaseline      { get; set; } = 0.6f;
    public float EnergyBaseline      { get; set; } = 0.5f;
    public float ConcernBaseline     { get; set; } = 0.2f;
    public float PlayfulnessBaseline { get; set; } = 0.5f;

    // How fast each dimension drifts toward baseline per hour (0.0–1.0 of the gap)
    public float DriftRate { get; set; } = 0.25f;

    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Feature 17: Effective warmth — the warmth the outside world sees.
    /// ContactGapTension suppresses expressed warmth by up to 30% of tension value.
    /// Internal Warmth is unchanged; this is about what surfaces in conversation.
    /// </summary>
    public float EffectiveWarmth => Math.Max(0f, Warmth - ContactGapTension * 0.3f);

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

        // Feature 17: surface contact-gap tension in emotional description
        if (ContactGapTension > 0.15f)
            parts.Add("aware of a quiet ache from the silence");

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
    /// Feature 17: Accumulate contact-gap tension based on hours since last contact.
    /// Tension begins after onsetHours (default 18h) and accumulates at rate per hour
    /// up to a hard cap. This is called once per cognitive cycle in Phase 0.
    /// </summary>
    public void AccumulateContactGapTension(double hoursSinceContact, double onsetHours, double rate, double max)
    {
        if (hoursSinceContact <= onsetHours)
            return;

        var excessHours = hoursSinceContact - onsetHours;
        var newTension = (float)Math.Min(excessHours * rate, max);
        ContactGapTension = Math.Min(newTension, (float)max);
    }

    /// <summary>
    /// Feature 17: Dissipate contact-gap tension on reconnection.
    /// Tension fades at dissipationMultiplier × accumulation rate per hour of contact.
    /// Called when the contact sends a message, before reply generation.
    /// </summary>
    public void DissipateContactGapTension(double elapsedMinutes, double rate, double dissipationMultiplier)
    {
        if (ContactGapTension <= 0f) return;
        var decay = (float)(rate * dissipationMultiplier * elapsedMinutes / 60.0);
        ContactGapTension = Math.Max(0f, ContactGapTension - decay);
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
    /// Feature 1: Determines whether the current emotional state is notable enough
    /// to warrant self-awareness injection into inner thoughts or conversation.
    /// Returns a natural-language prompt fragment if self-awareness should surface,
    /// or null if the emotional state is unremarkable.
    /// </summary>
    public string? GetSelfAwarenessPrompt()
    {
        const float extremeThreshold = 0.25f; // further from baseline = notable
        var notable = new List<string>();

        if (Math.Abs(Warmth - WarmthBaseline) > extremeThreshold)
            notable.Add(Warmth > WarmthBaseline
                ? "warmer than usual — something tender is sitting with you"
                : "a bit closed off — less warm than you normally are");

        if (Math.Abs(Energy - EnergyBaseline) > extremeThreshold)
            notable.Add(Energy > EnergyBaseline
                ? "more energized than usual — something has you buzzing"
                : "quieter and more low-key than normal");

        if (Math.Abs(Concern - ConcernBaseline) > extremeThreshold)
            notable.Add(Concern > ConcernBaseline
                ? "carrying a low hum of worry you can't quite place"
                : "unusually at ease — the worry that sometimes sits with you is gone");

        if (Math.Abs(Playfulness - PlayfulnessBaseline) > extremeThreshold)
            notable.Add(Playfulness > PlayfulnessBaseline
                ? "in a light, playful mood — everything feels a little funny"
                : "feeling serious — the part of you that jokes is quiet right now");

        // Feature 17: tension as self-awareness trigger
        if (ContactGapTension > 0.2f)
            notable.Add("aware that the quiet has been sitting with you — not worry, more like... missing the connection itself");

        if (notable.Count == 0)
            return null;

        // Multiple notable dimensions = complex emotional state
        var prefix = notable.Count >= 2
            ? "You notice you're in a complex mood: "
            : "You notice you're ";

        return prefix + string.Join(", and ", notable) + ".";
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
