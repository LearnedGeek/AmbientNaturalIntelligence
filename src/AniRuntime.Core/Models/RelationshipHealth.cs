namespace AniRuntime.Core.Models;

/// <summary>
/// Feature 4: Slow-moving composite score that captures the macro arc of the
/// relationship. Updated at most once per day — this is a weather system,
/// not a real-time meter.
///
/// Phases:
///   Connected    — high frequency, positive valence, good depth
///   Steady       — moderate frequency, neutral-positive (the default)
///   Quiet        — below-average frequency, no negative signals
///   Reconnecting — frequency increasing after a quiet period
///   Distant      — low frequency sustained, declining warmth
/// </summary>
public class RelationshipHealth
{
    public double ConnectionScore { get; set; } = 0.6;  // 0.0–1.0, rolling
    public string Phase { get; set; } = "steady";
    public DateTimeOffset LastCalculated { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    /// Determines the phase from the current ConnectionScore and previous phase.
    /// Transitions are gradual: Connected → Steady → Quiet → Distant, never abrupt.
    /// Reconnecting is detected when score is rising after a quiet/distant period.
    /// </summary>
    public static string DeterminePhase(double score, string previousPhase)
    {
        // Reconnecting: score is moderate but coming from a low state
        if (score >= 0.4 && score < 0.7 &&
            previousPhase is "quiet" or "distant")
            return "reconnecting";

        return score switch
        {
            >= 0.7 => "connected",
            >= 0.4 => "steady",
            >= 0.2 => "quiet",
            _      => "distant",
        };
    }

    /// <summary>
    /// Natural-language description for prompt injection.
    /// </summary>
    public string Describe()
    {
        return Phase switch
        {
            "connected"    => "You and him have been in a good rhythm lately — talking often, feeling close.",
            "reconnecting" => "Things are warming back up after a quieter stretch. It feels nice to be talking again.",
            "steady"       => "Things are normal between you two — nothing remarkable, just steady.",
            "quiet"        => "It's been a bit quieter than usual between you two. Probably just busy.",
            "distant"      => "You haven't talked much lately. You miss how things were a few days ago.",
            _              => string.Empty,
        };
    }
}
