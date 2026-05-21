using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Context;

/// <summary>
/// Production implementation of <see cref="IEmotionalContextBuilder"/>.
/// Extracted from <c>ContextBuilder</c> 2026-05-19 as the first sub-builder
/// of the SRP decomposition (`ANI-Testability-Architecture-Plan.md` §2).
///
/// Owns the four emotional-context fields that previously lived inline in
/// <c>ContextBuilder.BuildContextSnapshotAsync</c>:
/// <see cref="RelationshipHealth"/> (with on-the-fly recalculation when stale),
/// <see cref="EmotionalDrift"/> (computed from emotional history),
/// pattern awareness (Feature 12 — embedding-similarity over recent outreach),
/// processed themes (decayed emotional contributions).
///
/// Behavior preservation: each block degrades independently to null/empty on
/// retrieval failure, matching the pre-extraction inline shape exactly.
/// </summary>
public sealed class EmotionalContextBuilder : IEmotionalContextBuilder
{
    private readonly IStateStore                         _state;
    private readonly IMemorySearch                       _search;
    private readonly IMemoryPersistence                  _persist;
    private readonly IMemoryAnalytics                    _analytics;
    private readonly AniOptions                          _aniOptions;
    private readonly ILogger<EmotionalContextBuilder>    _log;

    public EmotionalContextBuilder(
        IStateStore                         state,
        IMemorySearch                       search,
        IMemoryPersistence                  persist,
        IMemoryAnalytics                    analytics,
        IOptions<AniOptions>                aniOptions,
        ILogger<EmotionalContextBuilder>    log)
    {
        _state      = state;
        _search     = search;
        _persist    = persist;
        _analytics  = analytics;
        _aniOptions = aniOptions.Value;
        _log        = log;
    }

    public async Task<EmotionalContextResult> BuildAsync(
        string              characterName,
        EmotionalState      emotionalState,
        CancellationToken   ct)
    {
        // Feature 4: Load relationship health — updated at most once per day.
        RelationshipHealth? relationshipHealth = null;
        try
        {
            relationshipHealth = await _state.GetRelationshipHealthAsync(ct).ConfigureAwait(false);

            if ((DateTimeOffset.UtcNow - relationshipHealth.LastCalculated).TotalHours >= 24)
            {
                relationshipHealth = await ComputeRelationshipHealthAsync(
                    relationshipHealth, emotionalState, ct).ConfigureAwait(false);
                await _persist.SaveRelationshipHealthAsync(relationshipHealth, ct).ConfigureAwait(false);
                _log.LogInformation("Relationship health recalculated: score={Score:F2}, phase={Phase}",
                    relationshipHealth.ConnectionScore, relationshipHealth.Phase);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load/compute relationship health — continuing without");
        }

        // Feature 8: Emotional drift detection — compare recent vs older emotional vectors.
        EmotionalDrift? emotionalDrift = null;
        try
        {
            var driftHistory = await _state.GetEmotionalHistoryAsync(48, ct).ConfigureAwait(false);
            if (driftHistory.Count >= 4)
            {
                var midpoint = driftHistory.Count / 2;
                var older    = driftHistory.Take(midpoint).ToList();
                var recent   = driftHistory.Skip(midpoint).ToList();
                emotionalDrift = EmotionalDrift.Compute(recent, older);
                if (emotionalDrift.IsSignificant)
                {
                    _log.LogInformation("Emotional drift detected: similarity={Sim:F3}, {Description}",
                        emotionalDrift.Similarity, emotionalDrift.Describe());
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Emotional drift detection failed — continuing without");
        }

        // Feature 12: Self-awareness feedback loop — analyze recent outreach for pattern clusters.
        string? patternAwareness = null;
        try
        {
            patternAwareness = await AnalyzeOutreachPatternsAsync(characterName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Feature 12: Pattern analysis failed — continuing without");
        }

        // Processed themes — topics whose emotional contributions have fully decayed.
        var processedThemes = await _analytics.GetProcessedThemesAsync(5, ct).ConfigureAwait(false);

        return new EmotionalContextResult(
            relationshipHealth,
            emotionalDrift,
            patternAwareness,
            processedThemes.ToList());
    }

    /// <summary>
    /// Feature 4: Computes relationship health from interaction metrics over a rolling window.
    /// </summary>
    internal async Task<RelationshipHealth> ComputeRelationshipHealthAsync(
        RelationshipHealth previous, EmotionalState current, CancellationToken ct)
    {
        var days = _aniOptions.RelationshipHealthWindowDays;

        // 1. Message frequency: conversations per day, normalized (0 = none, 1 = 3+/day)
        var msgCount       = await _analytics.GetRecentMessageCountAsync(days, ct).ConfigureAwait(false);
        var msgsPerDay     = (double)msgCount / days;
        var frequencyScore = Math.Min(1.0, msgsPerDay / 3.0);

        // 2. Conversation quality: average valence (0.0-1.0, center at 0.5)
        var avgValence    = await _analytics.GetAverageConversationValenceAsync(days, ct).ConfigureAwait(false);
        var qualityScore  = Math.Clamp(avgValence, 0.0, 1.0);

        // 3. Warmth trend: average warmth from emotional state history
        var history       = await _state.GetEmotionalHistoryAsync(days * 24, ct).ConfigureAwait(false);
        var warmthScore   = history.Count > 0
            ? Math.Clamp(history.Average(h => h.Warmth), 0.0, 1.0)
            : 0.5;

        // 4. Initiative balance: penalize when one side dominates
        var (outreach, inbound) = await _analytics.GetInitiativeBalanceAsync(days, ct).ConfigureAwait(false);
        var total          = outreach + inbound;
        var balanceScore   = total > 0
            ? 1.0 - Math.Abs((double)(outreach - inbound) / total)
            : 0.5;

        // Equal-weight composite
        var score = (frequencyScore + qualityScore + warmthScore + balanceScore) / 4.0;
        score = Math.Clamp(score, 0.0, 1.0);

        var phase = RelationshipHealth.DeterminePhase(score, previous.Phase);

        _log.LogDebug(
            "Relationship health inputs: freq={Freq:F2} ({MsgsPerDay:F1}/day), quality={Quality:F2}, warmth={Warmth:F2}, balance={Balance:F2} (out={Out}/in={In}) → score={Score:F2}, phase={Phase}",
            frequencyScore, msgsPerDay, qualityScore, warmthScore, balanceScore, outreach, inbound, score, phase);

        return new RelationshipHealth
        {
            ConnectionScore = score,
            Phase           = phase,
        };
    }

    /// <summary>
    /// Feature 12: Self-awareness feedback loop. Analyzes recent outreach messages
    /// for thematic repetition via pairwise embedding similarity.
    /// </summary>
    internal async Task<string?> AnalyzeOutreachPatternsAsync(string characterName, CancellationToken ct)
    {
        var recentEpisodic = (await _search.GetByTypeAsync(MemoryType.Episodic, 20, ct)
            .ConfigureAwait(false)).ToList();

        const string outreachPrefix = "I reached out to";
        var outreachRecords = recentEpisodic
            .Where(m => m.Content.StartsWith(outreachPrefix, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();

        if (outreachRecords.Count < 3) return null;

        var withEmbeddings = outreachRecords
            .Where(m => m.Embedding is { Length: > 0 })
            .ToList();

        if (withEmbeddings.Count < 3) return null;

        float totalSimilarity = 0;
        int   pairCount       = 0;
        for (var i = 0; i < withEmbeddings.Count; i++)
        {
            for (var j = i + 1; j < withEmbeddings.Count; j++)
            {
                totalSimilarity += VectorMath.CosineSimilarity(
                    withEmbeddings[i].Embedding!, withEmbeddings[j].Embedding!);
                pairCount++;
            }
        }

        var avgSimilarity = pairCount > 0 ? totalSimilarity / pairCount : 0f;

        _log.LogDebug("Feature 12: Outreach pattern similarity = {Similarity:F3} ({Count} messages, {Pairs} pairs)",
            avgSimilarity, withEmbeddings.Count, pairCount);

        if (avgSimilarity < 0.75f) return null;

        var recentTopics = outreachRecords.Take(3)
            .Select(m => m.Content.Replace(outreachPrefix, "").Trim().TrimStart(':').TrimStart().TrimStart('"').TrimEnd('"'))
            .ToList();

        return $"I notice my last few messages have been thematically similar — circling the same territory: " +
               $"\"{recentTopics[0][..Math.Min(40, recentTopics[0].Length)]}...\". " +
               "Maybe I should explore something different next time. A different corner of my mind.";
    }
}
