namespace AniRuntime.Core.Utilities;

/// <summary>
/// F-4 Phase 4a.1a (2026-10-22) — pure static helpers for the three-dimension
/// Perception effective-weight model named in the Content-Coverage Audit.
///
/// <para>
/// Phase 4a.1a ships axis 2 (preference, via TopicValence lookup) only.
/// Axis 1 (significance) and axis 3 (fatigue) land in Phase 4a.1b and
/// Phase 4a.1c respectively. See the audit doc §Importance model
/// clarification for the three-dimension framing.
/// </para>
/// </summary>
public static class PerceptionScoring
{
    /// <summary>
    /// Default lookup value when no TopicValence key matches the content.
    /// Neutral (0.5) so unmatched items are neither amplified nor zeroed-out.
    /// A zero default would silence any Perception event whose topic hasn't
    /// yet been named in TopicValence — including fresh world-events the
    /// system has never thought about before, which defeats Phase 4a.1a's
    /// purpose.
    /// </summary>
    public const float NoMatchDefaultWeight = 0.5f;

    /// <summary>
    /// For each TopicValence key, check whether the key text appears as a
    /// case-insensitive substring inside <paramref name="content"/>.
    /// Returns the weight of the highest-weighted matching key.
    /// Returns <see cref="NoMatchDefaultWeight"/> when no key matches or
    /// when the dictionary is empty.
    ///
    /// <para>
    /// <b>Why substring:</b> TopicValence keys are curated prose descriptions
    /// (e.g. "Mia", "Spanish lessons", "Okauchee Lake — our future spot").
    /// Substring match catches "Mia" inside "mia is at starbucks" but also
    /// catches partial collisions ("mia" inside "miami"). The audit flagged
    /// this as a known heuristic limitation; a Phase 4a.1 follow-up could
    /// move to word-boundary matching or embedding-cosine. For the first
    /// slice the substring is enough — the alternative (zero coverage for
    /// Perception) is strictly worse.
    /// </para>
    ///
    /// <para>
    /// <b>Short-key guard:</b> TopicValence keys of length &lt; 3 are
    /// skipped during matching because substring matching on 1-2 character
    /// keys produces false positives on nearly any content. The curated
    /// seed TopicValence has no such keys as of 2026-10-22.
    /// </para>
    /// </summary>
    public static float FuzzyTopicValenceLookup(
        string? content,
        IReadOnlyDictionary<string, float> topicValence)
    {
        if (string.IsNullOrWhiteSpace(content)) return NoMatchDefaultWeight;
        if (topicValence is null || topicValence.Count == 0) return NoMatchDefaultWeight;

        var contentLower = content.ToLowerInvariant();
        var best = -1f;

        foreach (var kv in topicValence)
        {
            var key = kv.Key;
            if (string.IsNullOrWhiteSpace(key) || key.Length < 3) continue;
            if (contentLower.Contains(key.ToLowerInvariant(), StringComparison.Ordinal)
                && kv.Value > best)
            {
                best = kv.Value;
            }
        }

        return best < 0f ? NoMatchDefaultWeight : best;
    }
}
