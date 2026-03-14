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
    public float ConcernDelta { get; set; }
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

    /// <summary>Current effective deltas after decay.</summary>
    public (float Warmth, float Energy, float Concern, float Playfulness) CurrentDeltas(DateTimeOffset asOf)
    {
        var factor = DecayFactor(asOf);
        return (
            WarmthDelta * factor,
            EnergyDelta * factor,
            ConcernDelta * factor,
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

    /// <summary>Major news, life events. Max delta 0.20, half-life 6 hours.</summary>
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
        ImpactCategory.Global       => (0.20f, 6.0f),
        _                           => (0.15f, 1.0f),
    };
}
