using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Context;

/// <summary>
/// Production implementation of <see cref="IRetrievalContextBuilder"/>.
/// Extracted from <c>ContextBuilder</c> 2026-05-19 as the second sub-builder
/// of the SRP decomposition (`ANI-Testability-Architecture-Plan.md` §2).
///
/// Owns the retrieval surface previously inline in
/// <c>ContextBuilder.BuildContextSnapshotAsync</c>:
/// recent memory (episodic + inner-thought concat),
/// anchored foundation memories,
/// perception-driven semantic search,
/// diversity re-ranking (Feature — topic-novelty bias against recent thoughts),
/// similar-thought loop-detection (two-pronged: perceptions-vs-thoughts and
/// most-recent-thought-vs-history).
///
/// Behavior preservation: each retrieval block degrades independently to an
/// empty list on failure, matching the pre-extraction inline shape exactly.
/// </summary>
public sealed class RetrievalContextBuilder : IRetrievalContextBuilder
{
    private readonly IMemorySearch                       _search;
    private readonly IOllamaClient                       _ollama;
    private readonly ILogger<RetrievalContextBuilder>    _log;

    public RetrievalContextBuilder(
        IMemorySearch                       search,
        IOllamaClient                       ollama,
        ILogger<RetrievalContextBuilder>    log)
    {
        _search = search;
        _ollama = ollama;
        _log    = log;
    }

    public async Task<RetrievalContextResult> BuildAsync(
        IReadOnlyList<PerceptionEvent>  perceptions,
        CancellationToken               ct)
    {
        var recentEpisodic = await _search
            .GetByTypeAsync(MemoryType.Episodic, 10, ct).ConfigureAwait(false);
        var recentThoughts = await _search
            .GetByTypeAsync(MemoryType.InnerThought, 5, ct).ConfigureAwait(false);
        var recentMem      = recentEpisodic.Concat(recentThoughts).ToList();

        // Feature 16: Load anchored (foundation) memories.
        var anchoredMemories = new List<MemoryRecord>();
        try
        {
            anchoredMemories = (await _search.GetAnchoredMemoriesAsync(ct).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load anchored memories — continuing without");
        }

        // Semantic search: use perceptions as the query to surface memories relevant
        // to what Ani is currently experiencing — not just the most recent ones.
        var relevantMem = new List<MemoryRecord>();
        if (perceptions.Count > 0)
        {
            var searchQuery = string.Join(". ", perceptions.Select(p => p.Summary));
            try
            {
                // Agentic Lens Layer 1 Phase 1c (Apr 2026): inner-thought retrieval
                // opts in to origin-quota enforcement. When RetrievalProtectedSlotsEnabled
                // is off in config this is a no-op. Conversation-reply callers leave
                // enforceOriginQuota=false so reply retrieval stays caregiver-weighted.
                var results = await _search.SearchAsync(
                    searchQuery, topK: 5, ct: ct, enforceOriginQuota: true)
                    .ConfigureAwait(false);
                relevantMem = results.ToList();
                _log.LogDebug("Semantic search returned {Count} relevant memories", relevantMem.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Semantic memory search failed — continuing without");
            }
        }

        // Thought loop detection via semantic search — find recent inner thoughts that
        // are similar to the current context OR to each other. Two-pronged detection:
        //  1. Compare perceptions to recent thoughts (about to repeat?).
        //  2. Compare the most recent thought to older thoughts (stuck on a theme?).
        var similarThoughts = new List<MemoryRecord>();
        try
        {
            if (perceptions.Count > 0)
            {
                var thoughtQuery = string.Join(". ", perceptions.Select(p => p.Summary));
                var results = await _search.SearchByTypeAsync(
                    thoughtQuery, MemoryType.InnerThought, 3, ct).ConfigureAwait(false);
                similarThoughts.AddRange(results);
            }

            var lastThought = recentMem
                .Where(m => m.Type == MemoryType.InnerThought)
                .OrderByDescending(m => m.OccurredAt)
                .FirstOrDefault();
            if (lastThought is not null)
            {
                var results = await _search.SearchByTypeAsync(
                    lastThought.Content, MemoryType.InnerThought, 5, ct).ConfigureAwait(false);
                var existingIds = similarThoughts.Select(m => m.Id).ToHashSet();
                existingIds.Add(lastThought.Id);
                similarThoughts.AddRange(results.Where(r => !existingIds.Contains(r.Id)));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Thought similarity search failed — continuing without");
        }

        // Topic-weighted diversity: rerank relevant memories to prefer topics that are
        // dissimilar from recent inner thoughts. Uses embeddings (not text injection).
        relevantMem = await ReRankForDiversityAsync(
            relevantMem, recentThoughts.ToList(), ct).ConfigureAwait(false);

        return new RetrievalContextResult(
            RecentMemory:           recentMem,
            AnchoredMemories:       anchoredMemories,
            RelevantMemory:         relevantMem,
            SimilarRecentThoughts:  similarThoughts);
    }

    /// <summary>
    /// Re-ranks memory candidates by novelty relative to recent inner thoughts.
    /// Memories most dissimilar to the thought centroid rank highest (diversity over echo).
    /// Falls open (returns original order) on any failure inside the loop.
    /// </summary>
    public async Task<List<MemoryRecord>> ReRankForDiversityAsync(
        List<MemoryRecord> candidates, List<MemoryRecord> recentThoughts, CancellationToken ct)
    {
        // Dedup by ID — multiple search paths (scored, link-enhanced, TF-IDF)
        // can return the same memory.
        candidates = candidates
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();

        // Dedup by content prefix — catches duplicate profile memories with different IDs.
        candidates = candidates
            .GroupBy(c => c.Content.Length >= 40 ? c.Content[..40] : c.Content)
            .Select(g => g.OrderByDescending(m => m.Importance).First())
            .ToList();

        if (candidates.Count <= 1 || recentThoughts.Count == 0)
            return candidates;

        try
        {
            var thoughtEmbeddings = recentThoughts
                .Where(t => t.Embedding is { Length: > 0 })
                .Select(t => t.Embedding!)
                .ToList();

            if (thoughtEmbeddings.Count == 0)
            {
                var thoughtText = string.Join(". ",
                    recentThoughts.Select(t => t.Content.Length > 100 ? t.Content[..100] : t.Content));
                var embedding = await _ollama.EmbedAsync(thoughtText, ct).ConfigureAwait(false);
                thoughtEmbeddings.Add(embedding);
            }

            var centroid = ComputeCentroid(thoughtEmbeddings);

            var scored = new List<(MemoryRecord record, float novelty)>();
            foreach (var candidate in candidates)
            {
                float[] candidateEmbed;
                if (candidate.Embedding is { Length: > 0 })
                {
                    candidateEmbed = candidate.Embedding;
                }
                else
                {
                    candidateEmbed = await _ollama.EmbedAsync(candidate.Content, ct).ConfigureAwait(false);
                }

                var similarity = VectorMath.CosineSimilarity(centroid, candidateEmbed);
                var novelty    = 1f - similarity;
                scored.Add((candidate, novelty));
            }

            var reranked = scored.OrderByDescending(s => s.novelty).Select(s => s.record).ToList();

            _log.LogDebug("Diversity re-rank: {Scores}",
                string.Join(", ", scored.OrderByDescending(s => s.novelty)
                    .Select(s => $"{s.record.Content[..Math.Min(30, s.record.Content.Length)]}={s.novelty:F2}")));

            return reranked;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Diversity re-ranking failed — returning original order");
            return candidates;
        }
    }

    private static float[] ComputeCentroid(List<float[]> embeddings)
    {
        var dim      = embeddings[0].Length;
        var centroid = new float[dim];
        foreach (var emb in embeddings)
            for (var i = 0; i < dim; i++)
                centroid[i] += emb[i];
        var count = (float)embeddings.Count;
        for (var i = 0; i < dim; i++)
            centroid[i] /= count;
        return centroid;
    }
}
