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

        // Issue #93 Phase 4 (2026-07-09) — hybrid RRF fusion (tier-agnostic).
        int bm25RescueCount = 0;
        if (_options.HybridRetrievalEnabled && scoredAll.Count > 0)
        {
            var bm25RankById = await FetchBm25RanksAsync(
                db, query, tier: null, _options.HybridRetrievalBm25TopN, ct).ConfigureAwait(false);
            scoredAll = FuseByRrf(scoredAll, bm25RankById, scoredAll.Count, _options.HybridRetrievalRrfK, out bm25RescueCount);
        }

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
                "Semantic search: {Candidates} candidates, top score={Score:F3} (cosine={Cosine:F3}, importance={Importance:F2}, type={Type}, mmr={Mmr}, slots={Slots}, hybrid={Hybrid}, bm25_rescues={Bm25Rescues}): {Content}",
                candidates.Count, top.CompositeScore, top.CosineSimilarity, top.Record.Importance, top.Record.Type,
                _options.RetrievalDiversityEnabled, protectedSlotsActive,
                _options.HybridRetrievalEnabled, bm25RescueCount,
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

        // Feature 31 link-enhancement — PRE-fusion pass.
        //
        // Issue #98 (2026-07-16) refactor. Previously ran AFTER RRF fusion,
        // computing raw-composite scores for linked candidates and jamming
        // them into the ranked list; the raw scores (0.3–0.9) massively
        // out-ranked the RRF-fused originals (0.03 max) because they were on
        // completely different scales. Empirical anchor: 2026-07-15 tool-call
        // test where Ani's own reply from tonight ranked #1 for query "WCTC"
        // at composite 0.626, burying the canonical Facts substrate.
        //
        // Fix: identify linked candidates from a PRE-fusion top-K preview,
        // add them to `scoredAll` with raw composite (no +0.05 bonus), and
        // let RRF fusion score them alongside everything else. Linked
        // candidates now compete on the same axis as fusion-scored originals.
        //
        // Kill switch: RetrievalLinkEnhancementEnabled=false skips the block
        // entirely — pre-fix behavior can be restored on the empirical anchor
        // via a single config flip.
        if (_options.RetrievalLinkEnhancementEnabled && scoredAll.Count > 0)
        {
            try
            {
                var previewIds = new HashSet<Guid>(
                    scoredAll.OrderByDescending(x => x.CompositeScore).Take(topK)
                        .Select(x => x.Record.Id));
                var linkedIds = await GetLinkedMemoryIdsAsync(previewIds, db, ct).ConfigureAwait(false);

                if (linkedIds.Count > 0)
                {
                    const float LinkRelevanceThreshold = 0.40f;
                    var alreadyScored = new HashSet<Guid>(scoredAll.Select(x => x.Record.Id));
                    var idsToFetch = linkedIds.Except(alreadyScored).ToList();

                    if (idsToFetch.Count > 0)
                    {
                        var linkedEntities = await db.Memories
                            .Where(m => idsToFetch.Contains(m.Id))
                            .ToListAsync(ct)
                            .ConfigureAwait(false);
                        var addedCount = 0;
                        foreach (var linked in linkedEntities)
                        {
                            if (linked.Embedding is null || linked.Embedding.Length != queryEmbedding.Length) continue;
                            var cosine = VectorMath.CosineSimilarity(queryEmbedding, linked.Embedding);
                            if (cosine < LinkRelevanceThreshold) continue;

                            var record = EfMemoryMappings.MapToRecord(linked);
                            var composite = ComputeRetrievalScore(queryEmbedding, record, includeRecency: true);
                            scoredAll.Add(new ScoredMemory(record, composite, cosine)
                            {
                                OriginTier = RetrievalOriginClassifier.Classify(record),
                            });
                            addedCount++;
                        }
                        if (addedCount > 0)
                            _log.LogDebug("Link-enhancement pre-fusion: added {Count} linked candidates to scored pool", addedCount);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Link-enhancement (pre-fusion) failed — proceeding without");
            }
        }

        // Issue #93 Phase 4 (2026-07-09) — hybrid RRF fusion (tier-agnostic
        // since this method operates across the full pool). MMR diversity
        // rerank still runs after fusion when enabled, so diversity + hybrid
        // compose cleanly. Post-Issue-#98 refactor: linked candidates from
        // Feature 31 are now already in `scoredAll` at this point, so RRF
        // scores them consistently with fusion-scored originals.
        int bm25RescueCount = 0;
        if (_options.HybridRetrievalEnabled && scoredAll.Count > 0)
        {
            var bm25RankById = await FetchBm25RanksAsync(
                db, query, tier: null, _options.HybridRetrievalBm25TopN, ct).ConfigureAwait(false);
            // Fuse over the ENTIRE scoredAll (not just top-K) so MMR can
            // pick from the fused ordering.
            scoredAll = FuseByRrf(scoredAll, bm25RankById, scoredAll.Count, _options.HybridRetrievalRrfK, out bm25RescueCount);
        }

        var ranked = _options.RetrievalDiversityEnabled && scoredAll.Count > 0
            ? ApplyMmrRerank(scoredAll, topK, (float)_options.RetrievalDiversityLambda)
            : scoredAll.OrderByDescending(x => x.CompositeScore).Take(topK).ToList();

        if (ranked.Count > 0)
        {
            var top = ranked[0];
            _log.LogDebug(
                "Scored search: {Candidates} candidates, top composite={Composite:F3} cosine={Cosine:F3} (type={Type}, origin={Origin}, mmr={Mmr}, hybrid={Hybrid}, bm25_rescues={Bm25Rescues}): {Content}",
                candidates.Count, top.CompositeScore, top.CosineSimilarity, top.Record.Type, top.OriginTier,
                _options.RetrievalDiversityEnabled, _options.HybridRetrievalEnabled, bm25RescueCount,
                top.Record.Content.Length > 80 ? top.Record.Content[..80] + "..." : top.Record.Content);
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

        // Issue #93 Phase 4 (2026-07-09) — hybrid RRF fusion. Fetch BM25
        // ranks for this tier and fuse with composite ranks. See the
        // ANI-Retrieval-Consultation-2026-07-08 empirical anchor: the
        // WCTC teaching-confirmation records sit at composite rank 47/600
        // by cosine but at BM25 rank 1 — fusion surfaces them into top-K.
        List<ScoredMemory> ranked;
        int bm25RescueCount = 0;
        if (_options.HybridRetrievalEnabled)
        {
            var bm25RankById = await FetchBm25RanksAsync(
                db, query, tier, _options.HybridRetrievalBm25TopN, ct).ConfigureAwait(false);
            ranked = FuseByRrf(passingFloor, bm25RankById, topK, _options.HybridRetrievalRrfK, out bm25RescueCount);
        }
        else
        {
            ranked = passingFloor.OrderByDescending(x => x.CompositeScore).Take(topK).ToList();
        }

        // Pre-filter top cosine: the best cosine in the candidate pool
        // BEFORE the floor was applied. Distinct from the post-filter
        // top_cosine field below — when no records pass the floor the
        // post-filter field shows 0.000 as the empty-list default, which
        // hides whether the pool actually had healthy embeddings sitting
        // just under the floor or pathologically all-zero embeddings.
        // This field disambiguates definitively on every cycle.
        var preFilterTopCosine = scored.Count > 0 ? scored.Max(s => s.CosineSimilarity) : 0f;

        _log.LogDebug(
            "Tier search ({Tier}): {Candidates} candidates, {Results} results, top composite={TopScore:F3}, top cosine={TopCosine:F3}, pre_filter_top_cosine={PreFilterTopCosine:F3}, includeRecency={IncludeRecency}, min_cosine={MinCosine:F2}, dropped_below_threshold={Dropped}, anchored_bypassed_floor={AnchoredBypassed}, hybrid_enabled={HybridEnabled}, bm25_rescues={Bm25Rescues}",
            tier, candidates.Count, ranked.Count,
            ranked.Count > 0 ? ranked[0].CompositeScore   : 0f,
            ranked.Count > 0 ? ranked[0].CosineSimilarity : 0f,
            preFilterTopCosine,
            includeRecency, minCosine, droppedByFloor, anchoredBypassed,
            _options.HybridRetrievalEnabled, bm25RescueCount);

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
    ///
    /// **Issue #93 (2026-07-06) — confirmation bias.** After the base
    /// composite is computed, records with <see cref="MemoryRecord.ConfirmedAt"/>
    /// set (Facts + Episodic canonical + Mark ///tag-confirmed Interior)
    /// receive a multiplicative bump: <c>score *= (1 + boost)</c>. Applied
    /// AFTER the recency-off branch normalisation so both branches see the
    /// same boost factor. The goal is that real (Episodic) Kevin content
    /// outranks importance-inflated (Interior) Kevin fabrications on
    /// Kevin-thread queries without hard-excluding Interior from the pool.
    /// </summary>
    internal float ComputeRetrievalScore(float[] queryEmbedding, MemoryRecord record, bool includeRecency)
    {
        var cosine = VectorMath.CosineSimilarity(queryEmbedding, record.Embedding!);
        var importance = record.Importance;

        float baseScore;
        if (!includeRecency)
        {
            var totalWithoutRecency = _options.RetrievalWeightCosine + _options.RetrievalWeightImportance;
            if (totalWithoutRecency <= 0.0) return 0f;
            baseScore = (float)(
                _options.RetrievalWeightCosine     / totalWithoutRecency * cosine +
                _options.RetrievalWeightImportance / totalWithoutRecency * importance);
        }
        else
        {
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

            baseScore = (float)(
                _options.RetrievalWeightCosine     * cosine +
                _options.RetrievalWeightImportance * importance +
                _options.RetrievalWeightRecency    * recency);
        }

        // Issue #93 confirmation bias — multiplicative bump for confirmed records.
        if (record.ConfirmedAt.HasValue && _options.RetrievalConfirmationBoost > 0.0)
            baseScore *= (float)(1.0 + _options.RetrievalConfirmationBoost);

        return baseScore;
    }

    /// <summary>
    /// Issue #93 Phase 4 (2026-07-09) — fetch BM25 ranks for the top-N
    /// records in the given tier for the given query, via SQLite FTS5.
    /// Returns a map of memory-id → BM25 rank (1-based, lower = better).
    /// Records outside the top-N are not present in the map — the RRF
    /// fusion treats absence as "worst rank" so BM25-invisible records
    /// still rank via composite alone.
    ///
    /// <para>Uses <c>memories_fts.MATCH</c> with the raw query text —
    /// FTS5's porter+unicode61 tokenizer handles stemming (teach/teaching)
    /// and stopword-agnostic ranking via BM25. See
    /// <see cref="AniDbContext.EnsureFtsIndexAsync"/> for the index shape.</para>
    ///
    /// <para>Empirical anchor: for the April 24 "back from teaching"
    /// Interior record, this returns the exact confirming Mark text
    /// (twilio-inbound "hey babe! Back from teaching!") at BM25 rank 1
    /// even though pure cosine ranked it at position 47/1557.</para>
    /// </summary>
    private async Task<Dictionary<Guid, int>> FetchBm25RanksAsync(
        AniDbContext db, string query, EpistemicTier? tier, int topN, CancellationToken ct)
    {
        var result = new Dictionary<Guid, int>();
        if (string.IsNullOrWhiteSpace(query) || topN <= 0) return result;

        // Simple defensive escape: strip FTS5 syntax characters that could
        // otherwise be interpreted as query operators. FTS5 supports NEAR,
        // AND, OR, NOT, +, - and column filters — for our purposes we want
        // pure token match.
        var safeQuery = SanitizeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(safeQuery)) return result;

        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            var tierClause = tier.HasValue ? "AND m.provenance = $tier" : string.Empty;
            cmd.CommandText = $@"
                SELECT m.id
                FROM memories_fts
                JOIN memories m ON m.id = memories_fts.memory_id
                WHERE memories_fts MATCH $q
                  {tierClause}
                  AND m.validity = 'valid'
                ORDER BY bm25(memories_fts)
                LIMIT $topN;";
            var pQuery = cmd.CreateParameter(); pQuery.ParameterName = "$q";    pQuery.Value = safeQuery; cmd.Parameters.Add(pQuery);
            var pN     = cmd.CreateParameter(); pN.ParameterName     = "$topN"; pN.Value     = topN;      cmd.Parameters.Add(pN);
            if (tier.HasValue)
            {
                var pTier = cmd.CreateParameter(); pTier.ParameterName = "$tier"; pTier.Value = tier.Value.ToString(); cmd.Parameters.Add(pTier);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rank = 1;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var idStr = reader.GetString(0);
                if (Guid.TryParse(idStr, out var id))
                    result[id] = rank++;
            }
        }
        catch (Exception ex)
        {
            // FTS5 not initialized, malformed query, or transport failure:
            // return empty map. FuseByRrf will degrade cleanly to composite-
            // only ranking (all records get identical BM25 rank contribution).
            _log.LogDebug(ex, "BM25 fetch failed for tier={Tier}; falling back to composite-only", tier);
        }
        return result;
    }

    /// <summary>
    /// Issue #93 Phase 4 (2026-07-09) — Reciprocal Rank Fusion (Cormack et
    /// al. 2009) over composite rank and BM25 rank. Records receive
    /// <c>1/(k + composite_rank) + 1/(k + bm25_rank)</c>. Records missing
    /// from the BM25 rank map are treated as rank = <see cref="int.MaxValue"/>
    /// (contribution ≈ 0 from the BM25 side, but still competitive via
    /// composite alone).
    ///
    /// <para>The returned <see cref="ScoredMemory.CompositeScore"/> is
    /// REPLACED with the RRF score so downstream MMR / protected-slots /
    /// own-output ceiling paths continue to see a single ranking score.
    /// The original composite is preserved elsewhere on the object via
    /// the record's own fields.</para>
    /// </summary>
    internal static List<ScoredMemory> FuseByRrf(
        List<ScoredMemory>       scored,
        Dictionary<Guid, int>    bm25RankById,
        int                      topK,
        int                      k,
        out int                  bm25RescueCount)
    {
        bm25RescueCount = 0;
        if (scored.Count == 0) return scored;

        // Compute composite ranks (1-based) by descending composite score.
        var compositeRanked = scored
            .Select((s, i) => (s, index: i))
            .OrderByDescending(x => x.s.CompositeScore)
            .Select((x, rank) => (x.s, x.index, compositeRank: rank + 1))
            .ToList();

        // If BM25 is empty (index not built / query failed), fall back to
        // composite-only ordering unchanged.
        if (bm25RankById.Count == 0)
        {
            return compositeRanked
                .OrderBy(x => x.compositeRank)
                .Take(topK)
                .Select(x => x.s)
                .ToList();
        }

        // Fuse. RRF replaces CompositeScore with the fused score so
        // downstream rerank steps see it as the sort key.
        var fused = compositeRanked.Select(x =>
        {
            var bm25Rank = bm25RankById.TryGetValue(x.s.Record.Id, out var br) ? br : int.MaxValue;
            var compositeContribution = 1.0 / (k + x.compositeRank);
            var bm25Contribution      = bm25Rank == int.MaxValue ? 0.0 : 1.0 / (k + bm25Rank);
            var rrfScore = (float)(compositeContribution + bm25Contribution);
            return (fused: x.s with { CompositeScore = rrfScore }, x.compositeRank, bm25Rank);
        }).ToList();

        var final = fused.OrderByDescending(x => x.fused.CompositeScore).Take(topK).ToList();

        // Instrumentation — count how many records in the final top-K
        // would NOT have been in the composite-only top-K. These are
        // "BM25 rescues" — the entity-based confirmations we came for.
        var compositeTopKIds = new HashSet<Guid>(
            compositeRanked.OrderBy(x => x.compositeRank).Take(topK).Select(x => x.s.Record.Id));
        foreach (var (fusedRec, _, bm25Rank) in final)
        {
            if (!compositeTopKIds.Contains(fusedRec.Record.Id) && bm25Rank != int.MaxValue)
                bm25RescueCount++;
        }

        return final.Select(x => x.fused).ToList();
    }

    /// <summary>
    /// Strip FTS5 syntax characters from a natural-language query and
    /// rewrite it as an explicit OR expression so the underlying MATCH
    /// behaves as "any of these terms" rather than the default implicit
    /// AND ("all of these terms"). For long natural-language queries
    /// (e.g. a full Interior record), implicit AND matches near-zero
    /// records because no confirmation record contains every token.
    /// Explicit OR gets us classic BM25 semantics — records containing
    /// ANY query token are candidates, ranked by BM25 which weights
    /// rare tokens (like "WCTC") higher than common ones.
    ///
    /// <para>Also drops single-letter tokens and a small stopword list so
    /// the OR list isn't dominated by pronouns / articles / conjunctions
    /// that match everything. FTS5's porter+unicode61 tokenizer handles
    /// stemming (teach/teaching → same token) so we don't need to.</para>
    /// </summary>
    internal static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        var tokens = System.Text.RegularExpressions.Regex
            .Matches(query.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1 && !FtsStopwords.Contains(t))
            .Distinct()
            .Take(40)
            .ToList();

        if (tokens.Count == 0) return string.Empty;
        return string.Join(" OR ", tokens);
    }

    /// <summary>
    /// Small English stopword list used only by FTS5 query construction.
    /// Not a stemming / register decision — just a filter to keep the
    /// OR-expression from being dominated by tokens that match every
    /// record and add zero discrimination.
    /// </summary>
    private static readonly HashSet<string> FtsStopwords = new(StringComparer.Ordinal)
    {
        "a","an","the","is","are","was","were","be","been","being","and","or","but",
        "i","you","he","she","it","we","they","me","him","her","us","them",
        "my","your","his","their","our","this","that","these","those",
        "to","of","in","on","at","for","with","from","by","as","so",
        "if","then","not","no","yes","can","could","would","should","will","shall","may","might",
        "do","does","did","done","have","has","had","get","got","goes","went","make","made",
        "like","just","still","one","some","any","all","more","less","also","only",
        "about","out","up","down","over","under","into","through","because","while","when","where","how","what","why","who","whose",
    };

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
