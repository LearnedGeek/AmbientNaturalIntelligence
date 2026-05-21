using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops.Context;

/// <summary>
/// Production implementation of <see cref="IEpistemicContextBuilder"/>.
/// Extracted from <c>ContextBuilder</c> 2026-05-19 as the fourth sub-builder
/// of the SRP decomposition (`ANI-Testability-Architecture-Plan.md` §2).
///
/// Owns the Apr 10, 2026 epistemic-grounding tier-scoped retrieval surface
/// previously inline in <c>ContextBuilder.BuildContextSnapshotAsync</c>.
///
/// Design principle: each tier has a different retrieval role.
///   - Facts pool: grounds factual assertions about Mark's world. Query
///     by current perceptions to find relevant facts, fall back to recent
///     facts if no perceptions. Anchored memories are always merged in
///     as the foundation layer.
///   - Episodic pool: conversation continuity. The recent verbatim
///     conversation messages (populated during reply generation, not
///     here — reply path injects them directly from the thread).
///   - Interior pool: voice, mood, self-model. Retrieved by recent
///     thoughts/reactions so the model's output matches its ongoing
///     felt state.
///
/// Apr 30, 2026 fix preserved exactly: Facts-tier retrieval runs in BOTH
/// modes (conversation and ambient); Interior tier remains gated on
/// !conversationMode. The bug being fixed: pre-Apr-30, tier-scoped
/// retrieval was bypassed in conversation mode, leaving the composition
/// prompt's WHAT IS TRUE section empty and producing the WCTC / teaching /
/// snow-tea-kids confabulation class.
///
/// Theme P P.4 (May 12, 2026): floor at MinCosineThresholdComposition so
/// substrate that is only weakly-related does not reach composition.
///
/// Behavior preservation: the entire tier-retrieval surface is wrapped in
/// a single try/catch matching the pre-extraction inline shape — any
/// failure degrades both pools to empty.
/// </summary>
public sealed class EpistemicContextBuilder : IEpistemicContextBuilder
{
    private readonly IMemorySearch                       _search;
    private readonly AniOptions                          _aniOptions;
    private readonly ILogger<EpistemicContextBuilder>    _log;

    public EpistemicContextBuilder(
        IMemorySearch                       search,
        IOptions<AniOptions>                aniOptions,
        ILogger<EpistemicContextBuilder>    log)
    {
        _search     = search;
        _aniOptions = aniOptions.Value;
        _log        = log;
    }

    public async Task<EpistemicContextResult> BuildAsync(
        IReadOnlyList<PerceptionEvent>  perceptions,
        IReadOnlyList<MemoryRecord>     anchoredMemories,
        bool                            conversationMode,
        CancellationToken               ct)
    {
        var groundedFacts   = new List<MemoryRecord>();
        var interiorContext = new List<MemoryRecord>();
        try
        {
            if (perceptions.Count > 0)
            {
                var searchQuery = string.Join(". ", perceptions.Select(p => p.Summary));

                // Facts: tier-scoped semantic search for grounded claims.
                // Always run — Apr 30 fix. Theme P P.4 composition floor.
                var factResults = await _search.SearchByTierAsync(
                    searchQuery, EpistemicTier.Facts, 5, ct,
                    minCosine: (float)_aniOptions.MinCosineThresholdComposition).ConfigureAwait(false);
                groundedFacts = factResults.Select(s => s.Record).ToList();

                if (!conversationMode)
                {
                    // Interior: skipped in conversation mode (stale mood drift).
                    var interiorResults = await _search.SearchByTierAsync(
                        searchQuery, EpistemicTier.Interior, 5, ct,
                        minCosine: (float)_aniOptions.MinCosineThresholdComposition).ConfigureAwait(false);
                    interiorContext = interiorResults.Select(s => s.Record).ToList();
                }
            }
            else
            {
                // No perceptions — fall back to recent memories.
                groundedFacts = (await _search.GetByTierAsync(
                    EpistemicTier.Facts, 8, ct).ConfigureAwait(false)).ToList();

                if (!conversationMode)
                {
                    interiorContext = (await _search.GetByTierAsync(
                        EpistemicTier.Interior, 5, ct).ConfigureAwait(false)).ToList();
                }
            }

            // Anchored memories are always facts — merge into Facts pool even
            // when semantic search didn't surface them. Stable, never produce
            // echoes; retained in both modes. Dedup by id.
            var missingAnchors = anchoredMemories
                .Where(m => !groundedFacts.Any(g => g.Id == m.Id));
            groundedFacts.InsertRange(0, missingAnchors);

            _log.LogDebug(
                "Tier retrieval: {Facts} facts, {Interior} interior (mode={Mode}, source={Source})",
                groundedFacts.Count, interiorContext.Count,
                conversationMode ? "conversation" : "ambient",
                perceptions.Count > 0 ? "perception query" : "recent fallback");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tier-scoped retrieval failed — continuing with empty pools");
        }

        return new EpistemicContextResult(groundedFacts, interiorContext);
    }
}
