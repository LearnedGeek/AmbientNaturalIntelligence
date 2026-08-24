using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Phase 3 (2026-05-17, data-layer refactor) — atomic operation for
/// saving a Phase 6 v1.2 reflection gist and marking its source records
/// as <see cref="DecayTier.Compressed"/> in ONE transaction.
///
/// Replaces the broken composition of
/// <c>IMemoryPersistence.SaveAsync(gist)</c> +
/// <c>IMemoryPersistence.MarkCompressedAsync(sourceIds, gistId)</c> that
/// silently failed when Feature 30 dedup merged the new gist into an
/// existing record (orphaned gistId → FK constraint failure → transaction
/// rollback → no sources marked).
///
/// Implementation uses EF Core's <c>DbContext.SaveChangesAsync</c> as
/// the natural Unit of Work boundary. Both the gist insert AND the
/// source UPDATEs commit together or roll back together.
///
/// See <c>docs/spec/ANI-Data-Layer-UoW-Repository-Refactor-Plan.md</c>
/// §Phase 3 and <c>docs/research/ANI-Research-Log.md</c> 2026-05-17
/// afternoon entry (architectural-debt discovery).
/// </summary>
public interface IReflectionGistService
{
    /// <summary>
    /// Saves a new reflection gist record and atomically marks the source
    /// records as Compressed. Creates <c>compressed_into</c> provenance
    /// links from gist → each source. All operations happen in one
    /// transaction; failure anywhere rolls back the whole batch.
    ///
    /// <para>
    /// <b>F-3 U5 (2026-08-24) — signature takes full source records
    /// rather than just IDs.</b> The implementation extracts the IDs it
    /// needs for the compression marking + provenance-link writes; the
    /// full records let it build per-source attribution claims that flow
    /// through the composer emission envelope alongside the gist. Empty
    /// or null source list is legal (some reflection cycles compress
    /// nothing) — the gist still saves without claims and without
    /// compression marking.
    /// </para>
    /// </summary>
    /// <param name="gistContent">The synthesized gist content (typically the
    /// <c>"[topic: X] Y"</c> shape produced by ReflectionPhase's Qwen call).</param>
    /// <param name="sources">The source records being compressed into this
    /// gist. Their IDs will be marked <see cref="DecayTier.Compressed"/> and
    /// their attribution snapshots feed the gist's per-source claim envelope
    /// (F-3 Option B). Pass the full records rather than IDs so per-source
    /// attribution is available at the wrap site.</param>
    /// <param name="provenance">EpistemicTier for the new gist. Defaults to
    /// Interior for reflection-source content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Id of the saved gist record.</returns>
    Task<Guid> SaveReflectionGistAndCompressAsync(
        string gistContent,
        IReadOnlyList<MemoryRecord> sources,
        EpistemicTier provenance,
        CancellationToken ct = default);
}
