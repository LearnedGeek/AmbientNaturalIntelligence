using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Agentic Lens Layer 1 Phase 1d (Apr 2026): rolling-window tracker of per-cycle
/// retrieval origin distributions. Populated by ContextBuilder at the end of
/// each cognitive-cycle context build; read by the
/// <c>RetrievalSelfDominancePerceptionSource</c> to detect feedback-loop
/// conditions (own-output share dominating the retrieval pool over recent
/// cycles).
///
/// In-memory only. State does not survive process restart. Singleton lifetime
/// — single writer (ContextBuilder on the cognitive-cycle thread), single
/// reader (perception source on the same thread), but implementations should
/// use a lock on write/read to be defensive against future multi-thread use.
/// </summary>
public interface IRetrievalOriginTracker
{
    /// <summary>
    /// Record the origin distribution from a single cognitive cycle's retrieval
    /// pool. Adds the distribution to the rolling buffer; oldest entry is
    /// evicted when the buffer is full.
    /// </summary>
    void RecordCycle(IReadOnlyDictionary<RetrievalOrigin, int> distribution);

    /// <summary>
    /// Aggregate origin counts across the most recent <paramref name="cycleWindow"/>
    /// cycles. If fewer than <paramref name="cycleWindow"/> cycles have been
    /// recorded, aggregates across all available cycles.
    /// </summary>
    IReadOnlyDictionary<RetrievalOrigin, int> GetRecentDistribution(int cycleWindow = 10);

    /// <summary>
    /// How many cycles have been recorded so far, capped by the internal buffer size.
    /// Used by the perception source to decide whether there is enough signal to
    /// emit (e.g. require at least 3 recorded cycles before evaluating dominance).
    /// </summary>
    int RecordedCycleCount { get; }
}
