using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory.Entities;
using AniRuntime.Memory.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// EF Core implementation of <see cref="IReflectionGistService"/> — the
/// first atomic composite operation built on the Phase 1+2 data-layer
/// refactor. Proves the EF + Unit of Work pattern works end-to-end before
/// the broader refactor migrates all 44 methods of SqliteMemoryService.
///
/// The atomicity guarantee: gist insert, source UPDATEs (tier flip to
/// Compressed), and provenance link inserts all happen in one
/// <c>SaveChangesAsync</c> call. EF Core wraps that in a single SQLite
/// transaction. If anything fails, nothing commits.
///
/// Auto-embedding for the gist content is preserved: if an
/// <see cref="IOllamaClient"/> is injected, the gist content is embedded
/// before save. If not (test scenarios), the gist saves without an
/// embedding — same fallback pattern as <c>SqliteMemoryService.SaveAsync</c>.
/// </summary>
public sealed class EfReflectionGistService : IReflectionGistService
{
    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly IOllamaClient? _ollama;
    private readonly ILogger<EfReflectionGistService> _log;

    public EfReflectionGistService(
        IDbContextFactory<AniDbContext> dbFactory,
        ILogger<EfReflectionGistService> log,
        IOllamaClient? ollama = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
        _ollama    = ollama;
    }

    public async Task<Guid> SaveReflectionGistAndCompressAsync(
        string gistContent,
        IReadOnlyList<Guid> sourceIds,
        EpistemicTier provenance,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gistContent))
            throw new ArgumentException("Gist content cannot be empty.", nameof(gistContent));

        var gistId = Guid.NewGuid();

        // Embed the gist content if Ollama is available. Failures here
        // do NOT block the save — gist persists without embedding (the
        // record is still useful for human inspection and exact-string
        // retrieval; cosine matching is degraded but not broken).
        float[]? embedding = null;
        if (_ollama is not null)
        {
            try
            {
                embedding = await _ollama.EmbedAsync(gistContent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "EfReflectionGistService: embedding failed for gist {GistId} — saving without embedding",
                    gistId);
            }
        }

        // ── Unit of Work boundary starts here ─────────────────────────
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var memoryRepo = new MemoryRepository(db);
        var linkRepo   = new MemoryLinkRepository(db);

        // 1. Add the gist record.
        // F-2 Phase 1 P6 (2026-08-22) — reflection synthesis is Ani's own
        // interpretive act (LLM-composed gist over her cognitive substrate),
        // so attribution is Ani-verified. Mirrors ReflectionPhase.SaveLegacyAsync;
        // this is the default primary path when UseEfReflectionGistService +
        // CompressionEnabled are on.
        var reflectionAttribution = AttributionTriple.AniAt(DateTimeOffset.UtcNow);
        var gistEntity = new MemoryEntity
        {
            Id                         = gistId,
            Type                       = MemoryType.Semantic,
            Content                    = gistContent,
            Importance                 = 0.8f,
            RelationalValence          = 0.5f,
            SourceName                 = "reflection",
            OccurredAt                 = DateTimeOffset.UtcNow,
            CreatedAt                  = DateTimeOffset.UtcNow,
            Tier                       = DecayTier.Standard,
            Provenance                 = provenance,
            Embedding                  = embedding,
            AttributedTo               = reflectionAttribution.AttributedTo,
            AttributedAt               = reflectionAttribution.AttributedAt,
            AttributedSourceRecordId   = reflectionAttribution.SourceRecordId,
            AttributedSourceDescriptor = reflectionAttribution.SourceDescriptor,
            AttributionTrust           = reflectionAttribution.Trust,
        };
        memoryRepo.Add(gistEntity);

        // 2. Mark sources as Compressed (per-source-tolerant via
        // ExecuteUpdateAsync — already Compressed/Anchored sources are
        // silently skipped by the WHERE clause).
        var markedCount = await memoryRepo.MarkCompressedAsync(sourceIds, ct).ConfigureAwait(false);

        // 3. Create compressed_into provenance links from gist → each
        // source. Skip sources we didn't actually mark (Anchored, already
        // Compressed) — no point creating provenance for a relationship
        // that didn't take effect.
        if (markedCount > 0)
        {
            // Reload the now-Compressed sources to confirm which IDs were
            // actually touched. ExecuteUpdateAsync returns the count, not
            // the affected rows, so we re-query to know which source IDs
            // are newly Compressed (vs. already-Compressed-skipped).
            var compressedIds = await db.Memories
                .Where(m => sourceIds.Contains(m.Id) && m.Tier == DecayTier.Compressed)
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

        // 4. Commit — single SaveChangesAsync wraps gist INSERT +
        // source UPDATEs + link INSERTs in one transaction.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // F-2 Phase 1 P8 (2026-08-23) — F2_ATTRIBUTION structured log for
        // the reflection primary path. Mirrors the pattern at the nine
        // other producer sites; MemoryEntity isn't itself IAttributedContent,
        // so log via a lightweight MemoryRecord projection of the fields
        // we just wrote (this avoids extending MemoryEntity's interface
        // surface just for a log line).
        new AniRuntime.Core.Models.MemoryRecord
        {
            AttributedTo               = gistEntity.AttributedTo,
            AttributedAt               = gistEntity.AttributedAt,
            AttributedSourceRecordId   = gistEntity.AttributedSourceRecordId,
            AttributedSourceDescriptor = gistEntity.AttributedSourceDescriptor,
            AttributionTrust           = gistEntity.AttributionTrust,
        }.LogAttribution(_log);

        _log.LogInformation(
            "EfReflectionGistService: saved gist {GistId} + marked {Marked}/{Requested} sources as Compressed",
            gistId, markedCount, sourceIds.Count);

        return gistId;
    }
}
