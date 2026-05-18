using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Memory.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Memory;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — EF Core implementation of
/// <see cref="IMemoryLinkRebuilder"/>. Behaviour-preserved port of
/// <c>SqliteMemoryService.RebuildMemoryLinksAsync</c>.
///
/// Algorithm:
/// <list type="number">
///   <item>Load all memories with embeddings (ordered occurred_at DESC)
///     + the existing memory_links set into memory.</item>
///   <item>For each memory, compare against the next 50 (bounded scan)
///     — cosine ≥0.85 same-type → log as duplicate candidate; cosine in
///     [0.5, 0.85) → create up to 3 <c>relates_to</c> links per source.</item>
///   <item>Skip pairs already linked in either direction.</item>
///   <item>Insert links in batches with periodic progress logging.</item>
/// </list>
/// </summary>
public sealed class EfMemoryLinkRebuilder : IMemoryLinkRebuilder
{
    // Mirror of EfMemoryMergePolicy.MergeThreshold — kept local rather than
    // shared because the rebuilder's semantics differ (it doesn't actually
    // merge; it just LOGS dup pairs and creates relates_to below the floor).
    private const float MergeThreshold = 0.85f;
    private const float RelatedFloor   = 0.5f;
    private const int MaxLinksPerSource = 3;
    private const int BoundedScanWidth = 50;

    private readonly IDbContextFactory<AniDbContext> _dbFactory;
    private readonly ILogger<EfMemoryLinkRebuilder> _log;

    public EfMemoryLinkRebuilder(
        IDbContextFactory<AniDbContext> dbFactory,
        ILogger<EfMemoryLinkRebuilder> log)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _log       = log       ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<(int MergeCount, int LinkCount)> RebuildAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Rebuild: starting retroactive link building...");

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var allMemories = await db.Memories
            .Where(m => m.Embedding != null)
            .OrderByDescending(m => m.OccurredAt)
            .Select(m => new { m.Id, m.Type, m.Content, m.Embedding })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _log.LogInformation("Rebuild: loaded {Count} memories with embeddings", allMemories.Count);

        var existingLinks = (await db.MemoryLinks
            .Select(l => new { l.SourceId, l.TargetId })
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .Select(p => $"{p.SourceId}|{p.TargetId}")
            .ToHashSet();

        var linkCount = 0;
        var dupCount  = 0;
        var now       = DateTimeOffset.UtcNow;
        var pendingInserts = new List<MemoryLinkEntity>();

        for (var i = 0; i < allMemories.Count && !ct.IsCancellationRequested; i++)
        {
            var source = allMemories[i];
            if (source.Embedding is null) continue;

            var linksForThis = 0;
            for (var j = i + 1; j < Math.Min(i + BoundedScanWidth, allMemories.Count); j++)
            {
                var target = allMemories[j];
                if (target.Embedding is null) continue;
                if (source.Embedding.Length != target.Embedding.Length) continue;

                var similarity = VectorMath.CosineSimilarity(source.Embedding, target.Embedding);

                // ≥0.85 same-type — log as duplicate candidate (no merge).
                if (similarity >= MergeThreshold && source.Type == target.Type)
                {
                    dupCount++;
                    _log.LogDebug("Rebuild: duplicate detected (cosine={Sim:F3}): '{A}' ↔ '{B}'",
                        similarity,
                        source.Content[..Math.Min(40, source.Content.Length)],
                        target.Content[..Math.Min(40, target.Content.Length)]);
                }

                // [0.5, 0.85) — create up to 3 relates_to links per source.
                if (similarity is >= RelatedFloor and < MergeThreshold && linksForThis < MaxLinksPerSource)
                {
                    var linkKey       = $"{source.Id}|{target.Id}";
                    var reverseKey    = $"{target.Id}|{source.Id}";
                    if (existingLinks.Contains(linkKey) || existingLinks.Contains(reverseKey)) continue;

                    pendingInserts.Add(new MemoryLinkEntity
                    {
                        SourceId     = source.Id,
                        TargetId     = target.Id,
                        Relationship = "relates_to",
                        CreatedAt    = now,
                    });
                    existingLinks.Add(linkKey);
                    linkCount++;
                    linksForThis++;
                }
            }

            // Periodic batch-write to avoid an unbounded change set.
            if (pendingInserts.Count >= 200)
            {
                db.MemoryLinks.AddRange(pendingInserts);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                pendingInserts.Clear();
            }

            if (i > 0 && i % 100 == 0)
            {
                _log.LogInformation("Rebuild: processed {Count}/{Total} memories — {Links} links created, {Dups} duplicates found",
                    i, allMemories.Count, linkCount, dupCount);
            }
        }

        // Final batch.
        if (pendingInserts.Count > 0)
        {
            db.MemoryLinks.AddRange(pendingInserts);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _log.LogInformation("Rebuild complete: {Links} links created, {Dups} duplicates detected across {Total} memories",
            linkCount, dupCount, allMemories.Count);

        return (dupCount, linkCount);
    }
}
