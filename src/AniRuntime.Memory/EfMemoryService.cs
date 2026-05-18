using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 2 (2026-05-17) — EF Core + Unit of Work + Repository implementation
/// of <see cref="IMemoryService"/>. Replaces <see cref="SqliteMemoryService"/>
/// behind the <c>UseEfDataLayer</c> flag.
///
/// **Strangler-fig pattern.** This service owns the operations that have
/// been migrated to the repository layer (state blobs, simple persistence,
/// audit, type/tier reads, decay-eligible scans, compression). For the
/// still-complex operations — full semantic search composition (MMR rerank,
/// origin-quota backfill, own-output ceiling, link-enhanced retrieval),
/// Feature 30 dedup-merge, RebuildMemoryLinksAsync, contradiction
/// detection, IsRumination — it delegates to an injected
/// <see cref="SqliteMemoryService"/> instance. As more operations get
/// ported in follow-up phases, the legacy delegations shrink and
/// eventually <see cref="SqliteMemoryService"/> can be deleted (Phase 5
/// of the refactor plan).
///
/// **Unit of Work boundary.** Methods that compose multiple repository
/// operations use <see cref="IDbContextFactory{TContext}"/> to create one
/// context per operation, instantiate the repositories that share it, and
/// commit with a single <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
/// All-or-nothing transactional semantics replace the cross-method,
/// cross-connection composition that produced the Apr 21 reflection-gist
/// orphan-FK bug.
/// </summary>
public sealed class EfMemoryService : IMemoryService
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly SqliteMemoryService             _legacy;   // strangler-fig fallback
    private readonly IOllamaClient?                  _ollama;
    private readonly AniOptions                      _options;
    private readonly ILogger<EfMemoryService>        _log;

    public EfMemoryService(
        IDbContextFactory<AniDbContext> dbFactory,
        SqliteMemoryService             legacy,
        IOptions<AniOptions>            options,
        ILogger<EfMemoryService>        log,
        IOllamaClient?                  ollama = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _legacy    = legacy    ?? throw new ArgumentNullException(nameof(legacy));
        _options   = options.Value;
        _log       = log;
        _ollama    = ollama;
    }

    // ══════════════════════════════════════════════════════════════════
    // IMemoryPersistence
    // ══════════════════════════════════════════════════════════════════

    public async Task SaveAsync(MemoryRecord record, CancellationToken ct = default)
    {
        // Feature 30 three-tier dedup-merge is still owned by the legacy
        // service. Until it's ported (Phase 2.x follow-up), all SaveAsync
        // calls go through the legacy path so the production dedup
        // semantics are preserved. The legacy service still uses raw
        // SQLite, so this delegation is correct as long as
        // _options.MemoryDbPath points at the same file (which the DI
        // wiring guarantees).
        await _legacy.SaveAsync(record, ct).ConfigureAwait(false);
    }

    public async Task SaveCharacterStateAsync(CharacterStateDoc doc, CancellationToken ct = default)
    {
        doc.LastUpdated = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(doc);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new CharacterStateRepository(db);
        await repo.UpsertAsync(json, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveDesireStateAsync(DesireState state, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(state);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new DesireStateRepository(db);
        await repo.UpsertAsync(json, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveEmotionalStateAsync(EmotionalState state, CancellationToken ct = default)
    {
        state.LastUpdated = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(state);

        // Atomic dual-write: primary state blob + history append in one
        // SaveChangesAsync. Without atomicity, a crash between the two
        // writes leaves the dashboard showing a phantom jump (history is
        // missing one row that the state table reflects). EF wraps the
        // SaveChanges in one SQLite transaction.
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var blobRepo    = new EmotionalStateBlobRepository(db);
        var historyRepo = new EmotionalStateHistoryRepository(db);

        await blobRepo.UpsertAsync(json, ct).ConfigureAwait(false);

        historyRepo.Add(new EmotionalStateHistoryEntity
        {
            Warmth            = state.Warmth,
            Energy            = state.Energy,
            Concern           = state.Worry,
            Playfulness       = state.Playfulness,
            ContactGapTension = state.ContactGapTension,
            RecordedAt        = state.LastUpdated,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveEmotionalContributionAsync(EmotionalContribution contribution, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new EmotionalContributionRepository(db);

        var existing = await repo.GetByIdAsync(contribution.Id, ct).ConfigureAwait(false);
        var entity = existing ?? new EmotionalContributionEntity { Id = contribution.Id };

        entity.SourceContent     = contribution.SourceContent;
        entity.WarmthDelta       = contribution.WarmthDelta;
        entity.EnergyDelta       = contribution.EnergyDelta;
        entity.ConcernDelta      = contribution.WorryDelta;
        entity.PlayfulnessDelta  = contribution.PlayfulnessDelta;
        entity.CreatedAt         = contribution.CreatedAt;
        entity.HalfLifeHours     = contribution.HalfLifeHours;
        entity.Category          = contribution.Category.ToString();
        entity.Severity          = contribution.Severity;
        entity.IsOutreachReady   = contribution.IsOutreachReady;
        entity.Register          = contribution.Register;
        entity.Embedding         = contribution.Embedding;
        entity.MLEmotion         = contribution.MLEmotion;
        entity.MLConfidence      = contribution.MLConfidence;
        entity.MLSarcasm         = contribution.MLSarcasmDetected;
        entity.DivergenceScore   = contribution.DivergenceScore;
        entity.AssociativeAnchor = contribution.AssociativeAnchor;

        if (existing == null) repo.Add(entity);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveRelationshipHealthAsync(RelationshipHealth health, CancellationToken ct = default)
    {
        health.LastCalculated = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(health);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new RelationshipHealthRepository(db);
        await repo.UpsertAsync(json, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AdjustImportanceAsync(Guid id, float delta, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Memories.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
        if (entity == null)
        {
            _log.LogDebug("AdjustImportanceAsync: memory {Id} not found", id);
            return;
        }

        entity.Importance = Math.Clamp(entity.Importance + delta, 0f, 1f);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task AnchorMemoryAsync(Guid id, string reason, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Memories.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
        if (entity == null)
        {
            _log.LogWarning("AnchorMemoryAsync: memory {Id} not found", id);
            return;
        }

        entity.Tier         = DecayTier.Anchored;
        entity.AnchorReason = reason;
        entity.AnchoredAt   = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Delegate to legacy — it writes an audit-log entry on delete via
        // AuditAsync, and that helper still lives in SqliteMemoryService.
        // Porting it (and the create/update audit hooks across SaveAsync)
        // is a Phase 2.x follow-up.
        await _legacy.DeleteAsync(id, ct).ConfigureAwait(false);
    }

    public async Task SaveConfabulationFlagAsync(
        string contactMessage,
        string aniReply,
        string? topicCategory = null,
        string? notes = null,
        string? canonicalCategory = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new ConfabulationFlagRepository(db);
        repo.Add(new ConfabulationFlagEntity
        {
            Id                = Guid.NewGuid(),
            ContactMessage    = contactMessage,
            AniReply          = aniReply,
            TopicCategory     = topicCategory,
            Notes             = notes,
            CanonicalCategory = canonicalCategory,
            FlaggedAt         = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IEnumerable<MemoryRecord>> GetRecentAsync(int limit = 10, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryRepository(db);
        var entities = await repo.GetRecentAsync(limit, ct).ConfigureAwait(false);
        return entities.Select(MapToRecord);
    }

    public async Task<IEnumerable<MemoryRecord>> GetDecayEligibleAsync(int limit = 10, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryRepository(db);

        // Pre-filter via repository on the cheap predicates (tier, source,
        // importance), then apply the type-aware recency decay in C#. The
        // decay multiplier is small per-record work and not worth pushing
        // into SQL.
        var overFetch = Math.Max(limit * 5, 50);
        var candidates = await repo.GetCompressionCandidatesAsync(
            _options.DecayEligibilityImportanceThreshold, overFetch, ct).ConfigureAwait(false);

        var threshold = _options.DecayEligibilityRecencyThreshold;
        var now = DateTimeOffset.UtcNow;

        var eligible = candidates
            .Where(e => RecencyScore(e, now) < threshold)
            .Take(limit)
            .Select(MapToRecord)
            .ToList();

        return eligible;
    }

    public async Task MarkCompressedAsync(IEnumerable<Guid> sourceIds, Guid gistId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var memoryRepo = new MemoryRepository(db);
        var linkRepo   = new MemoryLinkRepository(db);

        var marked = await memoryRepo.MarkCompressedAsync(sourceIds, ct).ConfigureAwait(false);

        // Provenance: gist → each successfully-compressed source.
        if (marked > 0)
        {
            var sourceList = sourceIds.ToList();
            var compressedIds = await db.Memories
                .Where(m => sourceList.Contains(m.Id) && m.Tier == DecayTier.Compressed)
                .Select(m => m.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            foreach (var sourceId in compressedIds)
            {
                linkRepo.Add(new MemoryLinkEntity
                {
                    SourceId     = gistId,
                    TargetId     = sourceId,
                    Relationship = "compressed_into",
                    CreatedAt    = now,
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════════════
    // IMemorySearch
    // ══════════════════════════════════════════════════════════════════

    public Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false)
    {
        // Full composite scoring (MMR, origin-quota backfill, own-output
        // ceiling, link-enhanced retrieval) lives in the legacy service.
        // Ported in a follow-up phase once the simpler reads have soaked.
        return _legacy.SearchAsync(query, topK, ct, enforceOriginQuota);
    }

    public Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(
        string query, int topK = 10, CancellationToken ct = default)
        => _legacy.SearchWithScoresAsync(query, topK, ct);

    public Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(
        string query, MemoryType type, int topK = 5, CancellationToken ct = default)
        => _legacy.SearchByTypeAsync(query, type, topK, ct);

    public async Task<IEnumerable<MemoryRecord>> GetByTypeAsync(
        MemoryType type, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryRepository(db);
        var entities = await repo.GetByTypeAsync(type, limit, ct).ConfigureAwait(false);
        return entities.Select(MapToRecord);
    }

    public async Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryRepository(db);
        var entities = await repo.GetAnchoredAsync(ct).ConfigureAwait(false);
        return entities.Select(MapToRecord);
    }

    public Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default)
        => _legacy.GetLinkedMemoriesAsync(memoryId, relationshipType, ct);

    public Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f)
        => _legacy.SearchByTierAsync(query, tier, topK, ct, minCosine);

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
        return entities.Select(MapToRecord);
    }

    // ══════════════════════════════════════════════════════════════════
    // IStateStore
    // ══════════════════════════════════════════════════════════════════

    public async Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await new CharacterStateRepository(db).GetAsync(ct).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.Json)) return new CharacterStateDoc();
        return JsonSerializer.Deserialize<CharacterStateDoc>(entity.Json, JsonDefaults.CaseInsensitive)
               ?? new CharacterStateDoc();
    }

    public async Task<DesireState> GetDesireStateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await new DesireStateRepository(db).GetAsync(ct).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.Json)) return new DesireState();
        return JsonSerializer.Deserialize<DesireState>(entity.Json) ?? new DesireState();
    }

    public async Task<EmotionalState> GetEmotionalStateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await new EmotionalStateBlobRepository(db).GetAsync(ct).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.Json)) return new EmotionalState();
        return JsonSerializer.Deserialize<EmotionalState>(entity.Json) ?? new EmotionalState();
    }

    public async Task<RelationshipHealth> GetRelationshipHealthAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await new RelationshipHealthRepository(db).GetAsync(ct).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.Json)) return new RelationshipHealth();
        return JsonSerializer.Deserialize<RelationshipHealth>(entity.Json) ?? new RelationshipHealth();
    }

    public async Task<List<EmotionalStateSnapshot>> GetEmotionalHistoryAsync(int hours, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new EmotionalStateHistoryRepository(db);
        var entities = await repo.GetRecentAsync(hours, ct).ConfigureAwait(false);
        // Repository returns DESC; spec returns ASC for charting.
        return entities
            .OrderBy(h => h.RecordedAt)
            .Select(h => new EmotionalStateSnapshot(
                h.Warmth, h.Energy, h.Concern, h.Playfulness,
                h.ContactGapTension, h.RecordedAt))
            .ToList();
    }

    // ══════════════════════════════════════════════════════════════════
    // IMemoryAnalytics
    // ══════════════════════════════════════════════════════════════════

    public Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default)
        => _legacy.GetOpenLoopsAsync(ct);

    public async Task<List<MemoryContradiction>> GetFlaggedContradictionsAsync(
        bool includeResolved = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryContradictionRepository(db);
        var entities = await repo.GetUnresolvedAsync(ct).ConfigureAwait(false);

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

    public Task<int> GetRecentMessageCountAsync(int days, CancellationToken ct = default)
        => _legacy.GetRecentMessageCountAsync(days, ct);

    public Task<float> GetAverageConversationValenceAsync(int days, CancellationToken ct = default)
        => _legacy.GetAverageConversationValenceAsync(days, ct);

    public Task<(int outreach, int inbound)> GetInitiativeBalanceAsync(int days, CancellationToken ct = default)
        => _legacy.GetInitiativeBalanceAsync(days, ct);

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

    public Task<List<string>> GetProcessedThemesAsync(int maxThemes = 5, CancellationToken ct = default)
        => _legacy.GetProcessedThemesAsync(maxThemes, ct);

    // ══════════════════════════════════════════════════════════════════
    // IMemoryMaintenance
    // ══════════════════════════════════════════════════════════════════

    public async Task ResolveOpenLoopAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Memories.FirstOrDefaultAsync(m => m.Id == id, ct).ConfigureAwait(false);
        if (entity == null) return;

        entity.IsResolved = true;
        entity.ResolvedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ResolveContradictionAsync(
        Guid newMemoryId, Guid existingMemoryId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.MemoryContradictions
            .FirstOrDefaultAsync(c => c.NewMemoryId == newMemoryId
                                   && c.ExistingMemoryId == existingMemoryId, ct)
            .ConfigureAwait(false);
        if (entity == null) return;

        entity.IsResolved = true;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CleanupDecayedContributionsAsync(CancellationToken ct = default)
    {
        // Remove anything older than 30 days — matches the legacy cleanup
        // window and the EmotionalContributionRepository.GetActiveAsync
        // lookback so anything outside the active window is safe to drop.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new EmotionalContributionRepository(db);
        await repo.DeleteOlderThanAsync(cutoff, ct).ConfigureAwait(false);
    }

    public async Task ExpireContributionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.EmotionalContributions.FirstOrDefaultAsync(c => c.Id == id, ct)
            .ConfigureAwait(false);
        if (entity == null) return;

        db.EmotionalContributions.Remove(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<(int MergeCount, int LinkCount)> RebuildMemoryLinksAsync(CancellationToken ct = default)
        => _legacy.RebuildMemoryLinksAsync(ct);

    public async Task<int> GetLinkCountAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.MemoryLinks.CountAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryLink>> GetAllLinksAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.MemoryLinks.ToListAsync(ct).ConfigureAwait(false);
        return entities.Select(e => new MemoryLink
        {
            SourceId     = e.SourceId,
            TargetId     = e.TargetId,
            Relationship = e.Relationship,
            CreatedAt    = e.CreatedAt,
        }).ToList();
    }

    public async Task<List<AuditEntry>> GetRecentAuditEntriesAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var repo = new MemoryAuditRepository(db);
        var entities = await repo.GetRecentAsync(limit, ct).ConfigureAwait(false);
        return entities.Select(e => new AuditEntry
        {
            Id               = e.Id,
            MemoryId         = e.MemoryId.ToString(),
            Action           = e.Action,
            Source           = e.Source,
            ContentBefore    = e.ContentBefore,
            ContentAfter     = e.ContentAfter,
            TypeBefore       = e.TypeBefore,
            TypeAfter        = e.TypeAfter,
            ImportanceBefore = e.ImportanceBefore,
            ImportanceAfter  = e.ImportanceAfter,
            OccurredAt       = e.OccurredAt,
        }).ToList();
    }

    public Task<bool> RestoreFromAuditAsync(long auditId, CancellationToken ct = default)
        => _legacy.RestoreFromAuditAsync(auditId, ct);

    // ══════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════

    private static MemoryRecord MapToRecord(MemoryEntity e) => new()
    {
        Id                = e.Id,
        Type              = e.Type,
        Content           = e.Content,
        RawJson           = e.RawJson,
        Importance        = e.Importance,
        RelationalValence = e.RelationalValence,
        Embedding         = e.Embedding,
        IsResolved        = e.IsResolved,
        SourceName        = e.SourceName,
        OccurredAt        = e.OccurredAt,
        CreatedAt         = e.CreatedAt,
        ResolvedAt        = e.ResolvedAt,
        DecayTier         = e.Tier,
        AnchorReason      = e.AnchorReason,
        AnchoredAt        = e.AnchoredAt,
        Provenance        = e.Provenance,
    };

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
    };

    /// <summary>
    /// Type-aware recency multiplier matching SqliteMemoryService.GetDecayMultiplier.
    /// Kept in sync via the GetDecayMultiplier-test invariants — if either drifts the
    /// retrieval-eligibility predicate diverges from the production scoring.
    /// </summary>
    private float RecencyScore(MemoryEntity entity, DateTimeOffset now)
    {
        if (entity.Tier == DecayTier.Anchored) return 1.0f;

        var hoursSince = (now - entity.OccurredAt).TotalHours;
        var multiplier = entity.Type switch
        {
            MemoryType.Episodic     => 2.0f,
            MemoryType.Semantic     => 2.0f,
            MemoryType.Commitment   => 2.0f,
            MemoryType.OpenLoop     => 1.5f,
            MemoryType.InnerThought => 1.0f,
            MemoryType.Perception   => 0.5f,
            _                       => 1.0f,
        };
        var lambda = _options.RetrievalRecencyDecayHours * multiplier;
        return (float)Math.Exp(-hoursSince / lambda);
    }
}
