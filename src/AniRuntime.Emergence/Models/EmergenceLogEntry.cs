namespace AniRuntime.Emergence.Models;

/// <summary>
/// A single entry in the emergence log — the research instrument.
/// Every scored cycle, resonance bump, and decay event is recorded here.
/// The raw CycleObservation JSON is preserved for replay and retroactive analysis.
/// </summary>
public class EmergenceLogEntry
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>CycleScored, ResonanceBump, ResonanceDecay.</summary>
    public string EntryType { get; set; } = "CycleScored";

    /// <summary>The ResonanceScore computed for this cycle (if applicable).</summary>
    public float? ResonanceScore { get; set; }

    /// <summary>Structured details (JSON) — what was observed and why.</summary>
    public string DetailsJson { get; set; } = "{}";

    /// <summary>Full CycleObservation serialized as JSON — ground truth for research.</summary>
    public string? CycleObservationJson { get; set; }

    /// <summary>JSON array of matched emergence type codes, e.g. ["EM2-symbolic-processing"]. Null if none detected.</summary>
    public string? EmergenceTypesJson { get; set; }
}
