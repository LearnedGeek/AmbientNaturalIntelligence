using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — EF Core implementation of
/// <see cref="ISemanticSearchComposer"/>. Behaviour-preserved port of
/// SqliteMemoryService's full semantic-search pipeline.
///
/// <para>
/// Houses the composite scoring (Park et al. cosine + importance +
/// recency with Feature 24 type-aware decay), the MMR diversity rerank
/// (Carbonell &amp; Goldstein 1998), the Agentic-Lens Layer 1 Phase 1c
/// protected-slots backfill, the Theme G Phase G3.4.B own-output
/// ceiling, the Apr 30 tier-aware recency-off Facts path, the Theme P
/// P.4 raw-cosine noise floor, and the Feature 31 link-enhanced
/// retrieval. Single class so the composition order is read-able
/// top-to-bottom without re-discovering it from grep.
/// </para>
/// <para>
/// All static helper methods (ApplyMmrRerank, ApplyProtectedSlotsBackfill,
/// ApplyOwnOutputCeiling) are kept <c>internal static</c> so the existing
/// pure-function unit tests against the legacy continue to work
/// (signatures preserved).
/// </para>
/// </summary>
public sealed class EfSemanticSearchComposer : ISemanticSearchComposer
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly IOllamaClient? _ollama;
    private readonly AniOptions _options;
    private readonly ILogger<EfSemanticSearchComposer> _log;

    public EfSemanticSearchComposer(
        IDbContextFactory<AniDbContext> dbFactory,
        IOptions<AniOptions> options,
        ILogger<EfSemanticSearchComposer> log,
        IOllamaClient? ollama = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _options   = options.Value;
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
        _ollama    = ollama;
    }

    // ══════════════════════════════════════════════════════════════════
    // SearchAsync — full composition (cosine + importance + recency,
    // MMR rerank, optional protected-slots floor + own-output ceiling
    // for inner-thought retrieval).
    // ══════════════════════════════════════════════════════════════════
    public async Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false)
    {
        if (_ollama is null)
        {
            _log.LogDebug("Search unavailable (no embedding client) — falling back to recency");
            return await FallbackRecentAsync(topK, ct).ConfigureAwait(false);
        }

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _ollama.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to embed search query — falling back to recency");
            return await FallbackRecentAsync(topK, ct).ConfigureAwait(false);
        }
        if (queryEmbedding.Length == 0)
            return await FallbackRecentAsync(topK, ct).ConfigureAwait(false);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var memoryRepo = new MemoryRepository(db);
        var candidates = await memoryRepo.GetForSemanticSearchAsync(ct).ConfigureAwait(false);

        var scoredAll = ScoreCandidates(candidates, queryEmbedding, includeRecency: true);

        var ranked = _options.RetrievalDiversityEnabled && scoredAll.Count > 0
            ? ApplyMmrRerank(scoredAll, topK, (float)_options.RetrievalDiversityLambda)
            : scoredAll.OrderByDescending(x => x.CompositeScore).Take(topK).ToList();

        var protectedSlotsActive = enforceOriginQuota && _options.RetrievalProtectedSlotsEnabled;
        if (protectedSlotsActive && ranked.Count > 0)
        {
            ranked = ApplyProtectedSlotsBackfill(
                ranked, scoredAll, topK,
                (float)_options.MinNonCaregiverRetrievalFraction);
        }

        var ownOutputCeilingActive = enforceOriginQuota && _options.RetrievalOwnOutputCeilingEnabled;
        if (ownOutputCeilingActive && ranked.Count > 0)
        {
            ranked = ApplyOwnOutputCeiling(
                ranked, scoredAll, topK,
                (float)_options.RetrievalOwnOutputCeilingFraction);
        }

        if (ranked.Count > 0)
        {
            var top = ranked[0];
            _log.LogDebug(
                "Semantic search: {Candidates} candidates, top score={Score:F3} (cosine={Cosine:F3}, importance={Importance:F2}, type={Type}, mmr={Mmr}, slots={Slots}): {Content}",
                candidates.Count, top.CompositeScore, top.CosineSimilarity, top.Record.Importance, top.Record.Type,
                _options.RetrievalDiversityEnabled, protectedSlotsActive,
                top.Record.Content.Length > 80 ? top.Record.Content[..80] + "..." : top.Record.Content);
        }

        return ranked.Select(x => x.Record);
    }

    // ══════════════════════════════════════════════════════════════════
    // SearchWithScoresAsync — same composition plus Feature 31 link
    // enhancement (1-hop neighbours of the top-K, ≥0.40 cosine, +0.05
    // composite-score bonus for being linked, top-3 added).
    // ══════════════════════════════════════════════════════════════════
    public async Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(
        string query, int topK = 10, CancellationToken ct = default)
    {
        if (_ollama is null)
        {
            _log.LogDebug("Scored search unavailable (no embedding client) — returning empty");
            return Enumerable.Empty<ScoredMemory>();
        }

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _ollama.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to embed search query for scored search — returning empty");
            return Enumerable.Empty<ScoredMemory>();
        }
        if (queryEmbedding.Length == 0) return Enumerable.Empty<ScoredMemory>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var memoryRepo = new MemoryRepository(db);
        var candidates = await memoryRepo.GetForSemanticSearchAsync(ct).ConfigureAwait(false);

        var scoredAll = ScoreCandidates(candidates, queryEmbedding, includeRecency: true);

        var ranked = _options.RetrievalDiversityEnabled && scoredAll.Count > 0
            ? ApplyMmrRerank(scoredAll, topK, (float)_options.RetrievalDiversityLambda)
            : scoredAll.OrderByDescending(x => x.CompositeScore).Take(topK).ToList();

        if (ranked.Count > 0)
        {
            var top = ranked[0];
            _log.LogDebug(
                "Scored search: {Candidates} candidates, top composite={Composite:F3} cosine={Cosine:F3} (type={Type}, origin={Origin}, mmr={Mmr}): {Content}",
                candidates.Count, top.CompositeScore, top.CosineSimilarity, top.Record.Type, top.OriginTier,
                _options.RetrievalDiversityEnabled,
                top.Record.Content.Length > 80 ? top.Record.Content[..80] + "..." : top.Record.Content);
        }

        // Feature 31 link-enhancement.
        try
        {
            var resultIds = new HashSet<Guid>(ranked.Select(r => r.Record.Id));
            var linkedIds = await GetLinkedMemoryIdsAsync(resultIds, db, ct).ConfigureAwait(false);

            if (linkedIds.Count > 0)
            {
                const float LinkRelevanceThreshold = 0.40f;
                var linkedCandidates = new List<ScoredMemory>();

                var idsToFetch = linkedIds.Except(resultIds).ToList();
                if (idsToFetch.Count > 0)
                {
                    var linkedEntities = await db.Memories
                        .Where(m => idsToFetch.Contains(m.Id))
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    foreach (var linked in linkedEntities)
                    {
                        if (linked.Embedding is null || linked.Embedding.Length != queryEmbedding.Length) continue;
                        var cosine = VectorMath.CosineSimilarity(queryEmbedding, linked.Embedding);
                        if (cosine < LinkRelevanceThreshold) continue;

                        var record = EfMemoryMappings.MapToRecord(linked);
                        var composite = ComputeRetrievalScore(queryEmbedding, record, includeRecency: true);
                        linkedCandidates.Add(new ScoredMemory(record, composite + 0.05f, cosine)
                        {
                            OriginTier = RetrievalOriginClassifier.Classify(record),
                        });
                    }
                }

                if (linkedCandidates.Count > 0)
                {
                    var topLinked = linkedCandidates
                        .OrderByDescending(x => x.CosineSimilarity)
                        .Take(3)
                        .ToList();

                    ranked.AddRange(topLinked);
                    ranked = ranked.OrderByDescending(x => x.CompositeScore).Take(topK).ToList();
                    _log.LogDebug("Link-enhanced retrieval: {Candidates} candidates above threshold, added top {Count}",
                        linkedCandidates.Count, topLinked.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Link-enhanced retrieval failed — returning standard results");
        }

        return ranked;
    }

    // ══════════════════════════════════════════════════════════════════
    // SearchByTypeAsync — type-filtered candidate set, composite scoring.
    // ══════════════════════════════════════════════════════════════════
    public async Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(
        string query, MemoryType type, int topK = 5, CancellationToken ct = default)
    {
        if (_ollama is null)
            return await FallbackByTypeRecentAsync(type, topK, ct).ConfigureAwait(false);

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _ollama.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to embed search query for type {Type} — falling back to recency", type);
            return await FallbackByTypeRecentAsync(type, topK, ct).ConfigureAwait(false);
        }
        if (queryEmbedding.Length == 0)
            return await FallbackByTypeRecentAsync(type, topK, ct).ConfigureAwait(false);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var memoryRepo = new MemoryRepository(db);
        var candidates = await memoryRepo.GetForSemanticSearchByTypeAsync(type, ct).ConfigureAwait(false);

        var scored = candidates
            .Where(e => e.Embedding is not null && e.Embedding.Length == queryEmbedding.Length)
            .Select(e =>
            {
                var record = EfMemoryMappings.MapToRecord(e);
                var score = ComputeRetrievalScore(queryEmbedding, record, includeRecency: true);
                return (record, score);
            })
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();

        _log.LogDebug("Semantic search (type={Type}): {Candidates} candidates, top score={TopScore:F3}",
            type, candidates.Count, scored.Count > 0 ? scored[0].score : 0f);

        return scored.Select(x => x.record);
    }

    // ══════════════════════════════════════════════════════════════════
    // SearchByTierAsync — Apr 30 tier-aware (Facts = cosine+importance,
    // no recency); Theme P P.4 raw-cosine floor.
    // ══════════════════════════════════════════════════════════════════
    public async Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f)
    {
        if (_ollama is null)
        {
            _log.LogDebug("Tier search unavailable (no embedding client) — returning empty");
            return Enumerable.Empty<ScoredMemory>();
        }

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _ollama.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to embed tier-search query — returning empty");
            return Enumerable.Empty<ScoredMemory>();
        }
        if (queryEmbedding.Length == 0) return Enumerable.Empty<ScoredMemory>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var memoryRepo = new MemoryRepository(db);
        var candidates = await memoryRepo.GetForTierScopedSearchAsync(tier, ct).ConfigureAwait(false);

        var includeRecency = tier != EpistemicTier.Facts;

        var scored = candidates
            .Where(e => e.Embedding is not null && e.Embedding.Length == queryEmbedding.Length)
            .Select(e =>
            {
                var record   = EfMemoryMappings.MapToRecord(e);
                var cosine   = VectorMath.CosineSimilarity(queryEmbedding, e.Embedding!);
                var composite= ComputeRetrievalScore(queryEmbedding, record, includeRecency);
                return new ScoredMemory(record, composite, cosine);
            })
            .ToList();

        var (passingFloor, droppedByFloor, anchoredBypassed) =
            ApplyCosineFloorWithAnchoredBypass(scored, minCosine);

        var ranked = passingFloor.OrderByDescending(x => x.CompositeScore).Take(topK).ToList();

        _log.LogDebug(
            "Tier search ({Tier}): {Candidates} candidates, {Results} results, top composite={TopScore:F3}, top cosine={TopCosine:F3}, includeRecency={IncludeRecency}, min_cosine={MinCosine:F2}, dropped_below_threshold={Dropped}, anchored_bypassed_floor={AnchoredBypassed}",
            tier, candidates.Count, ranked.Count,
            ranked.Count > 0 ? ranked[0].CompositeScore   : 0f,
            ranked.Count > 0 ? ranked[0].CosineSimilarity : 0f,
            includeRecency, minCosine, droppedByFloor, anchoredBypassed);

        return ranked;
    }

    // ══════════════════════════════════════════════════════════════════
    // GetLinkedMemoriesAsync — Feature 31 1-hop bidirectional traversal.
    // ══════════════════════════════════════════════════════════════════
    public async Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var linksQuery = db.MemoryLinks.AsQueryable();
        if (!string.IsNullOrEmpty(relationshipType))
            linksQuery = linksQuery.Where(l => l.Relationship == relationshipType);

        var bothDirections = await linksQuery
            .Where(l => l.SourceId == memoryId || l.TargetId == memoryId)
            .Select(l => l.SourceId == memoryId ? l.TargetId : l.SourceId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (bothDirections.Count == 0) return Enumerable.Empty<MemoryRecord>();

        var entities = await db.Memories
            .Where(m => bothDirections.Contains(m.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(EfMemoryMappings.MapToRecord);
    }

    // ══════════════════════════════════════════════════════════════════
    // Helpers — scoring + reranks + fallbacks.
    // ══════════════════════════════════════════════════════════════════

    private async Task<IEnumerable<MemoryRecord>> FallbackRecentAsync(int limit, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.Memories
            .Where(m => m.Embedding != null && m.Tier != DecayTier.Compressed)
            .OrderByDescending(m => m.OccurredAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return entities.Select(EfMemoryMappings.MapToRecord);
    }

    private async Task<IEnumerable<MemoryRecord>> FallbackByTypeRecentAsync(
        MemoryType type, int limit, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var memoryRepo = new MemoryRepository(db);
        var entities = await memoryRepo.GetByTypeAsync(type, limit, ct).ConfigureAwait(false);
        return entities.Select(EfMemoryMappings.MapToRecord);
    }

    private List<ScoredMemory> ScoreCandidates(
        IEnumerable<MemoryEntity> candidates, float[] queryEmbedding, bool includeRecency)
    {
        return candidates
            .Where(e => e.Embedding is not null && e.Embedding.Length == queryEmbedding.Length)
            .Select(e =>
            {
                var record = EfMemoryMappings.MapToRecord(e);
                var cosine = VectorMath.CosineSimilarity(queryEmbedding, e.Embedding!);
                var composite = ComputeRetrievalScore(queryEmbedding, record, includeRecency);
                return new ScoredMemory(record, composite, cosine)
                {
                    OriginTier = RetrievalOriginClassifier.Classify(record),
                };
            })
            .ToList();
    }

    private static async Task<HashSet<Guid>> GetLinkedMemoryIdsAsync(
        HashSet<Guid> sourceIds, AniDbContext db, CancellationToken ct)
    {
        if (sourceIds.Count == 0) return new HashSet<Guid>();

        var ids = await db.MemoryLinks
            .Where(l => sourceIds.Contains(l.SourceId) || sourceIds.Contains(l.TargetId))
            .Select(l => sourceIds.Contains(l.SourceId) ? l.TargetId : l.SourceId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return ids.ToHashSet();
    }

    /// <summary>
    /// Feature 20 + Feature 24 + Apr 30 tier-aware: composite retrieval
    /// score = α·cosine + β·importance + γ·recency_decay. When
    /// <paramref name="includeRecency"/> is false (Facts tier), the γ
    /// weight is redistributed proportionally onto α and β so the
    /// magnitude stays comparable.
    /// </summary>
    internal float ComputeRetrievalScore(float[] queryEmbedding, MemoryRecord record, bool includeRecency)
    {
        var cosine = VectorMath.CosineSimilarity(queryEmbedding, record.Embedding!);
        var importance = record.Importance;

        if (!includeRecency)
        {
            var totalWithoutRecency = _options.RetrievalWeightCosine + _options.RetrievalWeightImportance;
            if (totalWithoutRecency <= 0.0) return 0f;
            return (float)(
                _options.RetrievalWeightCosine     / totalWithoutRecency * cosine +
                _options.RetrievalWeightImportance / totalWithoutRecency * importance);
        }

        float recency;
        if (record.DecayTier == DecayTier.Anchored)
        {
            recency = 1.0f;
        }
        else
        {
            var hoursSinceCreation = (DateTimeOffset.UtcNow - record.OccurredAt).TotalHours;
            var lambda = _options.RetrievalRecencyDecayHours * GetDecayMultiplier(record);
            recency = (float)Math.Exp(-hoursSinceCreation / lambda);
        }

        return (float)(
            _options.RetrievalWeightCosine     * cosine +
            _options.RetrievalWeightImportance * importance +
            _options.RetrievalWeightRecency    * recency);
    }

    /// <summary>
    /// Feature 24 — type-aware decay multiplier. High-significance memory
    /// types persist longer; routine observations fade faster.
    /// </summary>
    internal static float GetDecayMultiplier(MemoryRecord record) => record.Type switch
    {
        MemoryType.Episodic     => 2.0f,
        MemoryType.Semantic     => 2.0f,
        MemoryType.Commitment   => 2.0f,
        MemoryType.OpenLoop     => 1.5f,
        MemoryType.InnerThought => 1.0f,
        MemoryType.Perception   => 0.5f,
        _                       => 1.0f,
    };

    // ── Static rerank/backfill helpers (signatures preserved from legacy
    //    for test-binary compatibility — these are also exercised by the
    //    SqliteMemoryService legacy code path under UseEfDataLayer=false).
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Foundation-memory bypass (May 28, 2026) — apply the cosine-similarity
    /// noise floor used by the verifier and composition substrate paths, but
    /// let anchored records (foundation memories: character seeds, world
    /// canon, Mia/Sarah/Kevin identity scaffolding) pass regardless of
    /// query-relevance. The floor exists to filter "topically unrelated
    /// noise," but anchored records are never noise — they're scaffolding
    /// that must always reach the consumer that asked.
    ///
    /// <para>
    /// Empirical anchor: 2026-05-28 01:56 outreach where 1002 Facts-tier
    /// candidates all scored below the 0.75 verifier floor, including the
    /// anchored character-seed record about Mia, so the verifier received
    /// zero canonical substrate and a spatial-presence confabulation
    /// ("Hey love, I'm pulling up to Mia's right now…") dispatched.
    /// </para>
    ///
    /// <para>
    /// Returns the filtered list (anchored ∪ cosine-passers), the count of
    /// records dropped by the floor (excluding anchored bypasses), and the
    /// count of anchored records that passed only because of the bypass.
    /// </para>
    /// </summary>
    internal static (List<ScoredMemory> PassingFloor, int DroppedByFloor, int AnchoredBypassed)
        ApplyCosineFloorWithAnchoredBypass(List<ScoredMemory> scored, float minCosine)
    {
        if (minCosine <= 0f)
            return (scored, 0, 0);

        var passingFloor = new List<ScoredMemory>(scored.Count);
        var droppedByFloor = 0;
        var anchoredBypassed = 0;

        foreach (var s in scored)
        {
            var passesCosine = s.CosineSimilarity >= minCosine;
            var isAnchored = s.Record.DecayTier == DecayTier.Anchored;

            if (passesCosine)
            {
                passingFloor.Add(s);
            }
            else if (isAnchored)
            {
                passingFloor.Add(s);
                anchoredBypassed++;
            }
            else
            {
                droppedByFloor++;
            }
        }

        return (passingFloor, droppedByFloor, anchoredBypassed);
    }

    /// <summary>
    /// Agentic Lens Layer 1 Phase 1b — MMR diversity rerank.
    /// </summary>
    internal static List<ScoredMemory> ApplyMmrRerank(
        List<ScoredMemory> scoredCandidates, int topK, float lambda)
    {
        if (scoredCandidates.Count == 0 || topK <= 0) return new List<ScoredMemory>();

        var selected  = new List<ScoredMemory>(Math.Min(topK, scoredCandidates.Count));
        var remaining = new List<ScoredMemory>(scoredCandidates);

        var first = remaining[0];
        for (var i = 1; i < remaining.Count; i++)
        {
            if (remaining[i].CompositeScore > first.CompositeScore)
                first = remaining[i];
        }
        selected.Add(first);
        remaining.Remove(first);

        while (selected.Count < topK && remaining.Count > 0)
        {
            ScoredMemory? best = null;
            var bestAdjusted = float.NegativeInfinity;

            foreach (var candidate in remaining)
            {
                var maxSimilarity = 0f;
                if (candidate.Record.Embedding is not null)
                {
                    foreach (var sel in selected)
                    {
                        if (sel.Record.Embedding is null) continue;
                        if (sel.Record.Embedding.Length != candidate.Record.Embedding.Length) continue;
                        var sim = VectorMath.CosineSimilarity(sel.Record.Embedding, candidate.Record.Embedding);
                        if (sim > maxSimilarity) maxSimilarity = sim;
                    }
                }

                var adjusted = candidate.CompositeScore - (lambda * maxSimilarity);
                if (adjusted > bestAdjusted)
                {
                    bestAdjusted = adjusted;
                    best = candidate;
                }
            }

            if (best is null) break;
            selected.Add(best);
            remaining.Remove(best);
        }

        return selected;
    }

    /// <summary>
    /// Agentic Lens Layer 1 Phase 1c — protected-slot backfill for non-
    /// caregiver origins.
    /// </summary>
    internal static List<ScoredMemory> ApplyProtectedSlotsBackfill(
        List<ScoredMemory> rankedTopK,
        List<ScoredMemory> allCandidates,
        int topK,
        float minNonCaregiverFraction)
    {
        if (rankedTopK.Count == 0 || topK <= 0 || minNonCaregiverFraction <= 0f)
            return rankedTopK;

        var requiredNonCaregiver = (int)Math.Ceiling(topK * minNonCaregiverFraction);
        var currentNonCaregiver  = rankedTopK.Count(r => !RetrievalOriginClassifier.IsCaregiverOriented(r.OriginTier));
        var shortfall = requiredNonCaregiver - currentNonCaregiver;
        if (shortfall <= 0) return rankedTopK;

        var selectedIds = new HashSet<Guid>(rankedTopK.Select(r => r.Record.Id));
        var backfillPool = allCandidates
            .Where(c => !selectedIds.Contains(c.Record.Id))
            .Where(c => !RetrievalOriginClassifier.IsCaregiverOriented(c.OriginTier))
            .OrderByDescending(c => c.CompositeScore)
            .Take(shortfall)
            .ToList();

        if (backfillPool.Count == 0) return rankedTopK;

        var result = new List<ScoredMemory>(rankedTopK);
        var swapsNeeded = Math.Min(backfillPool.Count, shortfall);

        var caregiverSlotsOrderedByScore = result
            .Where(r => RetrievalOriginClassifier.IsCaregiverOriented(r.OriginTier))
            .OrderBy(r => r.CompositeScore)
            .Take(swapsNeeded)
            .ToList();

        foreach (var weakCaregiver in caregiverSlotsOrderedByScore)
            result.Remove(weakCaregiver);

        result.AddRange(backfillPool.Take(swapsNeeded));

        return result.OrderByDescending(r => r.CompositeScore).ToList();
    }

    /// <summary>
    /// Theme G Phase G3.4.B — own-output retrieval ceiling.
    /// </summary>
    internal static List<ScoredMemory> ApplyOwnOutputCeiling(
        List<ScoredMemory> rankedTopK,
        List<ScoredMemory> allCandidates,
        int topK,
        float maxOwnOutputFraction)
    {
        if (rankedTopK.Count == 0 || topK <= 0 || maxOwnOutputFraction >= 1.0f)
            return rankedTopK;
        if (maxOwnOutputFraction < 0f) maxOwnOutputFraction = 0f;

        var maxOwnOutput     = (int)Math.Floor(topK * maxOwnOutputFraction);
        var currentOwnOutput = rankedTopK.Count(r => RetrievalOriginClassifier.IsOwnOutput(r.OriginTier));
        var excess           = currentOwnOutput - maxOwnOutput;
        if (excess <= 0) return rankedTopK;

        var selectedIds = new HashSet<Guid>(rankedTopK.Select(r => r.Record.Id));
        var backfillPool = allCandidates
            .Where(c => !selectedIds.Contains(c.Record.Id))
            .Where(c => !RetrievalOriginClassifier.IsOwnOutput(c.OriginTier))
            .OrderByDescending(c => c.CompositeScore)
            .Take(excess)
            .ToList();

        var ownOutputSlotsOrderedByScore = rankedTopK
            .Where(r => RetrievalOriginClassifier.IsOwnOutput(r.OriginTier))
            .OrderBy(r => r.CompositeScore)
            .Take(excess)
            .ToList();

        var result = new List<ScoredMemory>(rankedTopK);
        foreach (var weakOwnOutput in ownOutputSlotsOrderedByScore)
            result.Remove(weakOwnOutput);

        result.AddRange(backfillPool);
        return result.OrderByDescending(r => r.CompositeScore).ToList();
    }
}
