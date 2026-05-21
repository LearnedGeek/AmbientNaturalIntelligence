namespace AniRuntime.Core.Models;

/// <summary>
/// Output of <c>IEpistemicContextBuilder.BuildAsync</c>. Carries the two
/// tier-scoped retrieval pools that <c>ContextBuilder</c> previously assembled
/// inline. Extracted 2026-05-19 as the fourth sub-builder of the
/// ContextBuilder SRP decomposition (Testability Architecture Plan §2).
/// </summary>
/// <remarks>
/// Both lists are non-null. Empty <c>GroundedFacts</c> when no perceptions
/// drove a query and the recent-Facts fallback also returned nothing.
/// Empty <c>InteriorContext</c> is the expected steady-state in conversation
/// mode (Interior tier is intentionally gated off during inbound conversation
/// composition to avoid stale-mood-drift pollution).
/// </remarks>
public sealed record EpistemicContextResult(
    List<MemoryRecord>  GroundedFacts,
    List<MemoryRecord>  InteriorContext);
