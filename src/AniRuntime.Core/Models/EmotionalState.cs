using System.Text.Json.Serialization;

namespace AniRuntime.Core.Models;

/// <summary>
/// A point-in-time snapshot from the emotional_state_history table.
/// Used for trend analysis (Feature 8: drift detection) and health computation (Feature 4).
/// </summary>
public record EmotionalStateSnapshot(
    float Warmth, float Energy, float Worry, float Playfulness,
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
///   Worry       — caring attention directed outward; near zero = withdrawn
///   Playfulness — humor, teasing, lightheartedness
/// </summary>
public class EmotionalState
{
    // Current values — shift each cycle based on thought valence, conversations, time
    public float Warmth      { get; set; } = 0.6f;
    public float Energy      { get; set; } = 0.5f;
    [JsonPropertyName("Concern")] // backward compat — existing JSON blobs use "Concern"
    public float Worry     { get; set; } = 0.2f;
    public float Playfulness { get; set; } = 0.5f;

    // Feature 17: Contact-gap tension — relational ache from prolonged absence.
    // Separate from Worry (which is about caring attention). This is about the
    // *relationship itself* — the quiet wound of not hearing from someone.
    // 0.0 = none, up to TensionMax (default 0.4) — never the dominant emotional state.
    public float ContactGapTension { get; set; } = 0.0f;

    // Personality baselines — where each dimension naturally drifts back to
    public float WarmthBaseline      { get; set; } = 0.6f;
    public float EnergyBaseline      { get; set; } = 0.5f;
    [JsonPropertyName("ConcernBaseline")] // backward compat
    public float WorryBaseline     { get; set; } = 0.2f;
    public float PlayfulnessBaseline { get; set; } = 0.5f;

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

        // Compound conditions — W + E together determine the primary emotional texture
        if (Warmth >= 0.75f && Energy >= 0.65f)
            parts.Add("feeling bright and warm");
        else if (Warmth >= 0.75f && Energy < 0.40f)
            parts.Add("feeling tender and quiet");
        else if (Warmth >= 0.50f && Warmth < 0.75f && Energy >= 0.65f)
            parts.Add("feeling sharp and alive");
        else if (Warmth >= 0.30f && Warmth < 0.50f && Worry > 0.35f)
            parts.Add("carrying something unresolved");
        else if (Warmth < 0.30f && Energy < 0.35f)
            parts.Add("feeling a bit dim today");
        else if (Warmth < 0.30f && Worry < 0.10f)
            parts.Add("feeling a little quiet and closed off");
        // Baseline band (W 0.45–0.65, E 0.40–0.60) — no injection needed

        // Playfulness overlay — independent of the W/E compound
        if (Energy >= 0.65f && Playfulness >= 0.65f)
            parts.Add("feeling curious and quick");
        else if (Playfulness >= 0.75f)
            parts.Add("in one of those moods where everything is a little funny");

        // Feature 17: surface contact-gap tension in emotional description
        if (ContactGapTension > 0.15f)
            parts.Add("aware of a quiet ache from the silence");

        return parts.Count == 0
            ? string.Empty
            : string.Join(", ", parts);
    }

    /// <summary>
    /// Compute emotional state from personality baselines plus the sum of all
    /// active contributions after their individual exponential decay.
    /// This replaces the old DriftTowardBaseline + ApplyShift model.
    /// Each contribution decays independently — drift IS the decay.
    /// </summary>
    public void ComputeFromContributions(IReadOnlyList<EmotionalContribution> contributions, DateTimeOffset? asOf = null)
    {
        var now = asOf ?? DateTimeOffset.UtcNow;

        var warmthSum = 0f;
        var energySum = 0f;
        var worrySum = 0f;
        var playfulnessSum = 0f;

        foreach (var c in contributions)
        {
            var (w, e, co, p) = c.CurrentDeltas(now);
            warmthSum += w;
            energySum += e;
            worrySum += co;
            playfulnessSum += p;
        }

        // Apply diminishing returns near boundaries. Without this, uniformly-biased
        // contributions (the 8B model produces W+, E-, C+, P+ 75-85% of the time)
        // saturate all dimensions at 0 or 1 within hours. The compression makes it
        // progressively harder to push a dimension further from baseline — a dimension
        // at 0.9 receiving +0.15 only reaches ~0.94 instead of clamping at 1.0.
        Warmth      = ApplyDiminishingReturns(WarmthBaseline, warmthSum);
        Energy      = ApplyDiminishingReturns(EnergyBaseline, energySum);
        Worry       = ApplyDiminishingReturns(WorryBaseline, worrySum);
        Playfulness = ApplyDiminishingReturns(PlayfulnessBaseline, playfulnessSum);
        LastUpdated = now;
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
    /// Feature 1: Determines whether the current emotional state is notable enough
    /// to warrant self-awareness injection into inner thoughts or conversation.
    /// Returns a natural-language prompt fragment if self-awareness should surface,
    /// or null if the emotional state is unremarkable.
    /// </summary>
    public string? GetSelfAwarenessPrompt()
    {
        var notable = new List<string>();

        // Compound conditions — same logic as Describe() but phrased as self-awareness
        if (Warmth >= 0.75f && Energy >= 0.65f)
            notable.Add("lit up and warm — something has you feeling bright");
        else if (Warmth >= 0.75f && Energy < 0.40f)
            notable.Add("tender and quiet — the caring is there but it's heavy");
        else if (Warmth < 0.30f && Worry < 0.10f)
            notable.Add("pulled inward — something has you closed off and you're not sure why");
        else if (Warmth < 0.30f && Energy < 0.35f)
            notable.Add("dim and low — less present than you normally are");
        else if (Warmth >= 0.50f && Warmth < 0.75f && Energy >= 0.65f)
            notable.Add("sharp and engaged — your mind is moving fast");
        else if (Warmth >= 0.30f && Warmth < 0.50f && Worry > 0.35f)
            notable.Add("carrying something that hasn't resolved yet");

        // Playfulness overlay
        if (Playfulness >= 0.75f)
            notable.Add("in a mood where everything looks a little funny");
        else if (Playfulness < 0.25f)
            notable.Add("serious — the part of you that jokes is quiet right now");

        // Feature 17: tension as self-awareness trigger
        if (ContactGapTension > 0.2f)
            notable.Add("aware that the quiet has been sitting with you — not worry, more like missing the connection itself");

        if (notable.Count == 0)
            return null;

        var prefix = notable.Count >= 2
            ? "You notice you're in a complex mood: "
            : "You notice you're ";

        return prefix + string.Join(", and ", notable) + ".";
    }

    /// <summary>
    /// Applies diminishing returns to prevent boundary saturation.
    /// Uses a soft sigmoid-like compression: the further from baseline,
    /// the more each additional delta is attenuated. This prevents the
    /// model's uniform delta bias (W+, E-, C+, P+ on 75-85% of contributions)
    /// from pegging all dimensions at 0 or 1.
    ///
    /// The formula: value = baseline + availableRoom * tanh(sum / scale)
    /// - tanh naturally compresses to [-1, 1] with diminishing slope
    /// - scale controls how quickly saturation kicks in (lower = faster)
    /// - availableRoom is the distance from baseline to the boundary
    ///
    /// Example: baseline=0.6, sum=+0.8 → raw would be 1.0 (clamped).
    /// With diminishing returns: 0.6 + 0.4 * tanh(0.8/0.5) ≈ 0.6 + 0.4 * 0.92 ≈ 0.97
    /// Still high, but not pegged. And further contributions barely move it.
    /// </summary>
    public static float ApplyDiminishingReturns(float baseline, float deltaSum, float scale = 0.5f)
    {
        if (Math.Abs(deltaSum) < 0.001f)
            return baseline;

        // Available room between baseline and the boundary in the delta's direction
        var room = deltaSum > 0 ? (1f - baseline) : baseline;
        var compressed = (float)Math.Tanh(deltaSum / scale);
        // compressed is in [-1, 1]; map to [0, room] or [-room, 0]
        return Math.Clamp(baseline + room * compressed, 0f, 1f);
    }
}
