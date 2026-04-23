namespace AniRuntime.Core.Models;

/// <summary>
/// A memory record paired with its retrieval scores. Surfaces the cosine similarity
/// separately from the composite score so callers can apply confidence thresholds
/// on semantic relevance (AC1: retrieval confidence thresholding).
///
/// Agentic Lens Layer 1 Phase 1a (Apr 2026): <see cref="OriginTier"/> is populated
/// at retrieval time so downstream consumers (cognitive-cycle instrumentation,
/// Phase 1c protected-slot logic, Phase 1d feedback-loop perception) can reason
/// about the origin composition of the retrieved pool without re-classifying.
/// Defaults to <see cref="RetrievalOrigin.Unknown"/> when a caller constructs a
/// ScoredMemory without explicit classification — e.g. legacy call sites, tests,
/// and fallback paths that predate Layer 1.
/// </summary>
public record ScoredMemory(MemoryRecord Record, float CompositeScore, float CosineSimilarity)
{
    public RetrievalOrigin OriginTier { get; init; } = RetrievalOrigin.Unknown;
}
