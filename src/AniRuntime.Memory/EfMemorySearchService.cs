using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused EF Core implementation
/// of <see cref="IMemorySearch"/>. Phase 5 closeout (2026-05-18): all
/// semantic-search delegations to the legacy <c>SqliteMemoryService</c> (deleted 2026-05-18)
/// have been replaced by <see cref="ISemanticSearchComposer"/>.
///
/// Eight methods, split by responsibility:
/// <list type="bullet">
///   <item>Simple tier/type reads (<see cref="GetByTypeAsync"/>,
///     <see cref="GetByTierAsync"/>, <see cref="GetAnchoredMemoriesAsync"/>)
///     — direct EF queries via <see cref="MemoryRepository"/>.</item>
///   <item>Composite-scoring searches (<see cref="SearchAsync"/>,
///     <see cref="SearchWithScoresAsync"/>, <see cref="SearchByTypeAsync"/>,
///     <see cref="SearchByTierAsync"/>) and <see cref="GetLinkedMemoriesAsync"/>
///     — delegated to the composer.</item>
/// </list>
/// </summary>
public sealed class EfMemorySearchService : IMemorySearch
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly ISemanticSearchComposer _composer;
    private readonly ILogger<EfMemorySearchService> _log;

    public EfMemorySearchService(
        IDbContextFactory<AniDbContext> dbFactory,
        ISemanticSearchComposer composer,
        ILogger<EfMemorySearchService> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _composer  = composer  ?? throw new ArgumentNullException(nameof(composer));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false)
        => _composer.SearchAsync(query, topK, ct, enforceOriginQuota);

    public Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(
        string query, int topK = 10, CancellationToken ct = default)
        => _composer.SearchWithScoresAsync(query, topK, ct);

    public Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(
        string query, MemoryType type, int topK = 5, CancellationToken ct = default)
        => _composer.SearchByTypeAsync(query, type, topK, ct);

    public async Task<IEnumerable<MemoryRecord>> GetByTypeAsync(
        MemoryType type, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryRepository(db);
        var entities = await repo.GetByTypeAsync(type, limit, ct).ConfigureAwait(false);
        return entities.Select(EfMemoryMappings.MapToRecord);
    }

    public async Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryRepository(db);
        var entities = await repo.GetAnchoredAsync(ct).ConfigureAwait(false);
        return entities.Select(EfMemoryMappings.MapToRecord);
    }

    public Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default)
        => _composer.GetLinkedMemoriesAsync(memoryId, relationshipType, ct);

    public Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f)
        => _composer.SearchByTierAsync(query, tier, topK, ct, minCosine);

    public async Task<IEnumerable<MemoryRecord>> GetByTierAsync(
        EpistemicTier tier, int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.Memories
            .Where(m => m.Provenance == tier && m.Tier != DecayTier.Compressed)
            .OrderByDescending(m => m.OccurredAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return entities.Select(EfMemoryMappings.MapToRecord);
    }

    /// <summary>
    /// F-4 Phase 4a.1a (2026-10-22) — Perception retrieval ordered by
    /// <c>effective_weight = Importance × TopicValence_lookup × 1.0 (fatigue placeholder)</c>.
    /// See <see cref="IMemorySearch.GetPerceptionsOrderedAsync"/> XML doc for
    /// the three-dimension model and the Life Audit anchor.
    /// </summary>
    public async Task<IEnumerable<MemoryRecord>> GetPerceptionsOrderedAsync(
        IReadOnlyDictionary<string, float> topicValence,
        int limit = 5,
        IReadOnlyCollection<string>? allowedSourceNames = null,
        CancellationToken ct = default)
    {
        if (topicValence is null) throw new ArgumentNullException(nameof(topicValence));
        if (limit <= 0) return Array.Empty<MemoryRecord>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Pull a wide candidate pool and score in memory. Scope selection:
        // - type == Perception (type integer value is 5 for MemoryType.Perception)
        // - not Compressed tier
        // - optional source-name allowlist
        //
        // Pool size: 4 × limit so even with fuzzy-lookup zero-outs, we have
        // headroom to find limit records with non-trivial score. Bounded
        // enough that per-cycle query cost stays modest even at 1,000+
        // perception events in the corpus.
        var candidateCap = Math.Max(limit * 4, 20);
        var query = db.Memories
            .Where(m => m.Type == MemoryType.Perception && m.Tier != DecayTier.Compressed);

        if (allowedSourceNames is { Count: > 0 })
        {
            var set = allowedSourceNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // EF can't translate HashSet.Contains over a non-constant collection
            // into pure SQL under all providers — materialize as list for Contains
            // translation. The projection is small (set size is typically 2-3).
            var list = set.ToList();
            query = query.Where(m => m.SourceName != null && list.Contains(m.SourceName));
        }

        var candidates = await query
            .OrderByDescending(m => m.OccurredAt)
            .Take(candidateCap)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var records = candidates.Select(EfMemoryMappings.MapToRecord).ToList();

        // F-4 Phase 4a.1b (2026-10-23) — score = Significance × Importance ×
        // TopicValenceLookup. Phase 4a.1b decouples axis 1 (objective
        // world-events significance, set by ISignificanceClassifier at RSS
        // ingest) from Importance which stays as the Mark-interest-match axis.
        // Phase 4a.1c will fold in fatigue as a fourth multiplier.
        // See PerceptionScoring.FuzzyTopicValenceLookup for the lookup rule
        // (highest-weighted matching key, 0.5 default when no match).
        // F-4 Phase 4a.1c (2026-10-23) — fatigue folded in as fourth axis.
        // Composite is now Significance × Importance × TopicValence × FatigueDecay.
        // ExposureCount + LastExposedAt feed FatigueDecay; "now" is the
        // scoring moment so time-recovery is current.
        var scoringNow = DateTimeOffset.UtcNow;
        var scored = records
            .Select(r => new
            {
                Record = r,
                Score  = PerceptionScoring.PerceptionEffectiveWeight(
                    r.Significance, r.Importance, r.Content, topicValence,
                    r.ExposureCount, r.LastExposedAt, scoringNow),
            })
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Record)
            .ToList();

        _log.LogDebug(
            "F4_PHASE4A1A_PERCEPTION_ORDERED candidates={CandidateCount} returned={Returned} topicValenceSize={TopicValenceSize} allowedSources={Allowed}",
            records.Count, scored.Count, topicValence.Count,
            allowedSourceNames is null ? "all" : string.Join(",", allowedSourceNames));

        return scored;
    }
}
