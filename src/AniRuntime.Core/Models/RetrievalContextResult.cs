namespace AniRuntime.Core.Models;

/// <summary>
/// Output of <c>IRetrievalContextBuilder.BuildAsync</c>. Carries the four
/// retrieval-context fields that <c>ContextBuilder</c> previously assembled
/// inline. Extracted 2026-05-19 as the second sub-builder of the
/// ContextBuilder SRP decomposition (Testability Architecture Plan §2).
/// </summary>
/// <remarks>
/// <para>
/// <c>RecentMemory</c> is the concatenation of recent episodic + inner-thought
/// memories. Downstream consumers filter by <see cref="MemoryRecord.Type"/>
/// when they need just one slice. <c>RelevantMemory</c> is post-diversity-rerank.
/// </para>
/// <para>
/// All lists are non-null; empty when retrieval failed (each retrieval block
/// is independently try/caught and degrades to empty on failure, matching the
/// pre-extraction inline shape).
/// </para>
/// </remarks>
public sealed record RetrievalContextResult(
    List<MemoryRecord>  RecentMemory,
    List<MemoryRecord>  AnchoredMemories,
    List<MemoryRecord>  RelevantMemory,
    List<MemoryRecord>  SimilarRecentThoughts);
