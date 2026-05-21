using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Builds the state-pull portion of <see cref="ContextSnapshot"/>:
/// character state, desire state, emotional state (with override),
/// open loops, derived outreach-continuity context, and the Feature 41
/// thought-diversity nudge from the latest diagnostic report.
///
/// Extracted 2026-05-19 as the fifth and final sub-builder of the
/// ContextBuilder SRP decomposition (`ANI-Testability-Architecture-Plan.md` §2).
/// Production impl: <c>StateContextBuilder</c> in <c>AniRuntime.Loops.Context</c>.
/// </summary>
public interface IStateContextBuilder
{
    /// <summary>
    /// Build the state-context block. Loads character/desire/emotional state
    /// from their respective stores, the open-loops list from analytics, the
    /// thought-diversity nudge from the latest diagnostic report, and derives
    /// the outreach-continuity context from recent memory.
    /// </summary>
    /// <param name="recentMemory">
    /// Recent memory pool (typically from <see cref="IRetrievalContextBuilder"/>).
    /// Used to derive the outreach-continuity context (which outreach records
    /// were answered, time since last send, etc.).
    /// </param>
    /// <param name="emotionalStateOverride">
    /// When non-null, used as the result's <c>EmotionalState</c> instead of
    /// loading from the store. Matches the pre-extraction orchestrator
    /// behavior where the cognitive cycle could pass through a post-shift
    /// emotional state computed earlier in the cycle.
    /// </param>
    Task<StateContextResult> BuildAsync(
        IReadOnlyList<MemoryRecord>     recentMemory,
        EmotionalState?                 emotionalStateOverride,
        CancellationToken               ct);
}
