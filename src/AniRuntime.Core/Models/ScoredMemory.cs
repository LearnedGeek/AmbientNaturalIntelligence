namespace AniRuntime.Core.Models;

/// <summary>
/// A memory record paired with its retrieval scores. Surfaces the cosine similarity
/// separately from the composite score so callers can apply confidence thresholds
/// on semantic relevance (AC1: retrieval confidence thresholding).
/// </summary>
public record ScoredMemory(MemoryRecord Record, float CompositeScore, float CosineSimilarity);
