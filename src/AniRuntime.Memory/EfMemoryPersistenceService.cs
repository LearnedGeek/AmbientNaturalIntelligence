using System.Text.Json;
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
/// Phase 5 SOLID refactor (2026-05-18) — focused EF Core implementation
/// of <see cref="IMemoryPersistence"/>. Owns writes for memory records,
/// state blobs (with atomic dual-write of emotional state + history),
/// emotional contributions, confabulation flags, importance adjustments,
/// anchor promotion, and decay-eligibility scans + Compression marking
/// (via the Phase 1+2 repository + UoW pattern).
///
/// <para>
/// <see cref="SaveAsync"/> still delegates to <see cref="SqliteMemoryService"/>
/// because the Feature 30 three-tier dedup-merge (cosine 0.95 skip /
/// 0.85-0.95 LLM-mediated merge / sub-0.85 insert) is a complex domain
/// behaviour that belongs in its own service when extracted. Pending
/// review with Mark before extraction so the merge policy isn't dragged
/// into the persistence surface monolithically.
/// </para>
/// <para>
/// <see cref="DeleteAsync"/> also delegates because it writes an audit
/// log entry as a side effect via the legacy <c>AuditAsync</c> helper.
/// Audit-writing belongs in a dedicated <c>MemoryAuditWriter</c> service
/// when extracted.
/// </para>
/// </summary>
public sealed class EfMemoryPersistenceService : IMemoryPersistence
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly SqliteMemoryService _legacy;
    private readonly IMemoryAuditWriter _audit;
    private readonly AniOptions _options;
    private readonly ILogger<EfMemoryPersistenceService> _log;

    public EfMemoryPersistenceService(
        IDbContextFactory<AniDbContext> dbFactory,
        SqliteMemoryService legacy,
        IMemoryAuditWriter audit,
        IOptions<AniOptions> options,
        ILogger<EfMemoryPersistenceService> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _legacy    = legacy    ?? throw new ArgumentNullException(nameof(legacy));
        _audit     = audit     ?? throw new ArgumentNullException(nameof(audit));
        _options   = options.Value;
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Pending Phase 5 extraction — Feature 30 three-tier dedup-merge
    /// (exact-duplicate skip / similarity-window merge / insert)
    /// belongs in a <c>MemoryMergePolicy</c> domain service that this
    /// persistence service depends on. For now, delegated to legacy.
    /// </summary>
    public Task SaveAsync(MemoryRecord record, CancellationToken ct = default)
        => _legacy.SaveAsync(record, ct);

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
        // SaveChangesAsync. EF wraps the SaveChanges in one SQLite transaction
        // so a crash between writes can't leave history out of sync with the
        // primary state row.
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

    /// <summary>
    /// Hard-delete a memory + its links, with an audit-log entry capturing
    /// the pre-delete content snapshot for rollback. Used by Feature 41
    /// diagnostic auto-correction to remove InnerThought records driving
    /// retrieval loops — never call for Episodic / conversation data.
    ///
    /// Phase 5 SOLID port (2026-05-18): now uses <see cref="IMemoryAuditWriter"/>
    /// instead of delegating to the legacy AuditAsync helper. Three steps
    /// run sequentially: (1) snapshot content+type+importance for audit,
    /// (2) bulk-delete memory_links referencing this id, (3) delete the
    /// memory row + write the audit entry. The audit-write happens via the
    /// dedicated writer service so a future port that adds transactional
    /// composition (single SaveChanges across delete + audit) only touches
    /// this method.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Snapshot for audit BEFORE deletion.
        var snapshot = await db.Memories
            .Where(m => m.Id == id)
            .Select(m => new { m.Content, Type = (int)m.Type, m.Importance })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Bulk-delete memory_links pointing into or out of this id. Done via
        // ExecuteDelete so it's a single round-trip, no entity tracking.
        await db.MemoryLinks
            .Where(l => l.SourceId == id || l.TargetId == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // Delete the memory row.
        var rows = await db.Memories
            .Where(m => m.Id == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (rows > 0 && snapshot is not null)
        {
            await _audit.WriteAsync(
                memoryId:         id,
                action:           "delete",
                source:           "manual",
                contentBefore:    snapshot.Content,
                contentAfter:     null,
                typeBefore:       snapshot.Type,
                typeAfter:        null,
                importanceBefore: snapshot.Importance,
                importanceAfter:  null,
                ct: ct).ConfigureAwait(false);
            _log.LogInformation("Deleted memory \"{Id}\"", id);
        }
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
        return entities.Select(EfMemoryMappings.MapToRecord);
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
            .Where(e => EfMemoryMappings.RecencyScore(e, now, _options) < threshold)
            .Take(limit)
            .Select(EfMemoryMappings.MapToRecord)
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
}
