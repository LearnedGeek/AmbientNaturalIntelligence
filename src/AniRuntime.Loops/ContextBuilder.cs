using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Assembles the ContextSnapshot used by all cognitive phases. Handles semantic search,
/// diversity re-ranking, relationship health, emotional drift detection, outreach
/// pattern analysis, and outreach continuity context.
/// </summary>
public class ContextBuilder
{
    private readonly IStateStore _state;
    private readonly IMemorySearch _search;
    private readonly IMemoryPersistence _persist;
    private readonly IMemoryAnalytics _analytics;
    private readonly IOllamaClient _ollama;
    private readonly DesireEngine _desire;
    private readonly IDiagnosticService _diagnostic;
    private readonly AniOptions _aniOptions;
    private readonly ILogger<ContextBuilder> _log;

    public ContextBuilder(
        IStateStore state,
        IMemorySearch search,
        IMemoryPersistence persist,
        IMemoryAnalytics analytics,
        IOllamaClient ollama,
        DesireEngine desire,
        IDiagnosticService diagnostic,
        IOptions<AniOptions> aniOptions,
        ILogger<ContextBuilder> log)
    {
        _state = state;
        _search = search;
        _persist = persist;
        _analytics = analytics;
        _ollama = ollama;
        _desire = desire;
        _diagnostic = diagnostic;
        _aniOptions = aniOptions.Value;
        _log = log;
    }

    public async Task<ContextSnapshot> BuildContextSnapshotAsync(
        List<PerceptionEvent> perceptions, CancellationToken ct,
        EmotionalState? emotionalState = null,
        bool conversationMode = false)
    {
        var charState    = await _state.GetCharacterStateAsync(ct).ConfigureAwait(false);
        var desireState  = await _desire.GetStateAsync(ct).ConfigureAwait(false);
        var recentEpisodic = await _search.GetByTypeAsync(MemoryType.Episodic, 10, ct).ConfigureAwait(false);
        var recentThoughts = await _search.GetByTypeAsync(MemoryType.InnerThought, 5, ct).ConfigureAwait(false);
        var recentMem    = recentEpisodic.Concat(recentThoughts).ToList();
        var openLoops    = await _analytics.GetOpenLoopsAsync(ct).ConfigureAwait(false);

        // Feature 16: Load anchored (foundation) memories — always present in context
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
        // to what Ani is currently experiencing — not just the most recent ones
        var relevantMem = new List<MemoryRecord>();
        if (perceptions.Count > 0)
        {
            var searchQuery = string.Join(". ", perceptions.Select(p => p.Summary));
            try
            {
                var results = await _search.SearchAsync(searchQuery, 5, ct).ConfigureAwait(false);
                relevantMem = results.ToList();
                _log.LogDebug("Semantic search returned {Count} relevant memories", relevantMem.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Semantic memory search failed — continuing without");
            }
        }

        // ─── Epistemic Grounding (Apr 10, 2026) ─────────────────────────────
        // Tier-scoped retrieval — populate Facts / Episodic / Interior pools.
        // The PromptBuilder will render each into its own prompt section.
        //
        // Design principle: each tier has a different retrieval role.
        //   - Facts pool: grounds factual assertions about Mark's world. Query
        //     by current perceptions to find relevant facts, fall back to
        //     recent facts if no perceptions. Anchored memories are always
        //     included as the foundation layer.
        //   - Episodic pool: conversation continuity. The recent verbatim
        //     conversation messages (populated during reply generation, not
        //     here — reply path injects them directly from the thread).
        //   - Interior pool: voice, mood, self-model. Retrieved by recent
        //     thoughts/reactions so the model's output matches its ongoing
        //     felt state.
        var groundedFacts = new List<MemoryRecord>();
        var interiorContext = new List<MemoryRecord>();
        try
        {
            if (!conversationMode)
            {
                if (perceptions.Count > 0)
                {
                    var searchQuery = string.Join(". ", perceptions.Select(p => p.Summary));

                    // Facts: tier-scoped semantic search for grounded claims
                    var factResults = await _search.SearchByTierAsync(
                        searchQuery, EpistemicTier.Facts, 5, ct).ConfigureAwait(false);
                    groundedFacts = factResults.Select(s => s.Record).ToList();

                    // Interior: tier-scoped semantic search for voice/mood continuity
                    var interiorResults = await _search.SearchByTierAsync(
                        searchQuery, EpistemicTier.Interior, 5, ct).ConfigureAwait(false);
                    interiorContext = interiorResults.Select(s => s.Record).ToList();
                }
                else
                {
                    // No perceptions — fall back to recent memories from each pool
                    groundedFacts = (await _search.GetByTierAsync(
                        EpistemicTier.Facts, 8, ct).ConfigureAwait(false)).ToList();
                    interiorContext = (await _search.GetByTierAsync(
                        EpistemicTier.Interior, 5, ct).ConfigureAwait(false)).ToList();
                }
            }

            // Anchored memories are always facts — ensure they're in the Facts pool
            // even when semantic search didn't surface them (or when skipped in
            // conversation mode). Anchored foundation facts are stable and never
            // produce echoes; they are retained in both modes. Dedup by id.
            var missingAnchors = anchoredMemories
                .Where(m => !groundedFacts.Any(g => g.Id == m.Id));
            groundedFacts.InsertRange(0, missingAnchors);

            _log.LogDebug(
                "Tier retrieval: {Facts} facts, {Interior} interior (from {Source})",
                groundedFacts.Count, interiorContext.Count,
                conversationMode
                    ? "conversation mode — anchored only"
                    : (perceptions.Count > 0 ? "perception query" : "recent fallback"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tier-scoped retrieval failed — continuing with empty pools");
        }

        // Extract recent conversation summary — the most important context for what's
        // happening in the contact's life right now. This feeds into inner thoughts,
        // outreach decisions, and outreach messages.
        var conversationSummary = recentEpisodic
            .Where(m => m.Content.StartsWith(MemoryPrefixes.ConversationSummary))
            .Select(m => m.Content)
            .FirstOrDefault();

        // Thought loop detection via semantic search — find recent inner thoughts that
        // are similar to the current context OR to each other. If similarity is high,
        // the model is stuck in a loop and needs stronger diversity signals.
        //
        // Two-pronged detection:
        // 1. Compare perceptions to recent thoughts (are we about to think about something we already covered?)
        // 2. Compare the most recent thought to older thoughts (are we stuck on the same theme?)
        // The second prong catches loops like "hazel eyes" where the theme doesn't appear
        // in perceptions but keeps recurring in inner thoughts.
        var similarThoughts = new List<MemoryRecord>();
        try
        {
            // Prong 1: perceptions vs thoughts
            if (perceptions.Count > 0)
            {
                var thoughtQuery = string.Join(". ", perceptions.Select(p => p.Summary));
                var results = await _search.SearchByTypeAsync(
                    thoughtQuery, MemoryType.InnerThought, 3, ct).ConfigureAwait(false);
                similarThoughts.AddRange(results);
            }

            // Prong 2: most recent thought vs older thoughts
            var lastThought = recentMem
                .Where(m => m.Type == MemoryType.InnerThought)
                .OrderByDescending(m => m.OccurredAt)
                .FirstOrDefault();
            if (lastThought is not null)
            {
                var results = await _search.SearchByTypeAsync(
                    lastThought.Content, MemoryType.InnerThought, 5, ct).ConfigureAwait(false);
                // Exclude the thought itself and dedup against prong 1
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
        // dissimilar from recent inner thoughts. This steers the model toward fresh topics
        // by changing what context it sees — not by telling it what to avoid.
        // Uses embeddings (not text injection) per design decision.
        relevantMem = await ReRankForDiversityAsync(
            relevantMem, recentThoughts.ToList(), ct).ConfigureAwait(false);

        emotionalState ??= await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);

        // Feature 27: Assemble recent outreach context for continuity awareness
        var outreachContext = BuildOutreachContext(recentMem, desireState, charState);

        // Feature 4: Load relationship health — updated at most once per day
        RelationshipHealth? relationshipHealth = null;
        try
        {
            relationshipHealth = await _state.GetRelationshipHealthAsync(ct).ConfigureAwait(false);

            // Recalculate if stale (>24h since last calculation)
            if ((DateTimeOffset.UtcNow - relationshipHealth.LastCalculated).TotalHours >= 24)
            {
                relationshipHealth = await ComputeRelationshipHealthAsync(
                    relationshipHealth, emotionalState, ct).ConfigureAwait(false);
                await _persist.SaveRelationshipHealthAsync(relationshipHealth, ct).ConfigureAwait(false);
                _log.LogInformation("Relationship health recalculated: score={Score:F2}, phase={Phase}",
                    relationshipHealth.ConnectionScore, relationshipHealth.Phase);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load/compute relationship health — continuing without");
        }

        // Feature 8: Emotional drift detection — compare recent vs older emotional vectors
        EmotionalDrift? emotionalDrift = null;
        try
        {
            // Use the history already fetched for health (or fetch if needed)
            var driftHistory = await _state.GetEmotionalHistoryAsync(48, ct).ConfigureAwait(false);
            if (driftHistory.Count >= 4)
            {
                var midpoint = driftHistory.Count / 2;
                var older = driftHistory.Take(midpoint).ToList();
                var recent = driftHistory.Skip(midpoint).ToList();
                emotionalDrift = EmotionalDrift.Compute(recent, older);
                if (emotionalDrift.IsSignificant)
                {
                    _log.LogInformation("Emotional drift detected: similarity={Sim:F3}, {Description}",
                        emotionalDrift.Similarity, emotionalDrift.Describe());
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Emotional drift detection failed — continuing without");
        }

        // Feature 12: Self-awareness feedback loop — analyze recent outreach for pattern clusters.
        // If Ani's outreach has been thematically repetitive, surface awareness in inner thought.
        string? patternAwareness = null;
        try
        {
            patternAwareness = await AnalyzeOutreachPatternsAsync(charState.Name, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Feature 12: Pattern analysis failed — continuing without");
        }

        // Processed themes — topics whose emotional contributions have fully decayed
        var processedThemes = await _analytics.GetProcessedThemesAsync(5, ct).ConfigureAwait(false);

        return new ContextSnapshot
        {
            CharacterState           = charState,
            DesireState              = desireState,
            EmotionalState           = emotionalState,
            RecentMemory             = recentMem.ToList(),
            RelevantMemory           = relevantMem,
            OpenLoops                = openLoops.ToList(),
            Perceptions              = perceptions,
            BuiltAt                  = DateTimeOffset.UtcNow,
            RecentConversationSummary = conversationSummary,
            SimilarRecentThoughts    = similarThoughts,
            OutreachContext          = outreachContext,
            AnchoredMemories        = anchoredMemories,
            RelationshipHealth       = relationshipHealth,
            EmotionalDrift           = emotionalDrift,
            PatternAwareness         = patternAwareness,
            ProcessedThemes          = processedThemes,
            ThoughtDiversityNudge    = BuildThoughtDiversityNudge(),
            // Epistemic Grounding (Apr 10, 2026): tier-partitioned pools
            GroundedFacts            = groundedFacts,
            InteriorContext          = interiorContext,
            // RecentExchanges is populated by the reply path from the active
            // conversation thread, not here — ambient cycles leave it empty.
        };
    }

    /// <summary>
    /// Feature 41: When PERCEPTION-ANCHOR is active, generate a gentle curiosity redirect.
    /// Frames the nudge as self-discovery, not rejection, to avoid negative emotional response.
    /// </summary>
    private string? BuildThoughtDiversityNudge()
    {
        var report = _diagnostic.LatestReport;
        if (report is null) return null;

        var anchor = report.Findings
            .FirstOrDefault(f => f.Code == "PERCEPTION-ANCHOR" &&
                                 f.Severity >= DiagnosticSeverity.Info);
        if (anchor is null) return null;

        return "You've been circling the same thought for a while. " +
               "Your mind has more in it than this. " +
               "What else has been sitting quietly that you haven't explored yet?";
    }

    /// <summary>
    /// Feature 27: Assembles outreach continuity context from recent episodic memory.
    /// Determines which outreach messages were answered by checking if any conversation
    /// or inbound contact occurred after each outreach record.
    /// </summary>
    internal RecentOutreachContext BuildOutreachContext(
        List<MemoryRecord> recentMemory, DesireState desireState, CharacterStateDoc charState)
    {
        var outreachPrefix = "I reached out to ";
        var outreachRecords = recentMemory
            .Where(m => m.Type == MemoryType.Episodic && m.Content.StartsWith(outreachPrefix))
            .OrderByDescending(m => m.OccurredAt)
            .Take(5)
            .ToList();

        // Conversation records indicate the contact replied at some point
        var conversationTimes = recentMemory
            .Where(m => m.Type == MemoryType.Episodic && m.Content.StartsWith("Conversation ("))
            .Select(m => m.OccurredAt)
            .ToList();

        var lastContactReply = desireState.LastContactInbound;

        var records = new List<OutreachRecord>();
        foreach (var outreach in outreachRecords)
        {
            // An outreach is "answered" if the contact replied after it was sent
            var wasAnswered = lastContactReply > outreach.OccurredAt ||
                              conversationTimes.Any(t => t > outreach.OccurredAt);

            // Extract message text: "I reached out to Mark: "message here""
            var colonIdx = outreach.Content.IndexOf(": \"", StringComparison.Ordinal);
            var msgText = colonIdx >= 0
                ? outreach.Content[(colonIdx + 3)..].TrimEnd('"')
                : outreach.Content[outreachPrefix.Length..];

            records.Add(new OutreachRecord
            {
                Message     = msgText.Trim(),
                SentAt      = outreach.OccurredAt,
                WasAnswered = wasAnswered,
            });
        }

        // Count consecutive unanswered from most recent
        var unanswered = 0;
        foreach (var r in records)
        {
            if (r.WasAnswered) break;
            unanswered++;
        }

        var timeSinceLastSend = records.Count > 0
            ? DateTimeOffset.UtcNow - records[0].SentAt
            : (TimeSpan?)null;

        var timeSinceReply = lastContactReply != default
            ? DateTimeOffset.UtcNow - lastContactReply
            : (TimeSpan?)null;

        return new RecentOutreachContext
        {
            RecentMessages             = records,
            UnansweredCount            = unanswered,
            TimeSinceLastSend          = timeSinceLastSend,
            TimeSinceLastContactReply  = timeSinceReply,
        };
    }

    /// <summary>
    /// Re-ranks candidate memories to prefer topics dissimilar from recent inner thoughts.
    /// Computes a "thought centroid" from recent thought embeddings, then scores each
    /// candidate by (1 - similarity_to_centroid). Higher novelty = ranked first.
    /// </summary>
    /// <summary>
    /// Re-ranks memory candidates by novelty relative to recent inner thoughts.
    /// Memories most dissimilar to the thought centroid rank highest (diversity over echo).
    ///
    /// CS6: Dual-consumer — called by both BuildThoughtContextAsync (inner thought diversity)
    /// and ConversationReplyPhase (conversation context diversity). Changes to this method
    /// affect BOTH paths. If inner thought and conversation reply need different re-ranking
    /// strategies in the future, split into two methods with separate tuning parameters.
    /// </summary>
    public async Task<List<MemoryRecord>> ReRankForDiversityAsync(
        List<MemoryRecord> candidates, List<MemoryRecord> recentThoughts, CancellationToken ct)
    {
        // Dedup by ID — multiple search paths (scored, link-enhanced, TF-IDF)
        // can return the same memory. Without this, identical entries appear
        // multiple times in the re-ranked results.
        candidates = candidates
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .ToList();

        // Dedup by content prefix — catches duplicate profile memories with different IDs
        // (e.g., 4 copies of "About Mark: Salted caramel cold brew" from merge failures).
        // Keep the highest-importance version of each near-duplicate.
        candidates = candidates
            .GroupBy(c => c.Content.Length >= 40 ? c.Content[..40] : c.Content)
            .Select(g => g.OrderByDescending(m => m.Importance).First())
            .ToList();

        if (candidates.Count <= 1 || recentThoughts.Count == 0)
            return candidates;

        try
        {
            // Build thought centroid from recent inner thought embeddings
            var thoughtEmbeddings = recentThoughts
                .Where(t => t.Embedding is { Length: > 0 })
                .Select(t => t.Embedding!)
                .ToList();

            // If no embeddings available on stored thoughts, embed the text on-the-fly
            if (thoughtEmbeddings.Count == 0)
            {
                var thoughtText = string.Join(". ",
                    recentThoughts.Select(t => t.Content.Length > 100 ? t.Content[..100] : t.Content));
                var embedding = await _ollama.EmbedAsync(thoughtText, ct).ConfigureAwait(false);
                thoughtEmbeddings.Add(embedding);
            }

            var centroid = ComputeCentroid(thoughtEmbeddings);

            // Ensure candidates have embeddings — use stored or generate on-the-fly
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

                var similarity = CosineSimilarity(centroid, candidateEmbed);
                var novelty = 1f - similarity;
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

    /// <summary>
    /// Feature 4: Computes relationship health from interaction metrics over a rolling window.
    /// </summary>
    internal async Task<RelationshipHealth> ComputeRelationshipHealthAsync(
        RelationshipHealth previous, EmotionalState current, CancellationToken ct)
    {
        var days = _aniOptions.RelationshipHealthWindowDays;

        // 1. Message frequency: conversations per day, normalized (0 = no conversations, 1 = 3+/day)
        var msgCount = await _analytics.GetRecentMessageCountAsync(days, ct).ConfigureAwait(false);
        var msgsPerDay = (double)msgCount / days;
        var frequencyScore = Math.Min(1.0, msgsPerDay / 3.0);

        // 2. Conversation quality: average valence (0.0-1.0, center at 0.5)
        var avgValence = await _analytics.GetAverageConversationValenceAsync(days, ct).ConfigureAwait(false);
        var qualityScore = Math.Clamp(avgValence, 0.0, 1.0);

        // 3. Warmth trend: average warmth from emotional state history
        var history = await _state.GetEmotionalHistoryAsync(days * 24, ct).ConfigureAwait(false);
        var warmthScore = history.Count > 0
            ? Math.Clamp(history.Average(h => h.Warmth), 0.0, 1.0)
            : 0.5;

        // 4. Initiative balance: penalize when one side dominates
        var (outreach, inbound) = await _analytics.GetInitiativeBalanceAsync(days, ct).ConfigureAwait(false);
        var total = outreach + inbound;
        var balanceScore = total > 0
            ? 1.0 - Math.Abs((double)(outreach - inbound) / total)  // 1.0 = perfectly balanced
            : 0.5;  // no data = neutral

        // Equal-weight composite
        var score = (frequencyScore + qualityScore + warmthScore + balanceScore) / 4.0;
        score = Math.Clamp(score, 0.0, 1.0);

        var phase = RelationshipHealth.DeterminePhase(score, previous.Phase);

        _log.LogDebug(
            "Relationship health inputs: freq={Freq:F2} ({MsgsPerDay:F1}/day), quality={Quality:F2}, warmth={Warmth:F2}, balance={Balance:F2} (out={Out}/in={In}) → score={Score:F2}, phase={Phase}",
            frequencyScore, msgsPerDay, qualityScore, warmthScore, balanceScore, outreach, inbound, score, phase);

        return new RelationshipHealth
        {
            ConnectionScore = score,
            Phase = phase,
        };
    }

    /// <summary>
    /// Feature 12: Self-awareness feedback loop. Analyzes recent outreach messages for
    /// thematic repetition.
    /// </summary>
    internal async Task<string?> AnalyzeOutreachPatternsAsync(string characterName, CancellationToken ct)
    {
        // Get recent outreach memories (episodic records where Ani reached out)
        var recentEpisodic = (await _search.GetByTypeAsync(MemoryType.Episodic, 20, ct)
            .ConfigureAwait(false)).ToList();

        var outreachPrefix = "I reached out to";
        var outreachRecords = recentEpisodic
            .Where(m => m.Content.StartsWith(outreachPrefix, StringComparison.OrdinalIgnoreCase))
            .Take(8) // last 8 outreach messages
            .ToList();

        if (outreachRecords.Count < 3)
            return null; // not enough data for meaningful pattern analysis

        // Compute pairwise cosine similarity using stored embeddings
        var withEmbeddings = outreachRecords
            .Where(m => m.Embedding is { Length: > 0 })
            .ToList();

        if (withEmbeddings.Count < 3)
            return null;

        float totalSimilarity = 0;
        int pairCount = 0;
        for (var i = 0; i < withEmbeddings.Count; i++)
        {
            for (var j = i + 1; j < withEmbeddings.Count; j++)
            {
                totalSimilarity += CosineSimilarity(withEmbeddings[i].Embedding!, withEmbeddings[j].Embedding!);
                pairCount++;
            }
        }

        var avgSimilarity = pairCount > 0 ? totalSimilarity / pairCount : 0f;

        _log.LogDebug("Feature 12: Outreach pattern similarity = {Similarity:F3} ({Count} messages, {Pairs} pairs)",
            avgSimilarity, withEmbeddings.Count, pairCount);

        // Threshold: 0.75+ average similarity = thematically repetitive
        if (avgSimilarity < 0.75f)
            return null;

        // Extract most common theme from recent outreach for the awareness prompt
        var recentTopics = outreachRecords.Take(3)
            .Select(m => m.Content.Replace(outreachPrefix, "").Trim().TrimStart(':').TrimStart().TrimStart('"').TrimEnd('"'))
            .ToList();

        return $"I notice my last few messages have been thematically similar — circling the same territory: " +
               $"\"{recentTopics[0][..Math.Min(40, recentTopics[0].Length)]}...\". " +
               "Maybe I should explore something different next time. A different corner of my mind.";
    }

    internal static float[] ComputeCentroid(List<float[]> embeddings)
    {
        var dim = embeddings[0].Length;
        var centroid = new float[dim];
        foreach (var emb in embeddings)
            for (var i = 0; i < dim; i++)
                centroid[i] += emb[i];
        var count = (float)embeddings.Count;
        for (var i = 0; i < dim; i++)
            centroid[i] /= count;
        return centroid;
    }

    // Feature 9: Delegate to shared SIMD-accelerated implementation
    internal static float CosineSimilarity(float[] a, float[] b)
        => VectorMath.CosineSimilarity(a, b);
}
