using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Builds the retrieval portion of <see cref="ContextSnapshot"/>: recent memory
/// (episodic + inner-thought concat), anchored foundation memories, perception-
/// driven semantic search with diversity re-ranking, and similar-thought
/// loop-detection scratchpad.
///
/// Extracted 2026-05-19 as the second sub-builder of the ContextBuilder SRP
/// decomposition (`ANI-Testability-Architecture-Plan.md` §2). Production impl:
/// <c>RetrievalContextBuilder</c> in <c>AniRuntime.Loops.Context</c>.
/// </summary>
public interface IRetrievalContextBuilder
{
    /// <summary>
    /// Build the retrieval-context block. Each retrieval is wrapped in
    /// try/catch and degrades independently to an empty list on failure;
    /// the diversity re-rank also fails open (returns the input order).
    /// </summary>
    /// <param name="perceptions">
    /// Active perceptions driving the semantic-search query. Empty perceptions
    /// → empty <c>RelevantMemory</c> (no fallback recent-memory injection at
    /// the retrieval layer).
    /// </param>
    Task<RetrievalContextResult> BuildAsync(
        IReadOnlyList<PerceptionEvent>  perceptions,
        CancellationToken               ct);
}
