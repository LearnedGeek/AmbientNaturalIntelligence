namespace AniRuntime.Core.Models;

/// <summary>
/// Feature 8: Result of emotional drift detection.
/// Compares the emotional "center of gravity" between two time windows
/// to detect slow-moving trends invisible cycle-to-cycle.
///
/// High drift (low similarity) indicates a significant emotional shift
/// over the comparison period — e.g., warmth declining over 3 days.
/// </summary>
public class EmotionalDrift
{
    /// <summary>
    /// Cosine similarity between the recent and older emotional vectors.
    /// 1.0 = identical emotional profile, 0.0 = orthogonal, &lt;0 = inverted.
    /// </summary>
    public float Similarity { get; set; } = 1.0f;

    /// <summary>
    /// True when drift is significant enough to warrant attention.
    /// Threshold: similarity &lt; 0.90 over 24-48 hours.
    /// </summary>
    public bool IsSignificant => Similarity < 0.90f;

    /// <summary>
    /// Per-dimension drift (recent average minus older average).
    /// Positive = dimension is rising, negative = falling.
    /// </summary>
    public float WarmthDrift      { get; set; }
    public float EnergyDrift      { get; set; }
    public float ConcernDrift     { get; set; }
    public float PlayfulnessDrift { get; set; }

    /// <summary>
    /// Natural-language description of the dominant drift direction.
    /// Returns null if drift is insignificant.
    /// </summary>
    public string? Describe()
    {
        if (!IsSignificant) return null;

        var parts = new List<string>();
        const float threshold = 0.05f;

        if (WarmthDrift > threshold) parts.Add("warmth has been rising");
        else if (WarmthDrift < -threshold) parts.Add("warmth has been fading");

        if (EnergyDrift > threshold) parts.Add("energy has been climbing");
        else if (EnergyDrift < -threshold) parts.Add("energy has been dropping");

        if (ConcernDrift > threshold) parts.Add("worry has been creeping in");
        else if (ConcernDrift < -threshold) parts.Add("worry has been easing");

        if (PlayfulnessDrift > threshold) parts.Add("playfulness has been growing");
        else if (PlayfulnessDrift < -threshold) parts.Add("playfulness has been fading");

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// Computes emotional drift between two sets of snapshots.
    /// Each set is averaged to produce a centroid vector, then cosine similarity
    /// is computed between the two centroids.
    /// </summary>
    public static EmotionalDrift Compute(
        IReadOnlyList<EmotionalStateSnapshot> recent,
        IReadOnlyList<EmotionalStateSnapshot> older)
    {
        if (recent.Count == 0 || older.Count == 0)
            return new EmotionalDrift();

        var recentAvg = Average(recent);
        var olderAvg = Average(older);

        var similarity = CosineSimilarity(recentAvg, olderAvg);

        return new EmotionalDrift
        {
            Similarity      = similarity,
            WarmthDrift      = recentAvg[0] - olderAvg[0],
            EnergyDrift      = recentAvg[1] - olderAvg[1],
            ConcernDrift     = recentAvg[2] - olderAvg[2],
            PlayfulnessDrift = recentAvg[3] - olderAvg[3],
        };
    }

    private static float[] Average(IReadOnlyList<EmotionalStateSnapshot> snapshots)
    {
        float w = 0, e = 0, c = 0, p = 0;
        foreach (var s in snapshots)
        {
            w += s.Warmth;
            e += s.Energy;
            c += s.Concern;
            p += s.Playfulness;
        }
        var n = (float)snapshots.Count;
        return [w / n, e / n, c / n, p / n];
    }

    // Feature 9: Delegate to shared SIMD-accelerated implementation.
    // zeroDenomValue=1.0f because zero emotional vectors = "no drift" (identical).
    private static float CosineSimilarity(float[] a, float[] b)
        => VectorMath.CosineSimilarity(a, b, zeroDenomValue: 1.0f);
}
