using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Semantic and type-based memory retrieval.
/// Split from IMemoryService (ISP) — search consumers don't need
/// state persistence or analytics methods.
/// </summary>
public interface IMemorySearch
{
    /// <summary>
    /// Semantic search with optional origin-quota enforcement (Agentic Lens Layer 1 Phase 1c,
    /// Apr 2026). When <paramref name="enforceOriginQuota"/> is true AND
    /// <see cref="AniOptions.RetrievalProtectedSlotsEnabled"/> is true, the returned pool
    /// is adjusted to reserve a minimum non-caregiver-origin share per
    /// <see cref="AniOptions.MinNonCaregiverRetrievalFraction"/>. Intended for the inner-thought
    /// cycle retrieval path; conversation-reply retrieval should leave this false so reply
    /// generation remains caregiver-weighted.
    /// </summary>
    Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false);
    Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(string query, MemoryType type, int topK = 5, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetByTypeAsync(MemoryType type, int limit = 50, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Feature 31: Returns all memories linked to the given memory (1-hop bidirectional).
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default);

    /// <summary>
    /// Epistemic Grounding (Apr 10, 2026): Tier-scoped semantic search. Returns only
    /// memories whose <see cref="MemoryRecord.Provenance"/> matches the requested tier.
    ///
    /// Use this when the caller needs epistemically-bounded retrieval:
    /// - <see cref="EpistemicTier.Facts"/> for grounding factual claims about Mark/world.
    ///   This is the only tier that should ground assertions about Mark's external life.
    /// - <see cref="EpistemicTier.Episodic"/> for conversation continuity — "what was said."
    ///   Never treated as factual grounding.
    /// - <see cref="EpistemicTier.Interior"/> for Ani's voice, mood, self-concept, and
    ///   reflective continuity. Full creative latitude, structurally isolated from facts.
    ///
    /// See docs/spec/design/ANI-Epistemic-Grounding-Architecture.md for the full design.
    /// </summary>
    Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default);

    /// <summary>
    /// Epistemic Grounding: Non-scored tier retrieval for callers that just need recent
    /// memories of a specific tier without semantic ranking. Useful for constructing
    /// prompt sections that want "the N most recent Interior memories" regardless of query.
    /// </summary>
    Task<IEnumerable<MemoryRecord>> GetByTierAsync(
        EpistemicTier tier, int limit = 20, CancellationToken ct = default);
}
