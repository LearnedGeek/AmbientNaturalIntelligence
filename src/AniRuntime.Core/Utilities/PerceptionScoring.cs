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
    /// <summary>
    /// F-4 Phase 4a.1b (2026-10-23) — Perception retrieval composite:
    /// <c>significance × importance × topic_valence_lookup × fatigue_decay</c>.
    /// Pure function so the formula is pinnable by unit tests; EF service
    /// calls this instead of inlining the LINQ projection.
    ///
    /// <para>
    /// Each factor is independently clamped at the field level and the
    /// product is in [0, 1] by construction when inputs are well-behaved.
    /// Phase 4a.1c added fatigue as the fourth multiplicative factor.
    /// </para>
    /// </summary>
    public static float PerceptionEffectiveWeight(
        float significance,
        float importance,
        string? content,
        IReadOnlyDictionary<string, float> topicValence,
        int exposureCount = 0,
        DateTimeOffset? lastExposedAt = null,
        DateTimeOffset? now = null)
        => significance
         * importance
         * FuzzyTopicValenceLookup(content, topicValence)
         * FatigueDecay(exposureCount, lastExposedAt, now ?? DateTimeOffset.UtcNow);

    /// <summary>Default exposure-count half-life — ~0.37× weight at this many surfaces.</summary>
    public const float DefaultFatigueHalfLife = 3f;

    /// <summary>Default time-scale over which exposure "wears off."</summary>
    public const float DefaultFatigueRecoveryHours = 48f;

    /// <summary>
    /// F-4 Phase 4a.1c (2026-10-23) — fatigue axis of the Perception
    /// composite. Returns a weight in (0, 1] that decays with exposure
    /// count and recovers with time since last exposure.
    ///
    /// <para>
    /// Formula: let <c>effective_exposure = exposureCount × exp(-hoursSinceLast / recoveryHours)</c>,
    /// then <c>fatigue = exp(-effective_exposure / halfLife)</c>. At
    /// zero exposure → 1.0 (no fatigue). After N exposures right away →
    /// <c>exp(-N / halfLife)</c>. After N exposures with time passed →
    /// partial recovery (effective_exposure shrinks exponentially with time).
    /// </para>
    ///
    /// <para>
    /// Null <paramref name="lastExposedAt"/> or zero
    /// <paramref name="exposureCount"/> → returns 1.0 (fresh record).
    /// Negative values on either input clamp to zero.
    /// </para>
    /// </summary>
    public static float FatigueDecay(
        int exposureCount,
        DateTimeOffset? lastExposedAt,
        DateTimeOffset now,
        float halfLife = DefaultFatigueHalfLife,
        float recoveryHours = DefaultFatigueRecoveryHours)
    {
        if (exposureCount <= 0 || lastExposedAt is null) return 1f;
        if (halfLife <= 0f) return 1f;

        var recoveryScale = recoveryHours > 0f ? recoveryHours : DefaultFatigueRecoveryHours;
        var hoursSince = (float)Math.Max(0, (now - lastExposedAt.Value).TotalHours);
        var timeDecay  = (float)Math.Exp(-hoursSince / recoveryScale);
        var effective  = exposureCount * timeDecay;
        return (float)Math.Exp(-effective / halfLife);
    }

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
