using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused EF Core implementation
/// of <see cref="IMemoryMaintenance"/>. Owns the open-loop resolver,
/// contradiction resolver, contribution cleanup, link counters, and
/// audit-log reads.
///
/// Two methods remain delegated to <see cref="SqliteMemoryService"/>
/// pending Phase 5 risky-port review:
/// <see cref="RebuildMemoryLinksAsync"/> (Feature 37, O(N²) embedding
/// scan over the whole substrate — heavy, contains domain logic that
/// belongs in a separate service when extracted) and
/// <see cref="RestoreFromAuditAsync"/> (re-INSERT from soft-deleted
/// audit entry — has side effects on the audit log itself).
/// </summary>
public sealed class EfMemoryMaintenanceService : IMemoryMaintenance
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly SqliteMemoryService _legacy;
    private readonly IMemoryAuditWriter _audit;
    private readonly ILogger<EfMemoryMaintenanceService> _log;

    public EfMemoryMaintenanceService(
        IDbContextFactory<AniDbContext> dbFactory,
        SqliteMemoryService legacy,
        IMemoryAuditWriter audit,
        ILogger<EfMemoryMaintenanceService> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _legacy    = legacy    ?? throw new ArgumentNullException(nameof(legacy));
        _audit     = audit     ?? throw new ArgumentNullException(nameof(audit));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

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

    /// <summary>
    /// Feature 37: Retroactive memory link building + duplicate merging.
    /// O(N²) embedding scan + LLM-mediated merge for high-similarity
    /// clusters. Pending extraction into a dedicated
    /// <c>MemoryLinkRebuilder</c> domain service so the port doesn't
    /// drag the link/dedup logic into the maintenance surface.
    /// </summary>
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

    /// <summary>
    /// Re-INSERT a soft-deleted memory from its audit-log entry. The
    /// captured pre-delete content/type/importance becomes the restored
    /// memory's body; a new audit-log row records the restore so the
    /// roll-forward is itself audited.
    ///
    /// Returns false (with a warning logged) if the named audit entry
    /// either doesn't exist or isn't a delete-action entry (only deletes
    /// carry a usable contentBefore snapshot).
    ///
    /// Phase 5 SOLID port (2026-05-18): now uses <see cref="IMemoryAuditWriter"/>
    /// for the restore-side audit write instead of delegating to the legacy.
    /// </summary>
    public async Task<bool> RestoreFromAuditAsync(long auditId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var auditRepo = new MemoryAuditRepository(db);

        var entry = await auditRepo.GetByIdAsync(auditId, ct).ConfigureAwait(false);
        if (entry is null)
        {
            _log.LogWarning("Cannot restore audit entry {Id}: not found.", auditId);
            return false;
        }

        if (entry.Action != "delete" || entry.ContentBefore is null)
        {
            _log.LogWarning("Cannot restore audit entry {Id}: action={Action}, has content={HasContent}",
                auditId, entry.Action, entry.ContentBefore is not null);
            return false;
        }

        // Re-insert the deleted memory. INSERT OR IGNORE semantics —
        // if a row with this id already exists (e.g. someone manually
        // recreated it), the restore is a no-op and we return false.
        var memoryId = entry.MemoryId;
        var exists = await db.Memories.AnyAsync(m => m.Id == memoryId, ct).ConfigureAwait(false);
        if (exists)
        {
            _log.LogWarning("Cannot restore audit entry {Id}: memory {MemoryId} already exists.",
                auditId, memoryId);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        db.Memories.Add(new MemoryEntity
        {
            Id                = memoryId,
            Type              = (Core.Models.MemoryType)(entry.TypeBefore ?? 4),
            Content           = entry.ContentBefore!,
            Importance        = entry.ImportanceBefore ?? 0.3f,
            RelationalValence = 0.5f,
            OccurredAt        = now,
            CreatedAt         = now,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.WriteAsync(
            memoryId:         memoryId,
            action:           "create",
            source:           "restore",
            contentBefore:    null,
            contentAfter:     entry.ContentBefore,
            typeBefore:       null,
            typeAfter:        entry.TypeBefore,
            importanceBefore: null,
            importanceAfter:  entry.ImportanceBefore,
            ct: ct).ConfigureAwait(false);

        _log.LogInformation("Restored memory {Id} from audit entry {AuditId}", memoryId, auditId);
        return true;
    }
}
