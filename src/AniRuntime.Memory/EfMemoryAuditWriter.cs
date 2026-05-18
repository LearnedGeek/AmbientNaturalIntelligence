using AniRuntime.Core.Interfaces;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — EF Core implementation of
/// <see cref="IMemoryAuditWriter"/>. Replaces the private
/// <c>SqliteMemoryService.AuditAsync</c> helper.
///
/// Single-responsibility: append one row to <c>memory_audit</c>. No
/// other side effects. Designed to be called from any service that
/// mutates memory state (persistence, maintenance, merge-policy).
///
/// Audit failures are caught + logged but never re-thrown — the audit
/// log is observational, not load-bearing for the primary operation.
/// A broken audit-write does NOT block a delete/restore/merge from
/// committing.
/// </summary>
public sealed class EfMemoryAuditWriter : IMemoryAuditWriter
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly ILogger<EfMemoryAuditWriter> _log;

    public EfMemoryAuditWriter(
        IDbContextFactory<AniDbContext> dbFactory,
        ILogger<EfMemoryAuditWriter> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task WriteAsync(
        Guid memoryId,
        string action,
        string source,
        string? contentBefore,
        string? contentAfter,
        int? typeBefore,
        int? typeAfter,
        float? importanceBefore,
        float? importanceAfter,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var repo = new MemoryAuditRepository(db);
            repo.Add(new MemoryAuditEntity
            {
                MemoryId          = memoryId,
                Action            = action,
                Source            = source,
                ContentBefore     = contentBefore,
                ContentAfter      = contentAfter,
                TypeBefore        = typeBefore,
                TypeAfter         = typeAfter,
                ImportanceBefore  = importanceBefore,
                ImportanceAfter   = importanceAfter,
                OccurredAt        = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit failure must never block the primary operation, but it must
            // also not be silent. The audit table is the rollback safety net
            // (128-memory loss incident predates its existence). A broken audit
            // with no log makes a future incident undetectable.
            _log.LogWarning(ex,
                "Audit write failed for memory {MemoryId} action {Action} source {Source}",
                memoryId, action, source);
        }
    }
}
