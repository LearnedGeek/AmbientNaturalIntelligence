using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Builds the epistemic-tier portion of <see cref="ContextSnapshot"/>:
/// Facts pool (grounds factual assertions about the contact's world) and
/// Interior pool (voice/mood/self-model continuity). Tier-scoped semantic
/// search with composition-floor minimum cosine; falls back to recent-by-tier
/// when no perceptions drive the query.
///
/// Extracted 2026-05-19 as the fourth sub-builder of the ContextBuilder SRP
/// decomposition (`ANI-Testability-Architecture-Plan.md` §2). Production impl:
/// <c>EpistemicContextBuilder</c> in <c>AniRuntime.Loops.Context</c>.
/// </summary>
public interface IEpistemicContextBuilder
{
    /// <summary>
    /// Build the epistemic-context block.
    /// </summary>
    /// <param name="perceptions">
    /// Active perceptions drive the tier-scoped semantic search query. When
    /// empty, the implementation falls back to recent-by-tier reads.
    /// </param>
    /// <param name="anchoredMemories">
    /// Always-present foundation memories from
    /// <see cref="IRetrievalContextBuilder"/>. Merged into the Facts pool
    /// even when semantic search didn't surface them — anchored memories are
    /// stable and never produce echoes, so they're retained in both modes.
    /// </param>
    /// <param name="conversationMode">
    /// When <c>true</c>, the Interior tier is intentionally skipped — inner-
    /// thought and mood-reflection content would pollute conversation
    /// composition with stale mood drift. Facts tier runs in both modes
    /// (Apr 30, 2026 fix — the upstream cure for the WCTC / profession /
    /// teaching-schedule confabulation class).
    /// </param>
    Task<EpistemicContextResult> BuildAsync(
        IReadOnlyList<PerceptionEvent>  perceptions,
        IReadOnlyList<MemoryRecord>     anchoredMemories,
        bool                            conversationMode,
        CancellationToken               ct);
}
