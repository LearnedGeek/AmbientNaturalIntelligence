using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused EF Core implementation
/// of <see cref="IMemoryAnalytics"/>. Pure read-only analytics: open
/// loops, flagged contradictions, message-rate counters, processed
/// themes from the emotional-contribution log.
///
/// Extracted from the previous monolithic <c>EfMemoryService</c>.
/// Six of the eight methods are direct LINQ-to-Entities translations of
/// the legacy raw SQL (no side effects, deterministic). Two methods
/// (<see cref="GetActiveContributionsAsync"/>,
/// <see cref="GetContributionsSinceAsync"/>) flow through the
/// <see cref="EmotionalContributionRepository"/>.
/// </summary>
public sealed class EfMemoryAnalyticsService : IMemoryAnalytics
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly ILogger<EfMemoryAnalyticsService> _log;

    public EfMemoryAnalyticsService(
        IDbContextFactory<AniDbContext> dbFactory,
        ILogger<EfMemoryAnalyticsService> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Memories
            .Where(m => m.Type == MemoryType.OpenLoop && !m.IsResolved)
            .OrderBy(m => m.OccurredAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new OpenLoop
        {
            Id          = r.Id,
            Description = r.Content,
            Context     = r.RawJson ?? string.Empty,
            Urgency     = r.Importance,
            IsResolved  = r.IsResolved,
            CreatedAt   = r.CreatedAt,
            ResolvedAt  = r.ResolvedAt,
        }).ToList();
    }

    public async Task<List<MemoryContradiction>> GetFlaggedContradictionsAsync(
        bool includeResolved = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = includeResolved
            ? db.MemoryContradictions.OrderByDescending(c => c.FlaggedAt)
            : db.MemoryContradictions.Where(c => !c.IsResolved).OrderByDescending(c => c.FlaggedAt);

        var rows = await query.ToListAsync(ct).ConfigureAwait(false);

        // Hydrate content fields by joining to memories.
        var ids = rows.SelectMany(r => new[] { r.NewMemoryId, r.ExistingMemoryId }).Distinct().ToList();
        var contentMap = await db.Memories
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Content, ct)
            .ConfigureAwait(false);

        return rows.Select(r => new MemoryContradiction
        {
            NewMemoryId      = r.NewMemoryId,
            ExistingMemoryId = r.ExistingMemoryId,
            NewContent       = contentMap.GetValueOrDefault(r.NewMemoryId, string.Empty),
            ExistingContent  = contentMap.GetValueOrDefault(r.ExistingMemoryId, string.Empty),
            Reason           = r.Reason,
            Similarity       = r.Similarity,
            FlaggedAt        = r.FlaggedAt,
            IsResolved       = r.IsResolved,
        }).ToList();
    }

    public async Task<int> GetRecentMessageCountAsync(int days, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Memories
            .Where(m => m.Type == MemoryType.Episodic
                     && m.Content.StartsWith("Conversation (")
                     && m.OccurredAt > cutoff)
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<float> GetAverageConversationValenceAsync(int days, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Memories
            .Where(m => m.Type == MemoryType.Episodic
                     && m.Content.StartsWith("Conversation (")
                     && m.OccurredAt > cutoff)
            .Select(m => (float?)m.RelationalValence)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (rows.Count == 0) return 0.5f;
        return rows.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0.5f).Average();
    }

    public async Task<(int outreach, int inbound)> GetInitiativeBalanceAsync(int days, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var outreach = await db.Memories
            .Where(m => m.Type == MemoryType.Episodic
                     && m.Content.Contains("reached out:")
                     && m.OccurredAt > cutoff)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var inbound = await db.Memories
            .Where(m => m.Type == MemoryType.Episodic
                     && m.Content.StartsWith("Conversation (")
                     && m.OccurredAt > cutoff)
            .CountAsync(ct)
            .ConfigureAwait(false);

        return (outreach, inbound);
    }

    public async Task<List<EmotionalContribution>> GetActiveContributionsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new EmotionalContributionRepository(db);
        var entities = await repo.GetActiveAsync(ct).ConfigureAwait(false);
        return entities.Select(MapContribution).ToList();
    }

    public async Task<List<EmotionalContribution>> GetContributionsSinceAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new EmotionalContributionRepository(db);
        var entities = await repo.GetSinceAsync(since, ct).ConfigureAwait(false);
        return entities.Select(MapContribution).ToList();
    }

    public async Task<List<string>> GetProcessedThemesAsync(int maxThemes = 5, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.EmotionalContributions
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.SourceContent, c.CreatedAt, c.HalfLifeHours })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var themes = new List<string>();
        foreach (var r in rows)
        {
            var elapsed = (float)(now - r.CreatedAt).TotalHours;
            if (elapsed > r.HalfLifeHours * 7 && themes.Count < maxThemes)
                themes.Add(r.SourceContent);
            if (themes.Count >= maxThemes) break;
        }
        return themes;
    }

    public async Task<AttributionDistribution> GetAttributionDistributionAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Single query, count-only projections — no record loading.
        var byAttrRows = await db.Memories
            .GroupBy(m => m.AttributedTo)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byTrustRows = await db.Memories
            .GroupBy(m => m.AttributionTrust)
            .Select(g => new { Key = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byAttr  = byAttrRows.ToDictionary(r => r.Key, r => r.Count);
        var byTrust = byTrustRows.ToDictionary(r => r.Key ?? "unverified", r => r.Count);
        var total   = byAttrRows.Sum(r => r.Count);

        return new AttributionDistribution(byAttr, byTrust, total);
    }

    private static EmotionalContribution MapContribution(EmotionalContributionEntity e) => new()
    {
        Id                = e.Id,
        SourceContent     = e.SourceContent,
        WarmthDelta       = e.WarmthDelta,
        EnergyDelta       = e.EnergyDelta,
        WorryDelta        = e.ConcernDelta,
        PlayfulnessDelta  = e.PlayfulnessDelta,
        CreatedAt         = e.CreatedAt,
        HalfLifeHours     = e.HalfLifeHours,
        Category          = Enum.TryParse<ImpactCategory>(e.Category, ignoreCase: true, out var cat)
                                ? cat : ImpactCategory.Ambient,
        Embedding         = e.Embedding,
        Severity          = e.Severity,
        IsOutreachReady   = e.IsOutreachReady,
        Register          = e.Register,
        MLEmotion         = e.MLEmotion,
        MLConfidence      = e.MLConfidence,
        MLSarcasmDetected = e.MLSarcasm,
        DivergenceScore   = e.DivergenceScore,
        AssociativeAnchor = e.AssociativeAnchor,
        SubstrateJson     = e.SubstrateJson,
    };
}
