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
/// Phase 5 closeout (2026-05-18) — no longer delegates to
/// <c>SqliteMemoryService</c>. Dedup-merge runs via
/// <see cref="IMemoryMergePolicy"/>; audit-log writes via
/// <see cref="IMemoryAuditWriter"/>; the actual persistence runs native
/// EF Core through the Phase 1+2 repository layer.
/// </para>
/// </summary>
public sealed class EfMemoryPersistenceService : IMemoryPersistence
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly IMemoryAuditWriter _audit;
    private readonly IMemoryMergePolicy _mergePolicy;
    private readonly IOllamaClient? _ollama;
    private readonly AniOptions _options;
    private readonly ILogger<EfMemoryPersistenceService> _log;

    // Serializes concurrent saves so the merge-policy decision + insert/merge
    // sequence is effectively atomic at the service layer. Two concurrent
    // SaveAsync calls could otherwise both miss each other in the merge-candidate
    // search and insert duplicates. Save cadence is low; serialization is cheap.
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public EfMemoryPersistenceService(
        IDbContextFactory<AniDbContext> dbFactory,
        IMemoryAuditWriter audit,
        IMemoryMergePolicy mergePolicy,
        IOptions<AniOptions> options,
        ILogger<EfMemoryPersistenceService> log,
        IOllamaClient? ollama = null)
    {
        _dbFactory   = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _audit       = audit     ?? throw new ArgumentNullException(nameof(audit));
        _mergePolicy = mergePolicy ?? throw new ArgumentNullException(nameof(mergePolicy));
        _options     = options.Value;
        _log         = log       ?? throw new ArgumentNullException(nameof(log));
        _ollama      = ollama;
    }

    /// <summary>
    /// Persist a memory record with the full Feature 30 + Apr 21 rumination
    /// pipeline. Phase 5 SOLID port (2026-05-18) — no longer delegates to
    /// legacy; uses <see cref="IMemoryMergePolicy"/> for the merge decision
    /// and the cross-type profile correction, and EF Core for the
    /// insert/upsert + Feature 31 link creation.
    ///
    /// Pipeline (behaviour-preserved from the legacy SaveAsync):
    /// <list type="number">
    ///   <item>Auto-embed via <see cref="IOllamaClient"/> if no embedding
    ///     supplied. Failure is logged but doesn't block the save.</item>
    ///   <item>InnerThought-only rumination guard via the merge policy.</item>
    ///   <item>Three-tier dedup-merge for dedupable types (InnerThought,
    ///     Semantic, OpenLoop, Commitment): ≥0.95 skip, 0.85-0.95 LLM-merge
    ///     into existing record, &lt;0.85 fall through.</item>
    ///   <item>Cross-type profile correction for Perception/Episodic
    ///     contact-speaking records.</item>
    ///   <item>Upsert (preserving created_at on update) + audit-log entry.</item>
    ///   <item>Feature 31 link creation (top 3 cosine 0.5-0.85 neighbours).</item>
    /// </list>
    /// </summary>
    public async Task SaveAsync(MemoryRecord record, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Auto-embed if needed.
            if (record.Embedding is null && _ollama is not null && !string.IsNullOrWhiteSpace(record.Content))
            {
                try
                {
                    record.Embedding = await _ollama.EmbedAsync(record.Content, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to generate embedding for {Type} record — saving without", record.Type);
                }
            }

            // 2. Rumination guard — InnerThought only, only when option enabled.
            if (_options.RuminationGuardEnabled
                && record.Type == MemoryType.InnerThought
                && record.Embedding is not null)
            {
                if (await _mergePolicy.IsRuminationAsync(record, ct).ConfigureAwait(false))
                {
                    var preview = record.Content is null
                        ? "(null)"
                        : record.Content[..Math.Min(60, record.Content.Length)];
                    _log.LogInformation(
                        "Rumination guard: skipping InnerThought — ≥{Min} similar thoughts in last {Hours}h at similarity ≥{Threshold:F2}. Content: {Preview}",
                        _options.RuminationClusterMinSize,
                        _options.RuminationWindowHours,
                        _options.RuminationSimilarityThreshold,
                        preview);
                    return;
                }
            }

            // Issue #56 (2026-05-22) — exact-content dedup. Runs before the
            // cosine-based merge policy because cosine search only scans the last
            // 50 records of the same type, which can miss older seeds buried
            // behind newer writes. Exact match on (Type, Content, SourceName)
            // catches deterministically regardless of table size — empirically
            // this is the failure mode behind the 5-copy Mia character-seed
            // records in production. Conservative: only blocks exact-string
            // matches; near-duplicates still flow into the cosine merge tier.
            var exactDuplicateId = await _mergePolicy.FindExactContentDuplicateAsync(record, ct)
                .ConfigureAwait(false);
            if (exactDuplicateId is not null)
            {
                _log.LogInformation(
                    "Exact-content dedup: skipping {Type} insert (sourceName={Source}) — identical record already exists at {ExistingId}",
                    record.Type, record.SourceName ?? "(null)", exactDuplicateId.Value);
                return;
            }

            // 3. Feature 30 three-tier dedup-merge.
            if (record.Embedding is not null)
            {
                var candidate = await _mergePolicy.FindMergeCandidateAsync(record, ct).ConfigureAwait(false);
                if (candidate is not null)
                {
                    if (candidate.IsExactDuplicate)
                    {
                        _log.LogDebug("Semantic dedup: skipping {Type} — too similar to recent memory: {Content}",
                            record.Type, record.Content[..Math.Min(50, record.Content.Length)]);
                        return;
                    }

                    var merged = await _mergePolicy.MergeAsync(
                        candidate.ExistingId, candidate.ExistingContent, record.Content, ct)
                        .ConfigureAwait(false);

                    if (merged is not null)
                    {
                        // Create links for the surviving (merged) record using the
                        // existing record's id — the incoming record was never
                        // inserted, so using record.Id would FK-orphan the link.
                        var originalId = record.Id;
                        record.Id = candidate.ExistingId;
                        await CreateLinksAsync(record, ct).ConfigureAwait(false);
                        record.Id = originalId;
                        return;
                    }
                    // Merge failed — fall through to normal insert.
                }
            }

            // 4. Cross-type profile correction for contact-speaking records.
            var isContactSpeaking =
                record.Content.StartsWith("Mark said:",   StringComparison.OrdinalIgnoreCase) ||
                record.Content.StartsWith("Mark texted:", StringComparison.OrdinalIgnoreCase);

            if (record.Embedding is not null
                && record.Type is MemoryType.Perception or MemoryType.Episodic
                && isContactSpeaking)
            {
                await _mergePolicy.TryProfileCorrectionAsync(record, ct).ConfigureAwait(false);
            }

            // 5. Insert (or upsert if id collides) + audit.
            await InsertMemoryAsync(record, ct).ConfigureAwait(false);

            // 6. Feature 31 link creation.
            await CreateLinksAsync(record, ct).ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// EF Core upsert preserving <c>created_at</c> on the existing row.
    /// Writes a create or update audit entry depending on whether the
    /// id already existed. Behaviour-preserved from legacy
    /// <c>SqliteMemoryService.InsertMemoryAsync</c>.
    /// </summary>
    private async Task InsertMemoryAsync(MemoryRecord record, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.Memories.FirstOrDefaultAsync(m => m.Id == record.Id, ct)
            .ConfigureAwait(false);

        string? contentBefore    = null;
        int?    typeBefore       = null;
        float?  importanceBefore = null;
        bool    isUpdate         = existing is not null;

        if (existing is not null)
        {
            contentBefore    = existing.Content;
            typeBefore       = (int)existing.Type;
            importanceBefore = existing.Importance;

            existing.Type              = record.Type;
            existing.Content           = record.Content;
            existing.RawJson           = record.RawJson;
            existing.Importance        = record.Importance;
            existing.RelationalValence = record.RelationalValence;
            existing.Embedding         = record.Embedding;
            existing.IsResolved        = record.IsResolved;
            existing.SourceName        = record.SourceName;
            existing.OccurredAt        = record.OccurredAt;
            // created_at intentionally preserved — birth-stamp shouldn't move.
            existing.ResolvedAt        = record.ResolvedAt;
            existing.Tier              = record.DecayTier;
            existing.AnchorReason      = record.AnchorReason;
            existing.AnchoredAt        = record.AnchoredAt;
            existing.Provenance        = record.Provenance;
            existing.ConfirmedAt       = record.ConfirmedAt;
            existing.ConfirmedBy       = record.ConfirmedBy;
            existing.Register          = record.Register;
            // F-2 Phase 1 P2 (2026-08-21) — attribution
            existing.AttributedTo               = record.AttributedTo;
            existing.AttributedAt               = record.AttributedAt;
            existing.AttributedSourceRecordId   = record.AttributedSourceRecordId;
            existing.AttributedSourceDescriptor = record.AttributedSourceDescriptor;
            existing.AttributionTrust           = record.AttributionTrust;
        }
        else
        {
            db.Memories.Add(new MemoryEntity
            {
                Id                = record.Id,
                Type              = record.Type,
                Content           = record.Content,
                RawJson           = record.RawJson,
                Importance        = record.Importance,
                RelationalValence = record.RelationalValence,
                Embedding         = record.Embedding,
                IsResolved        = record.IsResolved,
                SourceName        = record.SourceName,
                OccurredAt        = record.OccurredAt,
                CreatedAt         = record.CreatedAt,
                ResolvedAt        = record.ResolvedAt,
                Tier              = record.DecayTier,
                AnchorReason      = record.AnchorReason,
                AnchoredAt        = record.AnchoredAt,
                Provenance        = record.Provenance,
                ConfirmedAt       = record.ConfirmedAt,
                ConfirmedBy       = record.ConfirmedBy,
                Register          = record.Register,
                // F-2 Phase 1 P2 (2026-08-21) — attribution
                AttributedTo               = record.AttributedTo,
                AttributedAt               = record.AttributedAt,
                AttributedSourceRecordId   = record.AttributedSourceRecordId,
                AttributedSourceDescriptor = record.AttributedSourceDescriptor,
                AttributionTrust           = record.AttributionTrust,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.WriteAsync(
            memoryId:         record.Id,
            action:           isUpdate ? "update" : "create",
            source:           record.SourceName ?? "cognitive-cycle",
            contentBefore:    contentBefore,
            contentAfter:     record.Content,
            typeBefore:       typeBefore,
            typeAfter:        (int)record.Type,
            importanceBefore: importanceBefore,
            importanceAfter:  record.Importance,
            ct: ct).ConfigureAwait(false);

        _log.LogDebug("Saved {Type} memory: {Content}",
            record.Type, record.Content[..Math.Min(50, record.Content.Length)]);
    }

    /// <summary>
    /// Feature 31 — create up to 3 <c>relates_to</c> links from this record
    /// to recent memories with cosine in the [0.5, 0.85) band (related but
    /// not merge-duplicates). Failure is non-blocking — link errors don't
    /// roll back the save.
    /// </summary>
    private async Task CreateLinksAsync(MemoryRecord record, CancellationToken ct)
    {
        if (record.Embedding is null) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var recent = await db.Memories
                .Where(m => m.Id != record.Id && m.Embedding != null)
                .OrderByDescending(m => m.OccurredAt)
                .Take(20)
                .Select(m => new { m.Id, m.Embedding })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var candidates = new List<(Guid TargetId, float Similarity)>();
            foreach (var row in recent)
            {
                if (row.Embedding is null || row.Embedding.Length != record.Embedding.Length) continue;
                var similarity = VectorMath.CosineSimilarity(record.Embedding, row.Embedding);
                if (similarity is >= 0.5f and < 0.85f)
                    candidates.Add((row.Id, similarity));
            }

            if (candidates.Count == 0) return;

            var now = DateTimeOffset.UtcNow;
            var top = candidates.OrderByDescending(l => l.Similarity).Take(3).ToList();

            foreach (var (targetId, _) in top)
            {
                // INSERT OR IGNORE semantics: composite PK collision = no-op.
                var exists = await db.MemoryLinks.AnyAsync(
                    l => l.SourceId == record.Id && l.TargetId == targetId && l.Relationship == "relates_to", ct)
                    .ConfigureAwait(false);
                if (exists) continue;

                db.MemoryLinks.Add(new MemoryLinkEntity
                {
                    SourceId     = record.Id,
                    TargetId     = targetId,
                    Relationship = "relates_to",
                    CreatedAt    = now,
                });
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            _log.LogDebug("Memory links: created {Count} links for {Id}", top.Count, record.Id);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Memory link creation failed — continuing without links");
        }
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
        entity.SubstrateJson     = contribution.SubstrateJson;

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

    public async Task<int> MarkConversationTurnInvalidAsync(
        string aniReplyContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(aniReplyContent)) return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Match the most-recent Episodic conversation record whose content contains
        // the Ani reply. Conversation records are stored with a speaker prefix
        // ("I said to Mark: \"...\"" / "Ani said: \"...\""), so the raw Ani reply
        // is a substring of the stored content. Restrict to Episodic + the
        // conversation source so we don't accidentally invalidate a Semantic
        // backstory fact that happens to share text.
        var candidates = await db.Memories
            .Where(m => m.Type == MemoryType.Episodic
                     && m.SourceName == "conversation"
                     && m.Validity == "valid"
                     && m.Content.Contains(aniReplyContent))
            .OrderByDescending(m => m.OccurredAt)
            .Take(5)
            .ToListAsync(ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            _log.LogInformation(
                "Walk-back tag: no matching valid Episodic conversation record found for ani reply (first 60 chars): {Preview}",
                aniReplyContent[..Math.Min(60, aniReplyContent.Length)]);
            return 0;
        }

        var updated = 0;
        foreach (var entity in candidates)
        {
            entity.Validity = "invalid_confabulation";
            updated++;

            await _audit.WriteAsync(
                memoryId:         entity.Id,
                action:           "walk-back-invalidate",
                source:           "TagCommand",
                contentBefore:    entity.Content,
                contentAfter:     entity.Content,
                typeBefore:       (int)entity.Type,
                typeAfter:        (int)entity.Type,
                importanceBefore: entity.Importance,
                importanceAfter:  entity.Importance,
                ct: ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.LogInformation(
            "Walk-back tag: invalidated {Count} conversation record(s) for ani reply (first 60 chars): {Preview}",
            updated, aniReplyContent[..Math.Min(60, aniReplyContent.Length)]);

        return updated;
    }

    public async Task<int> MarkConversationTurnConfirmedAsync(
        string aniReplyContent, DateTimeOffset confirmedAt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(aniReplyContent)) return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Symmetric to MarkConversationTurnInvalidAsync — same match rule, same
        // OrderByDescending(OccurredAt).Take(5) shape. Restrict to Episodic +
        // conversation source so we don't over-confirm a Semantic backstory
        // record that happens to share text.
        var candidates = await db.Memories
            .Where(m => m.Type == MemoryType.Episodic
                     && m.SourceName == "conversation"
                     && m.Content.Contains(aniReplyContent))
            .OrderByDescending(m => m.OccurredAt)
            .Take(5)
            .ToListAsync(ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            _log.LogInformation(
                "Confirm tag: no matching Episodic conversation record found for ani reply (first 60 chars): {Preview}",
                aniReplyContent[..Math.Min(60, aniReplyContent.Length)]);
            return 0;
        }

        var updated = 0;
        foreach (var entity in candidates)
        {
            entity.ConfirmedAt = confirmedAt;
            entity.ConfirmedBy = "mark-tag";
            updated++;

            await _audit.WriteAsync(
                memoryId:         entity.Id,
                action:           "confirm-mark-tag",
                source:           "TagCommand",
                contentBefore:    entity.Content,
                contentAfter:     entity.Content,
                typeBefore:       (int)entity.Type,
                typeAfter:        (int)entity.Type,
                importanceBefore: entity.Importance,
                importanceAfter:  entity.Importance,
                ct: ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.LogInformation(
            "Confirm tag: confirmed {Count} conversation record(s) for ani reply (first 60 chars): {Preview}",
            updated, aniReplyContent[..Math.Min(60, aniReplyContent.Length)]);

        return updated;
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
