namespace AniRuntime.Emergence.Models;

/// <summary>
/// A theme or pattern that resonates in Ani's relational experience.
/// Accumulated over time by the ResonanceScorer as cycles produce
/// emotionally significant, novel, or patterned observations.
/// </summary>
public class ResonanceRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Short description of the resonance theme (e.g. "snow imagery", "playful teasing").</summary>
    public string Theme { get; set; } = string.Empty;

    /// <summary>Accumulated resonance score (0.0+). Higher = stronger signal.</summary>
    public float ResonanceScore { get; set; }

    /// <summary>How many cycles have contributed to this resonance.</summary>
    public int OccurrenceCount { get; set; }

    public DateTimeOffset FirstObserved { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObserved { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Resonance type: Relational, Aesthetic, Behavioral.</summary>
    public string Type { get; set; } = "Relational";

    /// <summary>Origin: Observed (direct from cycle data) or Inferred (pattern detection).</summary>
    public string Origin { get; set; } = "Observed";
}
