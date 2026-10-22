using AniRuntime.Core.Models;

namespace AniRuntime.Core.Utilities;

/// <summary>
/// F-4 Phase 2.1 (2026-10-22) — pure static helpers for outreach-seed
/// selection scoring. The lean outreach composer (K.2 2026-07-02,
/// K.3 2026-07-06 — see <c>OutreachPipeline.cs:217-241</c>) sends
/// <c>userMessage: recentThought</c> as the fine-tune's input, so the
/// specific thought chosen as the seed is what shapes Mark's received
/// outreach text. Pre-Phase-2.1 the seed was always the just-generated
/// thought this cycle, regardless of its anchor-presence or specificity;
/// a cycle landing on a register-locked default thought would send a
/// register-locked default outreach even if the past hour's inner
/// thoughts included anchor-bearing Lila/Fleetwood/Sarah references.
///
/// <para>
/// <b>Scoring formula:</b> <c>Importance × (1 + AnchorBonus)</c> where
/// <c>AnchorBonus = GrowthEdgeAnchorBonus</c> (default 0.3) when the
/// thought content contains any entity from the character-state interior
/// lists (<see cref="CharacterStateDoc.FamilyContext"/>,
/// <see cref="CharacterStateDoc.Interests"/>,
/// <see cref="CharacterStateDoc.SharedExperiences"/>,
/// <see cref="CharacterStateDoc.SelfConcept"/>). Mark-facing lists
/// excluded, same discipline as <see cref="NamedSubstrateAnchor"/>.
/// </para>
///
/// <para>
/// <b>Why Importance × multiplicative anchor bonus (not additive):</b>
/// Importance distribution is currently skewed-high (78% of thoughts
/// score 0.8-0.9 per 2026-10-22 production corpus), so flat addition
/// would make AnchorBonus the whole signal. Multiplicative preserves
/// Importance's relative ordering AND amplifies anchor-bearing thoughts
/// without zeroing out non-anchor ones. Issue #176 tracks the follow-up
/// to sharpen Importance's distribution directly.
/// </para>
///
/// <para>
/// <b>No new seams invented:</b> uses Importance already set on every
/// InnerThought record by <c>InnerThoughtMetadataPromptCommand</c> and
/// the same character-state entity pool <see cref="NamedSubstrateAnchor"/>
/// already uses. One axis of signal, one scorer.
/// </para>
/// </summary>
public static class OutreachSeedScoring
{
    /// <summary>
    /// Default anchor-presence bonus multiplier. Final score = Importance * (1 + Bonus).
    /// Picked at 0.3 so an anchor-bearing thought at Importance 0.7 (score 0.91)
    /// beats a non-anchor thought at Importance 0.9 (score 0.90). Tunable via
    /// <see cref="AniRuntime.Core.AniOptions.OutreachSeedAnchorBonus"/>.
    /// </summary>
    public const float DefaultAnchorBonus = 0.3f;

    /// <summary>
    /// Computes the outreach-seed score for a candidate thought.
    /// Returns <c>importance * (1 + anchorBonus)</c> when any character-state
    /// interior entity appears as a case-insensitive substring in
    /// <paramref name="content"/>; otherwise <paramref name="importance"/>.
    /// </summary>
    public static float Score(
        string? content,
        float importance,
        CharacterStateDoc characterState,
        float anchorBonus = DefaultAnchorBonus)
    {
        if (characterState is null) return importance;
        if (string.IsNullOrWhiteSpace(content)) return importance;

        var pool = NamedSubstrateAnchor.BuildPool(characterState);
        if (pool.Count == 0) return importance;

        var contentLower = content.ToLowerInvariant();
        foreach (var entry in pool)
        {
            if (EntryMatches(entry, contentLower))
                return importance * (1f + anchorBonus);
        }
        return importance;
    }

    /// <summary>
    /// Returns true when any match-token derived from <paramref name="entry"/>
    /// appears as a case-insensitive substring in <paramref name="contentLower"/>
    /// (which must already be lower-cased).
    ///
    /// <para>
    /// Pool entries can be long prose descriptions ("Sister Lila — my
    /// 'code-sis,' texts at midnight"). Match strategy: extract the leading
    /// name/phrase (up to first separator). Try the whole leading phrase AND
    /// each individual word of length ≥ 4 — so "Sister Lila" fires on a
    /// thought saying just "Lila", "Fleetwood Mac Landslide" fires on
    /// "Fleetwood" or "Landslide". The ≥4-char word floor keeps short
    /// connector words ("Mac", "the", "and") from producing false positives.
    /// </para>
    /// </summary>
    internal static bool EntryMatches(string entry, string contentLower)
    {
        var leadingName = ExtractLeadingName(entry);
        if (leadingName.Length < 3) return false;

        var leadingLower = leadingName.ToLowerInvariant();
        if (contentLower.Contains(leadingLower, StringComparison.Ordinal))
            return true;

        if (!leadingName.Contains(' ')) return false;
        return leadingLower
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4)
            .Any(token => contentLower.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// Pulls the leading name/phrase from a character-state entry like
    /// "Sister Lila — my 'code-sis,' texts at midnight" → "Sister Lila".
    /// Short-string entries that have no separator return trimmed as-is.
    /// Internal so unit tests can pin the extraction rules.
    /// </summary>
    internal static string ExtractLeadingName(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return string.Empty;
        var splitChars = new[] { '—', '-', ',', '.', ':', ';', '(' };
        var cutoff = entry.IndexOfAny(splitChars);
        var leading = cutoff > 0 ? entry[..cutoff] : entry;
        return leading.Trim();
    }
}
