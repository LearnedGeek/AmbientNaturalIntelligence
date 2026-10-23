namespace AniRuntime.Core.Utilities;

/// <summary>
/// F-4 Phase 4a.2c (2026-10-22) — pure-static anchor-promotion scorer.
/// Counts how often each associative anchor appears in a recent-anchor
/// window; anchors meeting the minimum-occurrence threshold become
/// candidate TopicValence keys with a configured salience, matching
/// case-insensitively against existing keys.
///
/// <para>
/// Issue #174 preferred shape: skip the LLM for high-recurrence anchors
/// (counting is cheap, anchors are already condensed 2-6 word phrases).
/// Keeps <see cref="AniRuntime.Core.Interfaces.IGrowthEdgeMutator"/>'s
/// bounded-delta discipline intact — anchors here produce the same
/// shape of extracted topics the LLM path produces, so downstream
/// <c>ApplyUpdates</c> treats them identically.
/// </para>
///
/// <para>
/// Normalization: anchors are trimmed and lower-cased before counting,
/// so "Lila" / "lila" / " Lila " merge into one promotion candidate.
/// Existing-key match is case-insensitive; the matched key's original
/// casing is preserved (consumers reading <c>TopicValence</c> expect
/// stable keys).
/// </para>
/// </summary>
public static class AnchorPromotionScoring
{
    /// <summary>One anchor that has met the promotion threshold.</summary>
    public sealed record AnchorCandidate(
        string Topic,
        int    Occurrences,
        string? ExistingKeyMatch);

    /// <summary>
    /// Counts occurrences of each anchor in <paramref name="recentAnchors"/>
    /// (case-insensitive, trimmed, empty/null skipped). Anchors appearing
    /// at least <paramref name="minOccurrences"/> times become candidates.
    /// For each candidate, if a <paramref name="existingKeys"/> entry
    /// matches case-insensitively, <see cref="AnchorCandidate.ExistingKeyMatch"/>
    /// is set to the existing key's canonical casing; otherwise null.
    /// </summary>
    public static IReadOnlyList<AnchorCandidate> Score(
        IReadOnlyList<string>? recentAnchors,
        IReadOnlyList<string>? existingKeys,
        int                     minOccurrences)
    {
        if (recentAnchors is null || recentAnchors.Count == 0 || minOccurrences <= 0)
            return Array.Empty<AnchorCandidate>();

        var threshold = Math.Max(1, minOccurrences);

        // Count under lower-cased key, preserve the first-seen original casing
        // so we emit a stable topic name (not everything lower-cased).
        var counts   = new Dictionary<string, int>(StringComparer.Ordinal);
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmedAnchors = recentAnchors
            .Where(raw => !string.IsNullOrWhiteSpace(raw))
            .Select(raw => raw.Trim());
        foreach (var trimmed in trimmedAnchors)
        {
            var lower = trimmed.ToLowerInvariant();
            counts[lower] = counts.TryGetValue(lower, out var n) ? n + 1 : 1;
            if (!originals.ContainsKey(lower)) originals[lower] = trimmed;
        }

        // Build case-insensitive lookup of existing keys so match preserves
        // their canonical casing.
        var existingByLower = new Dictionary<string, string>(StringComparer.Ordinal);
        if (existingKeys is not null)
        {
            foreach (var k in existingKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
            {
                var lower = k.Trim().ToLowerInvariant();
                if (!existingByLower.ContainsKey(lower))
                    existingByLower[lower] = k;
            }
        }

        var result = new List<AnchorCandidate>();
        foreach (var (lower, count) in counts)
        {
            if (count < threshold) continue;
            var topic = originals[lower];
            var match = existingByLower.TryGetValue(lower, out var canonical)
                ? canonical
                : null;
            result.Add(new AnchorCandidate(topic, count, match));
        }

        return result;
    }
}
