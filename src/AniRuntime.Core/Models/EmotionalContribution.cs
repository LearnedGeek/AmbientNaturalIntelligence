namespace AniRuntime.Core.Models;

/// <summary>
/// A single emotional impact from a thought, conversation, or event.
/// Each contribution starts at its scored magnitude and decays exponentially
/// via a half-life. The emotional state at any moment is the sum of all
/// active contributions on top of personality baselines.
///
/// This replaces the old model where deltas were applied permanently and
/// a global drift pulled everything back. Now each thought has its own
/// lifecycle — strong initial impact that fades naturally.
/// </summary>
public class EmotionalContribution
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Summary of the source content (for semantic dedup and theme tracking).</summary>
    public string SourceContent { get; set; } = string.Empty;

    /// <summary>Initial scored deltas — the contribution at full strength.</summary>
    public float WarmthDelta { get; set; }
    public float EnergyDelta { get; set; }
    public float WorryDelta { get; set; }
    public float PlayfulnessDelta { get; set; }

    /// <summary>When this contribution was created (or last refreshed).</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Exponential decay half-life in hours. After one half-life, the contribution
    /// is at 50% strength. After two, 25%. After ~7 half-lives it's effectively zero.
    /// </summary>
    public float HalfLifeHours { get; set; } = 1.0f;

    /// <summary>
    /// Impact category determines max delta and half-life.
    /// Ambient = inner thoughts (low impact, fast decay).
    /// Conversation = direct interaction (higher impact, slower decay).
    /// Global = major events (moderate impact, slowest decay).
    /// </summary>
    public ImpactCategory Category { get; set; } = ImpactCategory.Ambient;

    /// <summary>
    /// Intensity within the emotional register (0.0–1.0). Applied as a multiplier
    /// to the raw deltas before tier ceiling clamping. Default 1.0 for backward compat.
    /// Phase 2 uses this for tier promotion: severity ≥ 0.85 → Global tier.
    /// </summary>
    public float Severity { get; set; } = 1.0f;

    /// <summary>
    /// C3 Associative Spark flag — signals that this thought has natural outreach
    /// potential ("something made me think of you") independent of the desire threshold.
    /// </summary>
    public bool IsOutreachReady { get; set; }

    /// <summary>Optional embedding for semantic similarity checks.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// Compute the decay factor at a given point in time.
    /// Returns a multiplier 0.0–1.0 to apply to the initial deltas.
    /// </summary>
    public float DecayFactor(DateTimeOffset asOf)
    {
        var elapsed = (float)(asOf - CreatedAt).TotalHours;
        if (elapsed <= 0f) return 1.0f;
        if (HalfLifeHours <= 0f) return 0f;

        // 2^(-t/halfLife) — standard exponential decay
        return (float)Math.Pow(2.0, -elapsed / HalfLifeHours);
    }

    /// <summary>Current effective deltas after severity scaling and decay.</summary>
    public (float Warmth, float Energy, float Worry, float Playfulness) CurrentDeltas(DateTimeOffset asOf)
    {
        var factor = DecayFactor(asOf) * Severity;
        return (
            WarmthDelta * factor,
            EnergyDelta * factor,
            WorryDelta * factor,
            PlayfulnessDelta * factor
        );
    }

    /// <summary>
    /// Whether this contribution has decayed below a meaningful threshold.
    /// Used to determine when to move it to "processed" status.
    /// </summary>
    public bool IsEffectivelyZero(DateTimeOffset asOf, float epsilon = 0.005f)
    {
        var (w, e, c, p) = CurrentDeltas(asOf);
        return Math.Abs(w) < epsilon
            && Math.Abs(e) < epsilon
            && Math.Abs(c) < epsilon
            && Math.Abs(p) < epsilon;
    }
}

public enum ImpactCategory
{
    /// <summary>Inner thoughts, routine observations. Max delta 0.15, half-life 1 hour.</summary>
    Ambient,

    /// <summary>Direct conversation with contact. Max delta 0.25, half-life 3 hours.</summary>
    Conversation,

    /// <summary>Major news, life events. Max delta 0.35, half-life 12 hours.</summary>
    Global
}

/// <summary>
/// Constants for impact category parameters, kept centralized so cognitive cycle
/// and prompt builder use the same values.
/// </summary>
public static class ImpactCategoryDefaults
{
    public static (float MaxDelta, float HalfLifeHours) GetDefaults(ImpactCategory category) => category switch
    {
        ImpactCategory.Ambient      => (0.15f, 1.0f),
        ImpactCategory.Conversation => (0.25f, 3.0f),
        ImpactCategory.Global       => (0.35f, 12.0f),
        _                           => (0.15f, 1.0f),
    };

    /// <summary>
    /// Severity-driven tier promotion. High-severity ambient thoughts promote to
    /// Conversation or Global tier for longer-lasting emotional impact.
    /// Kept here (not in CognitiveCycleProcessor) for discoverability and testability.
    /// </summary>
    public static ImpactCategory DetermineEffectiveTier(
        ImpactCategory baseTier, float severity, AniOptions options)
    {
        if (severity >= options.GlobalPromotionThreshold)
            return ImpactCategory.Global;
        if (severity >= options.ConversationPromotionThreshold
            && baseTier == ImpactCategory.Ambient)
            return ImpactCategory.Conversation;
        return baseTier;
    }
}
