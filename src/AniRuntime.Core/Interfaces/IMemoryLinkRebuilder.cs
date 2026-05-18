namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Phase 5 SOLID refactor (2026-05-18) — focused contract for Feature 37
/// retroactive memory-link building + duplicate detection. Extracted from
/// <c>SqliteMemoryService.RebuildMemoryLinksAsync</c> so the maintenance
/// surface (resolve loops, audit reads, cleanup) doesn't carry the
/// O(N²) embedding-scan logic.
///
/// One operation: scan all memories with embeddings, create
/// <c>relates_to</c> links for cosine 0.5–0.85 pairs (bounded to top-3
/// per source), log ≥0.85 same-type pairs as duplicate candidates.
/// Heavy — runs on-demand via the admin <c>///rebuild-links</c> command,
/// not in the cycle hot path.
/// </summary>
public interface IMemoryLinkRebuilder
{
    /// <summary>
    /// Scan the memory graph, create related-but-not-duplicate links,
    /// and report the duplicate-pair count + link-creation count.
    /// Returns <c>(MergeCount, LinkCount)</c> where MergeCount is the
    /// number of ≥0.85 same-type pairs detected (for awareness logging)
    /// and LinkCount is the number of new <c>relates_to</c> links created.
    /// </summary>
    Task<(int MergeCount, int LinkCount)> RebuildAsync(CancellationToken ct = default);
}
