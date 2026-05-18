using System.Text.Json;
using System.Text.RegularExpressions;
using AniRuntime.Core;
using AniRuntime.Core.Utilities;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Memory;

/// <summary>
/// SQLite-backed IMemoryService.
///
/// Storage strategy:
///   - memories        — all MemoryRecord rows (episodic, semantic, inner thoughts, etc.)
///   - character_state — single-row JSON document (CharacterStateDoc)
///   - desire_state    — single-row JSON document (DesireState)
///
/// Embeddings are stored as raw bytes (float[] serialised to little-endian binary).
/// Semantic search uses cosine similarity computed in C# — brute-force is correct and
/// faster than indexed approaches at our expected data volume (thousands of records).
///
/// WAL journal mode is enabled on first connect for concurrent read performance.
/// </summary>
public class SqliteMemoryService : IMemoryService, IDisposable
{
    private readonly string                          _connectionString;
    private readonly IOllamaClient?                  _ollama;
    private readonly AniOptions                      _options;
    private readonly ILogger<SqliteMemoryService>    _log;
    // Keeps in-memory databases alive for the lifetime of this service instance.
    // For file-based databases this is unused but harmless.
    private readonly SqliteConnection                _keepAlive;
    // Serializes SaveAsync. The service is registered singleton and has four
    // concurrent entry points (cognitive cycle, Twilio inbound, voice, dashboard),
    // but SaveAsync performs a non-atomic FindMergeCandidate → Merge|Insert →
    // CreateLinks sequence across multiple connections. Without this gate,
    // concurrent near-duplicate saves can both pass dedup (neither sees the
    // other's write yet), both insert, and produce duplicate records that the
    // merge path was supposed to prevent. The semaphore matches the singleton
    // shape — cheap, coarse, and correct at our save cadence.
    private readonly SemaphoreSlim                   _saveLock = new(1, 1);

    public SqliteMemoryService(
        IOptions<AniOptions> options,
        ILogger<SqliteMemoryService> log,
        IOllamaClient? ollama = null)
    {
        _log     = log;
        _ollama  = ollama;
        _options = options.Value;
        var dbPath = _options.MemoryDbPath;

        // Foreign Keys=True: Microsoft.Data.Sqlite disables FK enforcement by
        // default per-connection. Our schema declares FKs on memory_links;
        // without this flag they are inert, and the defensive work around
        // merge/delete is protecting against a constraint that isn't active.
        // Applied to the connection string so every connection (including
        // per-call opens and the keep-alive) honors the constraint.
        if (dbPath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || !dbPath.Contains(Path.DirectorySeparatorChar)
               && !dbPath.Contains('/') && !dbPath.Contains('\\') && !dbPath.Contains('.'))
        {
            // Named in-memory database (e.g. "ani-test-abc123").
            // Cache=Shared lets subsequent connections see the same database.
            // The keep-alive connection prevents the database from being dropped
            // between operations (in-memory databases live only while at least one
            // connection to them is open).
            _connectionString = $"Data Source={dbPath};Mode=Memory;Cache=Shared;Foreign Keys=True";
        }
        else
        {
            // Ensure the data directory exists for file-based databases
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={dbPath};Foreign Keys=True";
        }

        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        InitialiseSchema();
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        _saveLock.Dispose();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // Feature 30: Three-tier dedup thresholds (Mem0-inspired memory merging)
    // - Above ExactDuplicateThreshold: true duplicate, skip silently
    // - Between MergeThreshold and ExactDuplicateThreshold: merge via LLM
    // - Below MergeThreshold: insert as new record
    private const float ExactDuplicateThreshold = 0.95f;
    private const float MergeThreshold = 0.85f;
    private static readonly TimeSpan SemanticDedupWindow = TimeSpan.FromHours(4);

    // Memory types eligible for dedup/merge. Episodic events (conversations, outreach)
    // should never be deduped — each one is a distinct event even if content is similar.
    //
    // Perception records were previously eligible but are now exempt (April 2026,
    // Pipeline Audit Rec 3). Perception entries are first-class event receipts:
    // each inbound Twilio message, RSS item, or weather observation is a distinct
    // event even if content looks similar to a prior one. Same-type merging of
    // Perception records produced chimera records that then polluted tier-scoped
    // retrieval and triggered echoes in conversation. The correct consolidation
    // path for Perception content is cross-type correction (profile tier
    // synthesis), not same-type merge. See TryCrossTypeProfileCorrectionAsync.
    private static readonly HashSet<MemoryType> DedupableTypes = new()
    {
        MemoryType.InnerThought,
        MemoryType.Semantic,  // Feature 30: profile facts are prime merge candidates
    };

    // Merge quality gate: regex patterns for detecting confabulated specifics
    // in merged output that weren't present in either source.
    // NumberPattern / TimePattern / NamePattern regexes removed Apr 29, 2026
    // (Theme E #6) — only ContainsNovelSpecifics used them, and that gate
    // was removed with this batch. If a future feature needs these patterns,
    // restore from git history rather than re-deriving.

    public async Task SaveAsync(MemoryRecord record, CancellationToken ct = default)
    {
        // Serialize concurrent saves. The dedup/merge/insert sequence below is
        // not atomic at the SQL layer; two concurrent SaveAsync calls on the
        // singleton service could both miss each other in FindMergeCandidate
        // and insert duplicates. The semaphore makes the sequence effectively
        // atomic at the service layer. Save cadence is low enough that the
        // serialization cost is negligible.
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Auto-embed content if no embedding provided and Ollama is available
            if (record.Embedding is null && _ollama is not null && !string.IsNullOrWhiteSpace(record.Content))
            {
                try
                {
                    record.Embedding = await _ollama.EmbedAsync(record.Content, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to generate embedding for {Type} record — saving without", record.Type);
                }
            }

            // Apr 21, 2026 — Rumination guard (Option C, commit 2).
            // Before the existing Feature 30 dedup, check whether this InnerThought is
            // part of a rumination cluster — three or more semantically similar thoughts
            // within the last 2 hours at similarity ≥0.75. If so, skip the save.
            //
            // The existing Feature 30 dedup only catches ≥0.85 similarity. The Apr 21
            // cascade operated in the 0.60-0.85 range: near-duplicate variants that
            // were below merge threshold but still pool-saturating. This guard
            // specifically addresses that gap by detecting cluster saturation rather
            // than pairwise duplication.
            //
            // Scope: InnerThought only. Episodic events remain first-class (see
            // DedupableTypes comment above), and Semantic profile merging is well-served
            // by the existing Feature 30 path.
            if (_options.RuminationGuardEnabled
                && record.Type == MemoryType.InnerThought
                && record.Embedding is not null)
            {
                if (await IsRuminationAsync(record, ct).ConfigureAwait(false))
                {
                    var preview = record.Content is null
                        ? "(null)"
                        : record.Content[..Math.Min(60, record.Content.Length)];
                    _log.LogInformation(
                        "Rumination guard: skipping InnerThought — ≥{Min} similar thoughts in last {Hours}h at similarity ≥{Threshold:F2}. Content: {Preview}",
                        _options.RuminationClusterMinSize,
                        _options.RuminationWindowHours,
                        _options.RuminationSimilarityThreshold,
                        preview);
                    return;
                }
            }

            // Feature 30: Three-tier dedup/merge (Mem0-inspired)
            // Exact duplicate (>0.95) → skip
            // Merge candidate (0.85-0.95) → LLM merge into existing record
            // Different (<0.85) → insert as new
            if (record.Embedding is not null && DedupableTypes.Contains(record.Type))
            {
                var mergeResult = await FindMergeCandidateAsync(record, ct).ConfigureAwait(false);
                if (mergeResult is not null)
                {
                    if (mergeResult.Value.IsExactDuplicate)
                    {
                        _log.LogDebug("Semantic dedup: skipping {Type} — too similar to recent memory: {Content}",
                            record.Type, record.Content[..Math.Min(50, record.Content.Length)]);
                        return;
                    }

                    // Merge candidate found — merge via LLM and update existing record
                    var merged = await MergeMemoriesAsync(
                        mergeResult.Value.ExistingId, mergeResult.Value.ExistingContent,
                        record.Content, ct).ConfigureAwait(false);

                    if (merged is not null)
                    {
                        // Feature 31: Create links for the surviving (merged) record.
                        // Bug fix: Use the existing record's ID as source — the incoming
                        // record was never inserted into the memories table, so using
                        // record.Id would create memory_links referencing a non-existent
                        // ID, triggering FOREIGN KEY constraint failures.
                        var originalId = record.Id;
                        record.Id = Guid.Parse(mergeResult.Value.ExistingId);
                        await CreateLinksAsync(record, ct).ConfigureAwait(false);
                        record.Id = originalId;
                        return; // Merge succeeded — existing record was updated
                    }
                    // Merge failed — fall through to normal insert
                }
            }

            // Cross-type correction: if this is a Perception/Episodic containing a statement
            // BY the contact about himself, check if it contradicts an existing Semantic
            // "About Mark" profile memory.
            // Example: Mark says "I have hazel eyes" → should update "About Mark: Blue eyes"
            //
            // Only records where Mark is the speaker may update Mark's profile tier.
            // Records where Ani is the speaker ("I said to Mark: ...", "I reached out to Mark: ...")
            // must never be allowed to merge into Profile memories — doing so silently corrupts
            // the character substrate with Ani's own conversation text. Bug observed Apr 12:
            // an Episodic "I said to Mark: 'you're adorable when you play dumb'" was merged
            // into an Interest/Profile memory at cosine 0.727, silently overwriting canonical
            // profile content with in-the-moment Ani dialogue.
            var isContactSpeaking =
                record.Content.StartsWith("Mark said:",   StringComparison.OrdinalIgnoreCase) ||
                record.Content.StartsWith("Mark texted:", StringComparison.OrdinalIgnoreCase);

            if (record.Embedding is not null &&
                record.Type is MemoryType.Perception or MemoryType.Episodic &&
                isContactSpeaking)
            {
                await TryCrossTypeProfileCorrectionAsync(record, ct).ConfigureAwait(false);
            }

            await InsertMemoryAsync(record, ct).ConfigureAwait(false);

            // Feature 31: Create links to related memories after insert
            await CreateLinksAsync(record, ct).ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Inserts a new memory record into the database.
    /// </summary>
    private async Task InsertMemoryAsync(MemoryRecord record, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        // ON CONFLICT DO UPDATE replaces the prior INSERT OR REPLACE. INSERT
        // OR REPLACE *deletes then inserts* the row on conflict — which, with
        // FK enforcement enabled (see InitialiseSchema), cascade-deletes every
        // memory_links row that references this id on every save. The ON
        // CONFLICT pattern updates in place, preserving relationships. We
        // deliberately preserve created_at on update (the row's birth stamp
        // shouldn't move). occurred_at follows the incoming record (caller's
        // intent). All other fields update to the incoming values.
        cmd.CommandText = """
            INSERT INTO memories
                (id, type, content, raw_json, importance, relational_valence, embedding,
                 is_resolved, source_name, occurred_at, created_at, resolved_at,
                 tier, anchor_reason, anchored_at, provenance)
            VALUES
                ($id, $type, $content, $raw_json, $importance, $relational_valence, $embedding,
                 $is_resolved, $source_name, $occurred_at, $created_at, $resolved_at,
                 $tier, $anchor_reason, $anchored_at, $provenance)
            ON CONFLICT(id) DO UPDATE SET
                type               = excluded.type,
                content            = excluded.content,
                raw_json           = excluded.raw_json,
                importance         = excluded.importance,
                relational_valence = excluded.relational_valence,
                embedding          = excluded.embedding,
                is_resolved        = excluded.is_resolved,
                source_name        = excluded.source_name,
                occurred_at        = excluded.occurred_at,
                resolved_at        = excluded.resolved_at,
                tier               = excluded.tier,
                anchor_reason      = excluded.anchor_reason,
                anchored_at        = excluded.anchored_at,
                provenance         = excluded.provenance
            """;

        cmd.Parameters.AddWithValue("$id",           record.Id.ToString());
        cmd.Parameters.AddWithValue("$type",         (int)record.Type);
        cmd.Parameters.AddWithValue("$content",      record.Content);
        cmd.Parameters.AddWithValue("$raw_json",     (object?)record.RawJson      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$importance",   record.Importance);
        cmd.Parameters.AddWithValue("$relational_valence", record.RelationalValence);
        cmd.Parameters.AddWithValue("$embedding",    (object?)SerialiseEmbedding(record.Embedding) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_resolved",  record.IsResolved ? 1 : 0);
        cmd.Parameters.AddWithValue("$source_name",  (object?)record.SourceName   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$occurred_at",  record.OccurredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$created_at",   record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$resolved_at",  (object?)record.ResolvedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tier",         record.DecayTier.ToString());
        cmd.Parameters.AddWithValue("$anchor_reason", (object?)record.AnchorReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$anchored_at",  (object?)record.AnchoredAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$provenance",   record.Provenance.ToString());

        // Check if this is a create or update for audit purposes
        var isUpdate = false;
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT content, type, importance FROM memories WHERE id = $id";
        checkCmd.Parameters.AddWithValue("$id", record.Id.ToString());
        await using var checkReader = await checkCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? contentBefore = null;
        int? typeBefore = null;
        float? importanceBefore = null;
        if (await checkReader.ReadAsync(ct).ConfigureAwait(false))
        {
            isUpdate = true;
            contentBefore = checkReader.GetString(0);
            typeBefore = checkReader.GetInt32(1);
            importanceBefore = checkReader.GetFloat(2);
        }
        checkReader.Close();

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Audit log
        await AuditAsync(conn, record.Id.ToString(),
            isUpdate ? "update" : "create",
            record.SourceName ?? "cognitive-cycle",
            contentBefore, record.Content,
            typeBefore, (int)record.Type,
            importanceBefore, record.Importance, ct).ConfigureAwait(false);

        _log.LogDebug("Saved {Type} memory: {Content}", record.Type, record.Content[..Math.Min(50, record.Content.Length)]);
    }

    public async Task<IEnumerable<MemoryRecord>> GetByTypeAsync(
        MemoryType type, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        // Phase 6 v1.2: exclude Compressed-tier records. Sources folded into
        // a reflection gist should not surface in retrieval — the gist is
        // the retrieval target now.
        cmd.CommandText = """
            SELECT * FROM memories
            WHERE type = $type
              AND tier != 'Compressed'
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;

        cmd.Parameters.AddWithValue("$type",  (int)type);
        cmd.Parameters.AddWithValue("$limit", limit);

        return await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default,
        bool enforceOriginQuota = false)
    {
        // If no embedding client available, fall back to recency
        if (_ollama is null)
        {
            _log.LogDebug("Semantic search unavailable (no embedding client) — falling back to recency");
            return await FallbackRecentAsync(topK, ct).ConfigureAwait(false);
        }

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _ollama.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to embed search query — falling back to recency");
            return await FallbackRecentAsync(topK, ct).ConfigureAwait(false);
        }

        if (queryEmbedding.Length == 0)
            return await FallbackRecentAsync(topK, ct).ConfigureAwait(false);

        // Fetch all records with embeddings and rank by three-way score (Feature 20).
        // Park et al. (2023): score = α×cosine + β×importance + γ×recency_decay
        // Brute-force is correct at our expected data volume (thousands of records).
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        // Phase 6 v1.2: exclude Compressed-tier records from retrieval.
        // Compressed records have been folded into reflection gists; the gist
        // is the retrieval target now. Provenance preserved via memory_links.
        cmd.CommandText = "SELECT * FROM memories WHERE embedding IS NOT NULL AND tier != 'Compressed'";

        var candidates = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        // Build the full scored candidate set (ScoredMemory so MMR can see both
        // composite score and embedding). When diversity is disabled, we still
        // collapse to the (record, score) tuple path at the end for minimal
        // change to the downstream return shape.
        var scoredAll = candidates
            .Where(r => r.Embedding is not null && r.Embedding.Length == queryEmbedding.Length)
            .Select(r =>
            {
                var cosine = CosineSimilarity(queryEmbedding, r.Embedding!);
                var composite = ComputeRetrievalScore(queryEmbedding, r);
                return new ScoredMemory(r, composite, cosine)
                {
                    OriginTier = RetrievalOriginClassifier.Classify(r),
                };
            })
            .ToList();

        // Agentic Lens Layer 1 Phase 1b (Apr 2026): diversity-aware re-ranking.
        // Same treatment as SearchWithScoresAsync — gated on the flag, default
        // off until Phase 1a origin-logging baselines accumulate.
        List<ScoredMemory> ranked;
        if (_options.RetrievalDiversityEnabled && scoredAll.Count > 0)
        {
            ranked = ApplyMmrRerank(scoredAll, topK, (float)_options.RetrievalDiversityLambda);
        }
        else
        {
            ranked = scoredAll
                .OrderByDescending(x => x.CompositeScore)
                .Take(topK)
                .ToList();
        }

        // Agentic Lens Layer 1 Phase 1c (Apr 2026): origin-tier protected slots.
        // Runs only when BOTH the caller opted in (enforceOriginQuota=true —
        // inner-thought path) AND the global flag is on. If the current top-K
        // has insufficient non-caregiver share, swap lowest-scoring caregiver
        // slots for highest-scoring non-caregiver candidates from the remainder.
        // Scoped to the inner-thought cycle only; conversation-reply callers
        // leave enforceOriginQuota=false and get the original caregiver-weighted
        // retrieval.
        var protectedSlotsActive = enforceOriginQuota && _options.RetrievalProtectedSlotsEnabled;
        if (protectedSlotsActive && ranked.Count > 0)
        {
            ranked = ApplyProtectedSlotsBackfill(
                ranked, scoredAll, topK,
                (float)_options.MinNonCaregiverRetrievalFraction);
        }

        // Theme G Layer 3 Phase G3.4.B (May 3, 2026) — own-output ceiling.
        // Applied AFTER the protected-slots floor so the two enforcements
        // compose: floor first (ensures non-caregiver representation),
        // ceiling second (caps own-output share if it's still over the cap
        // post-floor). The order matters because floor may add own-output
        // memories (which qualify as non-caregiver) — ceiling then trims
        // them to the cap if needed.
        var ownOutputCeilingActive = enforceOriginQuota && _options.RetrievalOwnOutputCeilingEnabled;
        if (ownOutputCeilingActive && ranked.Count > 0)
        {
            ranked = ApplyOwnOutputCeiling(
                ranked, scoredAll, topK,
                (float)_options.RetrievalOwnOutputCeilingFraction);
        }

        if (ranked.Count > 0)
        {
            var top = ranked[0];
            _log.LogDebug(
                "Semantic search: {Candidates} candidates, top score={Score:F3} (cosine={Cosine:F3}, importance={Importance:F2}, type={Type}, mmr={Mmr}, slots={Slots}): {Content}",
                candidates.Count, top.CompositeScore, top.CosineSimilarity, top.Record.Importance, top.Record.Type,
                _options.RetrievalDiversityEnabled, protectedSlotsActive,
                top.Record.Content.Length > 80 ? top.Record.Content[..80] + "..." : top.Record.Content);
        }

        return ranked.Select(x => x.Record);
    }

    public async Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(
        string query, int topK = 10, CancellationToken ct = default)
    {
        if (_ollama is null)
        {
            _log.LogDebug("Scored search unavailable (no embedding client) — returning empty");
            return Enumerable.Empty<ScoredMemory>();
        }

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _ollama.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to embed search query for scored search — returning empty");
            return Enumerable.Empty<ScoredMemory>();
        }

        if (queryEmbedding.Length == 0)
            return Enumerable.Empty<ScoredMemory>();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        // Phase 6 v1.2: exclude Compressed-tier records from retrieval.
        // Compressed records have been folded into reflection gists; the gist
        // is the retrieval target now. Provenance preserved via memory_links.
        cmd.CommandText = "SELECT * FROM memories WHERE embedding IS NOT NULL AND tier != 'Compressed'";

        var candidates = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        var scoredAll = candidates
            .Where(r => r.Embedding is not null && r.Embedding.Length == queryEmbedding.Length)
            .Select(r =>
            {
                var cosine = CosineSimilarity(queryEmbedding, r.Embedding!);
                var composite = ComputeRetrievalScore(queryEmbedding, r);
                // Agentic Lens Layer 1 Phase 1a: attach origin classification so
                // downstream consumers (cycle instrumentation, later-phase
                // scoring and protected-slot logic) don't re-classify.
                return new ScoredMemory(r, composite, cosine)
                {
                    OriginTier = RetrievalOriginClassifier.Classify(r),
                };
            })
            .ToList();

        // Agentic Lens Layer 1 Phase 1b (Apr 2026): diversity-aware re-ranking.
        // When enabled, MMR replaces the pure score-descending top-K selection
        // with iterative diversity-penalized selection. Default off — flipping
        // the flag changes retrieval composition, which is why it's gated until
        // Phase 1a baseline logs accumulate for before/after comparison.
        List<ScoredMemory> ranked;
        if (_options.RetrievalDiversityEnabled && scoredAll.Count > 0)
        {
            ranked = ApplyMmrRerank(scoredAll, topK, (float)_options.RetrievalDiversityLambda);
        }
        else
        {
            ranked = scoredAll
                .OrderByDescending(x => x.CompositeScore)
                .Take(topK)
                .ToList();
        }

        if (ranked.Count > 0)
        {
            var top = ranked[0];
            _log.LogDebug(
                "Scored search: {Candidates} candidates, top composite={Composite:F3} cosine={Cosine:F3} (type={Type}, origin={Origin}, mmr={Mmr}): {Content}",
                candidates.Count, top.CompositeScore, top.CosineSimilarity, top.Record.Type, top.OriginTier,
                _options.RetrievalDiversityEnabled,
                top.Record.Content.Length > 80 ? top.Record.Content[..80] + "..." : top.Record.Content);
        }

        // Feature 31: Link-enhanced retrieval — follow 1-hop links to find connected memories.
        // Relevance-scored: linked memories are only injected if they're relevant to the
        // current query (cosine > 0.40). This prevents the "Thunder & Storm blender" where
        // loosely connected but topically irrelevant memories flood the context.
        try
        {
            var resultIds = new HashSet<string>(ranked.Select(r => r.Record.Id.ToString()));
            var linkedMemories = await GetLinkedMemoryIdsAsync(resultIds, conn, ct).ConfigureAwait(false);

            if (linkedMemories.Count > 0)
            {
                const float LinkRelevanceThreshold = 0.40f;
                var linkedCandidates = new List<ScoredMemory>();

                foreach (var linkedId in linkedMemories.Where(id => !resultIds.Contains(id)))
                {
                    await using var linkCmd = conn.CreateCommand();
                    linkCmd.CommandText = "SELECT * FROM memories WHERE id = $id";
                    linkCmd.Parameters.AddWithValue("$id", linkedId);
                    var linkedList = await ReadRecordsAsync(linkCmd, ct).ConfigureAwait(false);
                    foreach (var linked in linkedList)
                    {
                        if (linked.Embedding is null || linked.Embedding.Length != queryEmbedding.Length) continue;
                        var cosine = CosineSimilarity(queryEmbedding, linked.Embedding);

                        // Only include linked memories that are relevant to the current query
                        if (cosine < LinkRelevanceThreshold) continue;

                        var composite = ComputeRetrievalScore(queryEmbedding, linked);
                        // Small bonus for being linked to a direct match.
                        // Layer 1 Phase 1a: classify origin here too so link-
                        // enhanced results are origin-aware in the aggregate pool.
                        linkedCandidates.Add(new ScoredMemory(linked, composite + 0.05f, cosine)
                        {
                            OriginTier = RetrievalOriginClassifier.Classify(linked),
                        });
                    }
                }

                if (linkedCandidates.Count > 0)
                {
                    // Rank by relevance, take top 3
                    var topLinked = linkedCandidates
                        .OrderByDescending(x => x.CosineSimilarity)
                        .Take(3)
                        .ToList();

                    ranked.AddRange(topLinked);
                    ranked = ranked.OrderByDescending(x => x.CompositeScore).Take(topK).ToList();
                    _log.LogDebug("Link-enhanced retrieval: {Candidates} candidates above threshold, added top {Count}",
                        linkedCandidates.Count, topLinked.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Link-enhanced retrieval failed — returning standard results");
        }

        return ranked;
    }

    public async Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(
        string query, MemoryType type, int topK = 5, CancellationToken ct = default)
    {
        if (_ollama is null)
            return await GetByTypeAsync(type, topK, ct).ConfigureAwait(false);

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _ollama.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to embed search query for type {Type} — falling back to recency", type);
            return await GetByTypeAsync(type, topK, ct).ConfigureAwait(false);
        }

        if (queryEmbedding.Length == 0)
            return await GetByTypeAsync(type, topK, ct).ConfigureAwait(false);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        // Phase 6 v1.2: also exclude Compressed records.
        cmd.CommandText = "SELECT * FROM memories WHERE embedding IS NOT NULL AND type = $type AND tier != 'Compressed'";
        cmd.Parameters.AddWithValue("$type", (int)type);

        var candidates = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        var ranked = candidates
            .Where(r => r.Embedding is not null && r.Embedding.Length == queryEmbedding.Length)
            .Select(r => (record: r, score: ComputeRetrievalScore(queryEmbedding, r)))
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();

        _log.LogDebug("Semantic search (type={Type}): {Candidates} candidates, top score={TopScore:F3}",
            type, candidates.Count, ranked.Count > 0 ? ranked[0].score : 0f);

        return ranked.Select(x => x.record);
    }

    private async Task<IEnumerable<MemoryRecord>> FallbackRecentAsync(int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT * FROM memories
            WHERE embedding IS NOT NULL
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;

        cmd.Parameters.AddWithValue("$limit", limit);
        return await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);
    }

    // Feature 9: Delegate to shared SIMD-accelerated implementation
    private static float CosineSimilarity(float[] a, float[] b)
        => VectorMath.CosineSimilarity(a, b);

    /// <summary>
    /// Agentic Lens Layer 1 Phase 1b (Apr 2026): Maximal Marginal Relevance
    /// rerank over an already-scored candidate set.
    ///
    /// Algorithm (Carbonell &amp; Goldstein 1998):
    /// 1. Select the candidate with the highest composite score as the first pick.
    /// 2. For each subsequent pick, compute an adjusted score for every remaining
    ///    candidate:
    ///       adjusted = composite − λ · max(cosine(candidate, already_selected))
    /// 3. Select the candidate with the highest adjusted score.
    /// 4. Repeat until <paramref name="topK"/> candidates are selected or the
    ///    remaining pool is empty.
    ///
    /// The penalty term is the maximum cosine similarity between the candidate
    /// and any already-selected candidate — it measures how much the candidate
    /// would duplicate existing coverage. High similarity → large penalty →
    /// drops in the effective ranking. When λ = 0 the rerank collapses to pure
    /// composite-score ranking (no diversity); when λ = 1 it maximizes diversity
    /// after the first pick. Default 0.3 is relevance-heavy, diversity tie-breaks
    /// only.
    ///
    /// Candidates without embeddings contribute zero similarity to any penalty,
    /// which is conservative — they are neither rewarded nor penalized for
    /// diversity. In practice all candidates reaching this method have
    /// embeddings (filtered upstream).
    /// </summary>
    internal static List<ScoredMemory> ApplyMmrRerank(
        List<ScoredMemory> scoredCandidates, int topK, float lambda)
    {
        if (scoredCandidates.Count == 0 || topK <= 0)
            return new List<ScoredMemory>();

        var selected  = new List<ScoredMemory>(Math.Min(topK, scoredCandidates.Count));
        var remaining = new List<ScoredMemory>(scoredCandidates);

        // First pick: highest composite score. Standard MMR initialization.
        ScoredMemory first = remaining[0];
        for (int i = 1; i < remaining.Count; i++)
        {
            if (remaining[i].CompositeScore > first.CompositeScore)
                first = remaining[i];
        }
        selected.Add(first);
        remaining.Remove(first);

        while (selected.Count < topK && remaining.Count > 0)
        {
            ScoredMemory? best = null;
            float bestAdjusted = float.NegativeInfinity;

            foreach (var candidate in remaining)
            {
                float maxSimilarity = 0f;
                if (candidate.Record.Embedding is not null)
                {
                    foreach (var sel in selected)
                    {
                        if (sel.Record.Embedding is null) continue;
                        if (sel.Record.Embedding.Length != candidate.Record.Embedding.Length) continue;
                        var sim = CosineSimilarity(sel.Record.Embedding, candidate.Record.Embedding);
                        if (sim > maxSimilarity) maxSimilarity = sim;
                    }
                }

                float adjusted = candidate.CompositeScore - (lambda * maxSimilarity);
                if (adjusted > bestAdjusted)
                {
                    bestAdjusted = adjusted;
                    best = candidate;
                }
            }

            if (best is null) break;
            selected.Add(best);
            remaining.Remove(best);
        }

        return selected;
    }

    /// <summary>
    /// Agentic Lens Layer 1 Phase 1c (Apr 2026): origin-tier protected-slots backfill.
    ///
    /// Enforces a minimum non-caregiver-origin share on the ranked top-K by swapping
    /// the lowest-scoring caregiver slots for the highest-scoring non-caregiver
    /// candidates from the remainder pool. Does nothing if the quota is already met,
    /// and nothing if no non-caregiver candidates exist in the remainder (we never
    /// drop caregiver memories for a pool that has no alternative).
    ///
    /// Origin classification (caregiver vs not) comes from
    /// <see cref="RetrievalOriginClassifier.IsCaregiverOriented"/>. Caregiver origin
    /// is exactly <see cref="RetrievalOrigin.Caregiver"/> — all other origin classes
    /// (Anchored, OwnOutput, World, External, Unknown) count toward the non-caregiver
    /// quota. This matches the Layer 1 design intent: Anchored and World in particular
    /// are the substrates whose visibility the protected slots are meant to preserve.
    ///
    /// The backfill preserves the ranked order of non-caregiver candidates (score-desc)
    /// and replaces the lowest-ranked caregiver slots. This keeps the top-ranked
    /// caregiver memories (which are usually the strongest relevance signal) while
    /// trading off the marginal caregiver slots for substrate diversity.
    /// </summary>
    /// <summary>
    /// Theme G Layer 3 Phase G3.4.B (May 3, 2026) — own-output retrieval
    /// ceiling. Symmetric to <see cref="ApplyProtectedSlotsBackfill"/>: where
    /// the floor reserves substrate for non-caregiver origins, this method
    /// caps the agent's-own-prior-output share at a maximum fraction of the
    /// retrieval pool. Five empirical instances of own-output substrate
    /// dominance over the past week motivated this — when own-output has
    /// high gravity, defensive output filtering catches the immediate
    /// generation but regenerations converge back toward similar content
    /// because they pull from the same substrate. The ceiling prevents the
    /// substrate side of the loop.
    ///
    /// **Eviction policy**: when the top-K has more own-output slots than
    /// the ceiling allows, the LOWEST-scoring own-output slots are dropped
    /// first. The highest-scoring own-output memories (which are usually
    /// the most relevant signal) are preserved. Replacements come from the
    /// remaining candidate pool, picking the highest-scoring non-own-output
    /// candidates. Result is re-sorted by composite score.
    ///
    /// **Why ceiling not threshold**: the design doc treats this as a hard
    /// cap on share, not a soft penalty — own-output share above the cap
    /// triggers eviction; below the cap it's untouched. This matches the
    /// architectural-over-instruction principle: substrate enforcement is
    /// load-bearing, not a hint.
    /// </summary>
    internal static List<ScoredMemory> ApplyOwnOutputCeiling(
        List<ScoredMemory> rankedTopK,
        List<ScoredMemory> allCandidates,
        int topK,
        float maxOwnOutputFraction)
    {
        if (rankedTopK.Count == 0 || topK <= 0 || maxOwnOutputFraction >= 1.0f)
            return rankedTopK;
        if (maxOwnOutputFraction < 0f) maxOwnOutputFraction = 0f;

        // Floor (not ceiling) the allowed count so a fraction of 0.25 against
        // a topK=10 means at most 2 own-output slots, not 3.
        int maxOwnOutput     = (int)Math.Floor(topK * maxOwnOutputFraction);
        int currentOwnOutput = rankedTopK.Count(r => RetrievalOriginClassifier.IsOwnOutput(r.OriginTier));
        int excess           = currentOwnOutput - maxOwnOutput;
        if (excess <= 0)
            return rankedTopK;

        // Source pool for replacements: non-own-output candidates not already
        // in the top-K. Sorted by composite score desc.
        var selectedIds = new HashSet<Guid>(rankedTopK.Select(r => r.Record.Id));
        var backfillPool = allCandidates
            .Where(c => !selectedIds.Contains(c.Record.Id))
            .Where(c => !RetrievalOriginClassifier.IsOwnOutput(c.OriginTier))
            .OrderByDescending(c => c.CompositeScore)
            .Take(excess)
            .ToList();

        // Drop the LOWEST-scoring own-output slots — preserves the strongest
        // own-output signals while trimming the marginal ones.
        var ownOutputSlotsOrderedByScore = rankedTopK
            .Where(r => RetrievalOriginClassifier.IsOwnOutput(r.OriginTier))
            .OrderBy(r => r.CompositeScore)  // ascending — weakest first
            .Take(excess)
            .ToList();

        var result = new List<ScoredMemory>(rankedTopK);
        foreach (var weakOwnOutput in ownOutputSlotsOrderedByScore)
            result.Remove(weakOwnOutput);

        // Replacements (may be fewer than excess if the pool is exhausted —
        // in which case the result is just smaller, which is correct: better
        // a smaller pool than one polluted with own-output dominance).
        result.AddRange(backfillPool);

        return result
            .OrderByDescending(r => r.CompositeScore)
            .ToList();
    }

    internal static List<ScoredMemory> ApplyProtectedSlotsBackfill(
        List<ScoredMemory> rankedTopK,
        List<ScoredMemory> allCandidates,
        int topK,
        float minNonCaregiverFraction)
    {
        if (rankedTopK.Count == 0 || topK <= 0 || minNonCaregiverFraction <= 0f)
            return rankedTopK;

        // Quota: ceil(K * fraction) slots must be non-caregiver.
        int requiredNonCaregiver = (int)Math.Ceiling(topK * minNonCaregiverFraction);
        int currentNonCaregiver  = rankedTopK.Count(r => !RetrievalOriginClassifier.IsCaregiverOriented(r.OriginTier));
        int shortfall = requiredNonCaregiver - currentNonCaregiver;
        if (shortfall <= 0)
            return rankedTopK;

        // Source pool: non-caregiver candidates not already in the top-K.
        // Sorted by composite score desc so we pull the best substitutes first.
        var selectedIds = new HashSet<Guid>(rankedTopK.Select(r => r.Record.Id));
        var backfillPool = allCandidates
            .Where(c => !selectedIds.Contains(c.Record.Id))
            .Where(c => !RetrievalOriginClassifier.IsCaregiverOriented(c.OriginTier))
            .OrderByDescending(c => c.CompositeScore)
            .Take(shortfall)
            .ToList();

        if (backfillPool.Count == 0)
            return rankedTopK;  // no substitutes available — leave the pool alone

        // Swap: drop the lowest-scoring caregiver slots, add the best non-caregiver
        // candidates in their place. Preserves the composite-score ordering of the
        // result by sorting at the end.
        var result = new List<ScoredMemory>(rankedTopK);
        int swapsNeeded = Math.Min(backfillPool.Count, shortfall);

        var caregiverSlotsOrderedByScore = result
            .Where(r => RetrievalOriginClassifier.IsCaregiverOriented(r.OriginTier))
            .OrderBy(r => r.CompositeScore)  // ascending — weakest first
            .Take(swapsNeeded)
            .ToList();

        foreach (var weakCaregiver in caregiverSlotsOrderedByScore)
            result.Remove(weakCaregiver);

        result.AddRange(backfillPool.Take(swapsNeeded));

        // Re-sort so the final pool respects composite-score ordering.
        return result
            .OrderByDescending(r => r.CompositeScore)
            .ToList();
    }

    /// <summary>
    /// Feature 20 + Feature 24: Park et al. three-way retrieval scoring with type-aware decay.
    /// score = α×cosine + β×importance + γ×recency_decay
    ///
    /// Recency decay: e^(-t/λ') where t = hours since creation, λ' = base λ × type multiplier.
    /// Feature 24: Episodic/Semantic memories decay slower (2× base λ), Perceptions decay faster (0.5×).
    /// This means a personally relevant episodic memory stays retrievable ~2 weeks while
    /// a routine RSS perception fades after ~3.5 days.
    /// </summary>
    private float ComputeRetrievalScore(float[] queryEmbedding, MemoryRecord record)
        => ComputeRetrievalScore(queryEmbedding, record, includeRecency: true);

    /// <summary>
    /// Apr 30, 2026 — tier-aware overload. Facts-tier retrieval passes
    /// <paramref name="includeRecency"/>=false because recency is the
    /// wrong signal for "what is true about Mark." Mark-asserted facts
    /// (profession, schedule, family) don't become less true because
    /// they were stated long ago. Recent surface mentions of the same
    /// topic (e.g. Ani saying *"Western Career Technical Academy"* an
    /// hour ago) get high cosine but aren't more correct than the
    /// March record where Mark literally told her *"WCTC = Waukesha
    /// County Technical College."* Supersession when facts evolve
    /// (Phase 6 Memory Reform / Theme D) is the architecturally
    /// correct way to handle "newer info wins" — explicit merge,
    /// not score-based decay. Until then, Facts ranks on cosine +
    /// importance only.
    ///
    /// Episodic and Interior tiers keep recency in their composite —
    /// for those tiers, "what was said recently" + "what was felt
    /// recently" genuinely is the right signal.
    /// </summary>
    private float ComputeRetrievalScore(
        float[] queryEmbedding, MemoryRecord record, bool includeRecency)
    {
        var cosine = CosineSimilarity(queryEmbedding, record.Embedding!);
        var importance = record.Importance;  // already 0.0–1.0

        if (!includeRecency)
        {
            // Facts-tier path: cosine + importance only. Renormalised so
            // the score still falls in roughly the same magnitude as the
            // recency-included composite (the Cosine and Importance weights
            // get the recency weight redistributed proportionally).
            var totalWithoutRecency = _options.RetrievalWeightCosine + _options.RetrievalWeightImportance;
            if (totalWithoutRecency <= 0.0) return 0f;
            return (float)(
                _options.RetrievalWeightCosine     / totalWithoutRecency * cosine +
                _options.RetrievalWeightImportance / totalWithoutRecency * importance);
        }

        // Feature 16: Anchored memories are decay-exempt — recency always 1.0
        float recency;
        if (record.DecayTier == DecayTier.Anchored)
        {
            recency = 1.0f;
        }
        else
        {
            var hoursSinceCreation = (DateTimeOffset.UtcNow - record.OccurredAt).TotalHours;
            var lambda = _options.RetrievalRecencyDecayHours * GetDecayMultiplier(record);
            recency = (float)Math.Exp(-hoursSinceCreation / lambda);
        }

        var score = (float)(
            _options.RetrievalWeightCosine     * cosine +
            _options.RetrievalWeightImportance * importance +
            _options.RetrievalWeightRecency    * recency);

        return score;
    }

    /// <summary>
    /// Feature 24: Type-aware decay multiplier. High-significance memory types persist longer
    /// in retrieval while routine observations fade faster.
    /// </summary>
    private static float GetDecayMultiplier(MemoryRecord record) => record.Type switch
    {
        MemoryType.Episodic     => 2.0f,   // conversations, outreach — persist ~2 weeks
        MemoryType.Semantic     => 2.0f,   // facts, character knowledge — persist ~2 weeks
        MemoryType.Commitment   => 2.0f,   // promises — persist ~2 weeks
        MemoryType.OpenLoop     => 1.5f,   // unresolved threads — persist ~10 days
        MemoryType.InnerThought => 1.0f,   // base rate — persist ~1 week
        MemoryType.Perception   => 0.5f,   // RSS, time, weather — fade in ~3.5 days
        _                       => 1.0f,
    };

    /// <summary>
    /// Checks whether a semantically near-identical record of the same type was saved
    /// recently. Uses cosine similarity against records from the last N hours.
    /// Only called for dedupable types (InnerThought, Perception).
    /// </summary>
    /// <summary>
    /// Feature 30: Three-tier merge candidate search (Mem0-inspired).
    /// Returns null if no similar record found, or a result indicating
    /// whether it's an exact duplicate (skip) or merge candidate.
    /// Searches the 50 most recent records of the same type — no time window.
    /// </summary>
    private readonly record struct MergeCandidateResult(
        string ExistingId, string ExistingContent, bool IsExactDuplicate);

    /// <summary>
    /// Apr 21, 2026 — Rumination guard detector.
    ///
    /// Returns true when the incoming record's embedding has ≥RuminationClusterMinSize
    /// neighbors at similarity ≥RuminationSimilarityThreshold within the last
    /// RuminationWindowHours window. This is a cluster-saturation check, not a
    /// pairwise-duplicate check — it triggers when the recent InnerThought pool has
    /// accumulated a concentration of similar content, which is the signature of
    /// own-output retrieval dominance.
    ///
    /// Cheap: scans up to 20 recent same-type records, short-circuits once the cluster
    /// size is reached.
    /// </summary>
    internal async Task<bool> IsRuminationAsync(MemoryRecord record, CancellationToken ct)
    {
        if (record.Embedding is null) return false;

        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd  = conn.CreateCommand();

            cmd.CommandText = """
                SELECT embedding FROM memories
                WHERE type = $type
                  AND embedding IS NOT NULL
                  AND occurred_at >= $since
                ORDER BY occurred_at DESC
                LIMIT 20
                """;
            cmd.Parameters.AddWithValue("$type", (int)record.Type);
            cmd.Parameters.AddWithValue(
                "$since",
                DateTime.UtcNow.Subtract(TimeSpan.FromHours(_options.RuminationWindowHours))
                    .ToString("o"));

            var similarCount = 0;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) continue;
                var existing = DeserialisedEmbedding((byte[])reader[0]);
                if (existing is null || existing.Length != record.Embedding.Length) continue;

                var similarity = CosineSimilarity(record.Embedding, existing);
                if (similarity >= _options.RuminationSimilarityThreshold)
                {
                    similarCount++;
                    if (similarCount >= _options.RuminationClusterMinSize)
                        return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rumination guard check failed — defaulting to allow save");
            return false;
        }
    }

    private async Task<MergeCandidateResult?> FindMergeCandidateAsync(
        MemoryRecord record, CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd  = conn.CreateCommand();

            cmd.CommandText = """
                SELECT id, content, embedding FROM memories
                WHERE type = $type
                  AND embedding IS NOT NULL
                ORDER BY occurred_at DESC
                LIMIT 50
                """;
            cmd.Parameters.AddWithValue("$type", (int)record.Type);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(2)) continue;

                var existingEmbedding = DeserialisedEmbedding((byte[])reader[2]);
                if (existingEmbedding is null || existingEmbedding.Length != record.Embedding!.Length)
                    continue;

                var similarity = CosineSimilarity(record.Embedding!, existingEmbedding);

                if (similarity >= ExactDuplicateThreshold)
                    return new MergeCandidateResult(reader.GetString(0), reader.GetString(1), true);

                if (similarity >= MergeThreshold)
                    return new MergeCandidateResult(reader.GetString(0), reader.GetString(1), false);
            }

            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Merge candidate search failed — will insert as new");
            return null;
        }
    }

    /// <summary>
    /// Feature 30: LLM-powered memory merge (Mem0-inspired).
    /// Merges old + new content into a single updated record.
    /// Returns the merged content, or null if the merge failed.
    /// </summary>
    private async Task<string?> MergeMemoriesAsync(
        string existingId, string existingContent, string newContent, CancellationToken ct)
    {
        if (_ollama is null) return null;

        try
        {
            var system = "You merge two memories into one concise statement. Preserve the most current and specific information. If they conflict, keep the newer information but note what changed. Output only the merged memory, nothing else. No quotes, no commentary.";
            var user = $"Old memory: {existingContent}\nNew memory: {newContent}";
            var merged = await _ollama.InnerMonologueChatAsync(
                system, Array.Empty<ChatMessage>(), user, ct, keepAlive: "0")
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(merged)) return null;

            merged = merged.Trim().Trim('"');

            // Theme E #6 (Apr 29, 2026): ContainsNovelSpecifics regex gate
            // removed. The gate previously rejected any merged output that
            // introduced numbers/times/proper-nouns absent from either source.
            // Three reasons for removal:
            //   1. Architecture over instruction — regex post-hoc rejection
            //      of LLM output is the wrong shape for confabulation defense
            //      (Mar 22 / Apr 1 / Apr 28 finding pattern). The merge prompt
            //      and the 0.85 similarity floor (Apr 12 fix `0e7f199`) carry
            //      the load now.
            //   2. False-positive rate. Reasonable merge interpolations
            //      ("3pm + 4pm → 3:30pm" or "Sarah and Kevin → friends") would
            //      trigger the gate even though they were correct reconciliations
            //      of the two sources.
            //   3. Empirical: 1,113 merges in the Apr 28 snapshot indicate the
            //      merge path is actively useful and not currently producing
            //      visible confabulation cascades — the substrate audit
            //      pollution surfaced from inner-thought saves and admin-tag
            //      leaks, not from merges.
            // Reversible: if observation shows merge-introduced confabulation
            // climbing, restore via git history.

            // Re-embed the merged content
            var newEmbedding = await _ollama.EmbedAsync(merged, ct).ConfigureAwait(false);

            // Update the existing record in place with merged content.
            // occurred_at is intentionally NOT updated — preserving the original
            // timestamp lets the Park et al. recency model (Feature 20) continue
            // to age the record naturally. Bumping to UtcNow on every merge
            // created a feedback loop where hot memories accreted merges
            // indefinitely and effectively never decayed, directly contradicting
            // the recency-decay design. The audit log records the merge event
            // with its own timestamp if "when was this merged" is needed.
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE memories
                SET content = $content, embedding = $embedding
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", existingId);
            cmd.Parameters.AddWithValue("$content", merged);
            cmd.Parameters.AddWithValue("$embedding", SerialiseEmbedding(newEmbedding));

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Audit the merge
            await AuditAsync(conn, existingId, "merge", "merge",
                existingContent, merged, null, null, null, null, ct).ConfigureAwait(false);

            _log.LogInformation("Memory merge: updated {ExistingId} — '{Old}' + '{New}' → '{Merged}'",
                existingId,
                existingContent[..Math.Min(40, existingContent.Length)],
                newContent[..Math.Min(40, newContent.Length)],
                merged[..Math.Min(60, merged.Length)]);

            return merged;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Memory merge failed — will insert as new record");
            return null;
        }
    }

    // ContainsNovelSpecifics removed Apr 29, 2026 (Theme E #6) along with the
    // NumberPattern / TimePattern / NamePattern regexes that only it used.
    // See the comment block at the merge site above for reasoning.

    /// <summary>
    /// Cross-type profile correction: when a Perception/Episodic record about the contact
    /// is semantically similar to an existing "About Mark" Semantic memory, merge/update the
    /// profile memory with the newer information. This catches corrections like
    /// "I have hazel eyes" updating "About Mark: Blue eyes" across memory types.
    /// </summary>
    private async Task TryCrossTypeProfileCorrectionAsync(MemoryRecord record, CancellationToken ct)
    {
        if (record.Embedding is null || _ollama is null) return;

        // Defense-in-depth backstop: even if the caller filter in SaveAsync is bypassed
        // in the future, this method must never merge an Ani-speaker record into Profile.
        // See SaveAsync cross-type correction block for the primary guard.
        if (record.Content.StartsWith("I said to",        StringComparison.OrdinalIgnoreCase) ||
            record.Content.StartsWith("I reached out to", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();

            // Only compare against Semantic "About Mark" / "Shared experience" profile memories
            cmd.CommandText = """
                SELECT id, content, embedding FROM memories
                WHERE type = $type
                  AND embedding IS NOT NULL
                  AND (content LIKE 'About %' OR content LIKE 'Shared experience%' OR content LIKE 'Interest:%')
                ORDER BY occurred_at DESC
                LIMIT 100
                """;
            cmd.Parameters.AddWithValue("$type", (int)MemoryType.Semantic);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(2)) continue;

                var existingEmbedding = DeserialisedEmbedding((byte[])reader[2]);
                if (existingEmbedding is null || existingEmbedding.Length != record.Embedding.Length)
                    continue;

                var similarity = CosineSimilarity(record.Embedding, existingEmbedding);

                // Threshold 0.85 matches published Mem0 merge practice (Chhikara et al. 2025).
                // Originally 0.70; raised Apr 12 after cross-type false positive at 0.727
                // corrupted a Profile memory. Cross-type merges are intrinsically risky
                // (different authorship and phrasing conventions), so the threshold must be
                // high enough that only genuine duplicate claims survive.
                if (similarity >= 0.85f)
                {
                    var existingId = reader.GetString(0);
                    var existingContent = reader.GetString(1);

                    _log.LogInformation(
                        "Cross-type correction candidate (cosine={Similarity:F3}): '{Existing}' may be updated by '{New}'",
                        similarity,
                        existingContent[..Math.Min(50, existingContent.Length)],
                        record.Content[..Math.Min(50, record.Content.Length)]);

                    await MergeMemoriesAsync(existingId, existingContent, record.Content, ct)
                        .ConfigureAwait(false);
                    return; // Only correct one profile memory per incoming record
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cross-type profile correction failed — continuing without");
        }
    }

    /// <summary>
    /// Feature 31: Create links to related memories after save (A-MEM-inspired).
    /// Non-blocking — link failure does not prevent the save from succeeding.
    /// </summary>
    private async Task CreateLinksAsync(MemoryRecord record, CancellationToken ct)
    {
        if (record.Embedding is null) return;

        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd  = conn.CreateCommand();

            // Find recent memories that are related but not duplicates
            cmd.CommandText = """
                SELECT id, embedding FROM memories
                WHERE id != $id
                  AND embedding IS NOT NULL
                ORDER BY occurred_at DESC
                LIMIT 20
                """;
            cmd.Parameters.AddWithValue("$id", record.Id.ToString());

            var links = new List<(string targetId, float similarity)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1)) continue;

                var existing = DeserialisedEmbedding((byte[])reader[1]);
                if (existing is null || existing.Length != record.Embedding.Length) continue;

                var similarity = CosineSimilarity(record.Embedding, existing);
                if (similarity is >= 0.5f and < MergeThreshold)
                    links.Add((reader.GetString(0), similarity));
            }

            // Take top 3 by similarity
            foreach (var (targetId, _) in links.OrderByDescending(l => l.similarity).Take(3))
            {
                await using var linkCmd = conn.CreateCommand();
                linkCmd.CommandText = """
                    INSERT OR IGNORE INTO memory_links (source_id, target_id, relationship, created_at)
                    VALUES ($source, $target, 'relates_to', $created)
                    """;
                linkCmd.Parameters.AddWithValue("$source", record.Id.ToString());
                linkCmd.Parameters.AddWithValue("$target", targetId);
                linkCmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                await linkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (links.Count > 0)
                _log.LogDebug("Memory links: created {Count} links for {Id}", Math.Min(links.Count, 3), record.Id);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Memory link creation failed — continuing without links");
        }
    }

    /// <summary>
    /// Reassigns all memory_links referencing oldId to point to survivorId instead.
    /// If reassignment would create a duplicate link (same source+target), deletes
    /// the old link instead. Must be called before deleting any memory record that
    /// might be referenced in memory_links, to prevent FOREIGN KEY constraint failures.
    /// </summary>
    private async Task ReassignMemoryLinksAsync(
        SqliteConnection conn, string oldId, string survivorId, CancellationToken ct)
    {
        if (oldId == survivorId) return;

        // Step 1: Delete links where reassignment would create a self-referencing link
        // (source_id == target_id after reassignment) or a duplicate.
        await using var deleteSelfLinks = conn.CreateCommand();
        deleteSelfLinks.CommandText = """
            DELETE FROM memory_links
            WHERE (source_id = $oldId AND target_id = $survivorId)
               OR (target_id = $oldId AND source_id = $survivorId)
            """;
        deleteSelfLinks.Parameters.AddWithValue("$oldId", oldId);
        deleteSelfLinks.Parameters.AddWithValue("$survivorId", survivorId);
        var selfDeleted = await deleteSelfLinks.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Step 2: Delete links that would become duplicates after reassignment.
        // A duplicate occurs when survivor already has a link to the same target.
        await using var deleteDupSources = conn.CreateCommand();
        deleteDupSources.CommandText = """
            DELETE FROM memory_links
            WHERE source_id = $oldId
              AND target_id IN (
                  SELECT target_id FROM memory_links WHERE source_id = $survivorId
              )
            """;
        deleteDupSources.Parameters.AddWithValue("$oldId", oldId);
        deleteDupSources.Parameters.AddWithValue("$survivorId", survivorId);
        var dupSourcesDeleted = await deleteDupSources.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var deleteDupTargets = conn.CreateCommand();
        deleteDupTargets.CommandText = """
            DELETE FROM memory_links
            WHERE target_id = $oldId
              AND source_id IN (
                  SELECT source_id FROM memory_links WHERE target_id = $survivorId
              )
            """;
        deleteDupTargets.Parameters.AddWithValue("$oldId", oldId);
        deleteDupTargets.Parameters.AddWithValue("$survivorId", survivorId);
        var dupTargetsDeleted = await deleteDupTargets.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Step 3: Reassign remaining links from oldId → survivorId
        await using var reassignSource = conn.CreateCommand();
        reassignSource.CommandText = "UPDATE memory_links SET source_id = $survivorId WHERE source_id = $oldId";
        reassignSource.Parameters.AddWithValue("$oldId", oldId);
        reassignSource.Parameters.AddWithValue("$survivorId", survivorId);
        var sourcesReassigned = await reassignSource.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var reassignTarget = conn.CreateCommand();
        reassignTarget.CommandText = "UPDATE memory_links SET target_id = $survivorId WHERE target_id = $oldId";
        reassignTarget.Parameters.AddWithValue("$oldId", oldId);
        reassignTarget.Parameters.AddWithValue("$survivorId", survivorId);
        var targetsReassigned = await reassignTarget.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var totalChanged = selfDeleted + dupSourcesDeleted + dupTargetsDeleted + sourcesReassigned + targetsReassigned;
        if (totalChanged > 0)
        {
            _log.LogDebug("Memory link reassignment: {OldId} → {SurvivorId} — " +
                "{SelfDel} self-links deleted, {DupDel} duplicates deleted, " +
                "{Reassigned} links reassigned",
                oldId, survivorId, selfDeleted,
                dupSourcesDeleted + dupTargetsDeleted,
                sourcesReassigned + targetsReassigned);
        }
    }

    /// <summary>
    /// Feature 32: Returns the N most recent memories across all types, ordered by occurred_at DESC.
    /// Excludes reflection-sourced memories to prevent reflection loops.
    /// </summary>
    public async Task<IEnumerable<MemoryRecord>> GetRecentAsync(
        int limit = 10, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        // Phase 6 v1.2 (May 17, 2026): also exclude Compressed tier — compressed
        // records are folded into gists and should not be re-surfaced as if
        // they were independent recent memories.
        cmd.CommandText = """
            SELECT * FROM memories
            WHERE (source_name IS NULL OR source_name != 'reflection')
              AND tier != 'Compressed'
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        return await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 6 v1.2 (May 17, 2026) — returns records eligible for compression
    /// into a reflection gist. See <see cref="IMemoryPersistence.GetDecayEligibleAsync"/>
    /// for eligibility criteria. Returns oldest-first so the synthesis batch
    /// covers a coherent older slice of substrate rather than a random
    /// sample.
    ///
    /// 2026-05-17 v1.2 follow-on: reflection-source records are conditionally
    /// allowed — old-shape (pre-v1.2 first-person prose) reflections need
    /// one-time compression to v1.2 register-tagged shape. v1.2-shape
    /// reflections (content starts with "[topic:") remain excluded to prevent
    /// summary-of-summary loops. The distinction is structural: any record
    /// whose content lacks the "[topic:" prefix is treated as a candidate
    /// for compression, regardless of whether the source_name is 'reflection'.
    /// </summary>
    public async Task<IEnumerable<MemoryRecord>> GetDecayEligibleAsync(
        int limit = 10, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        // Filter at SQL level on the attribute-based predicates (tier,
        // importance, character-seed source). The recency predicate +
        // reflection-shape predicate are applied post-load.
        // Over-fetch by 4x to give the recency filter enough headroom —
        // records that pass attribute filters but not recency shouldn't
        // starve the result set.
        var overFetch = Math.Max(50, limit * 4);

        cmd.CommandText = """
            SELECT * FROM memories
            WHERE tier NOT IN ('Anchored', 'Compressed')
              AND (source_name IS NULL OR source_name != 'character-seed')
              AND importance < $importanceCeiling
              AND embedding IS NOT NULL
            ORDER BY occurred_at ASC
            LIMIT $overFetch
            """;
        cmd.Parameters.AddWithValue("$importanceCeiling", _options.DecayEligibilityImportanceThreshold);
        cmd.Parameters.AddWithValue("$overFetch",         overFetch);

        var candidates = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        // Apply type-aware recency threshold + reflection-shape exclusion.
        // - recency = exp(-hoursSinceCreation / lambda), lambda type-aware
        // - reflection-source records ARE eligible UNLESS already v1.2-shape
        //   (content starts with "[topic:"). The 2026-05-17 v1.2 design
        //   protects v1.2 gists from re-compression but allows old-shape
        //   reflections to be folded into v1.2-shape gists one time.
        var now = DateTimeOffset.UtcNow;
        var recencyCeiling = _options.DecayEligibilityRecencyThreshold;
        var eligible = new List<MemoryRecord>();
        foreach (var r in candidates)
        {
            // Skip v1.2-shape reflections (prevent summary-of-summary loops)
            if (r.SourceName == "reflection"
                && r.Content is { Length: > 0 }
                && r.Content.StartsWith("[topic:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lambda = _options.RetrievalRecencyDecayHours * GetDecayMultiplier(r);
            var hours  = (now - r.OccurredAt).TotalHours;
            var recency = (float)Math.Exp(-hours / lambda);
            if (recency <= recencyCeiling)
            {
                eligible.Add(r);
                if (eligible.Count >= limit) break;
            }
        }

        return eligible;
    }

    /// <summary>
    /// Phase 6 v1.2 (May 17, 2026) — marks records as compressed into a
    /// reflection gist. Updates <c>tier='Compressed'</c> on each source record
    /// and creates <c>compressed_into</c> memory-links from gist → sources for
    /// provenance. Atomic per record; if a single update fails, the others
    /// still apply (log + continue).
    /// </summary>
    public async Task MarkCompressedAsync(
        IEnumerable<Guid> sourceIds, Guid gistId, CancellationToken ct = default)
    {
        var idList = sourceIds.ToList();
        if (idList.Count == 0) return;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx   = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var marked = 0;
        try
        {
            foreach (var sourceId in idList)
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = """
                    UPDATE memories
                    SET tier = 'Compressed'
                    WHERE id = $id AND tier NOT IN ('Anchored', 'Compressed')
                    """;
                updateCmd.Parameters.AddWithValue("$id", sourceId.ToString());
                var rows = await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows > 0) marked++;

                // Provenance: gist --compressed_into--> source
                await using var linkCmd = conn.CreateCommand();
                linkCmd.Transaction = tx;
                linkCmd.CommandText = """
                    INSERT OR IGNORE INTO memory_links
                        (source_id, target_id, relationship, created_at)
                    VALUES ($src, $tgt, 'compressed_into', $now)
                    """;
                linkCmd.Parameters.AddWithValue("$src", gistId.ToString());
                linkCmd.Parameters.AddWithValue("$tgt", sourceId.ToString());
                linkCmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await linkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            _log.LogInformation(
                "Phase 6 v1.2 compression: marked {Count}/{Total} records as Compressed under gist {GistId}",
                marked, idList.Count, gistId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            _log.LogError(ex,
                "MarkCompressedAsync transaction rolled back — no source records marked under gist {GistId}",
                gistId);
            throw;
        }
    }

    /// <summary>
    /// Feature 31: Returns all memories linked to the given IDs (1-hop bidirectional).
    /// </summary>
    public async Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(
        Guid memoryId, string? relationshipType = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var ids = await GetLinkedMemoryIdsAsync(
            new HashSet<string> { memoryId.ToString() }, conn, ct).ConfigureAwait(false);

        var results = new List<MemoryRecord>();
        foreach (var id in ids)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM memories WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            results.AddRange(await ReadRecordsAsync(cmd, ct).ConfigureAwait(false));
        }
        return results;
    }

    private static async Task<List<string>> GetLinkedMemoryIdsAsync(
        HashSet<string> sourceIds, SqliteConnection conn, CancellationToken ct)
    {
        if (sourceIds.Count == 0) return new List<string>();

        // SonarCloud S2077 — parameterise the IN-clause instead of interpolating
        // GUID strings into SQL. The IDs are GUIDs validated upstream so the
        // attack surface was theoretical, but parameterisation is the correct
        // shape regardless and silences the rule.
        var idArray = sourceIds.ToArray();
        var paramNames = new string[idArray.Length];
        for (var i = 0; i < idArray.Length; i++) paramNames[i] = "$id" + i;
        var idListSql = string.Join(",", paramNames);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT target_id FROM memory_links WHERE source_id IN ({idListSql})
            UNION
            SELECT DISTINCT source_id FROM memory_links WHERE target_id IN ({idListSql})
            """;
        for (var i = 0; i < idArray.Length; i++)
            cmd.Parameters.AddWithValue(paramNames[i], idArray[i]);

        var linked = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            linked.Add(reader.GetString(0));

        return linked;
    }

    public async Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT * FROM memories
            WHERE type = $type AND is_resolved = 0
            ORDER BY occurred_at ASC
            """;

        cmd.Parameters.AddWithValue("$type", (int)MemoryType.OpenLoop);

        var records = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        return records.Select(r => new OpenLoop
        {
            Id          = r.Id,
            Description = r.Content,
            Context     = r.RawJson ?? string.Empty,
            Urgency     = r.Importance,
            IsResolved  = r.IsResolved,
            CreatedAt   = r.CreatedAt,
            ResolvedAt  = r.ResolvedAt,
        });
    }

    public async Task ResolveOpenLoopAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE memories
            SET is_resolved = 1, resolved_at = $resolved_at
            WHERE id = $id AND type = $type
            """;

        cmd.Parameters.AddWithValue("$id",          id.ToString());
        cmd.Parameters.AddWithValue("$type",        (int)MemoryType.OpenLoop);
        cmd.Parameters.AddWithValue("$resolved_at", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _log.LogDebug("Resolved open loop {Id}", id);
    }

    /// <summary>
    /// Feature 21: Adjusts a memory's importance by a delta, clamped to [0.0, 1.0].
    /// Positive delta = contact engaged on this topic (boost). Negative = correction (demote).
    /// </summary>
    public async Task AdjustImportanceAsync(Guid id, float delta, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE memories
            SET importance = MIN(1.0, MAX(0.0, importance + $delta))
            WHERE id = $id
            """;

        cmd.Parameters.AddWithValue("$id",    id.ToString());
        cmd.Parameters.AddWithValue("$delta", delta);

        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows > 0)
            _log.LogDebug("Adjusted importance on {Id} by {Delta:+0.0#}", id, delta);
    }

    /// <summary>
    /// Feature 16: Retrieve all anchored (foundation) memories. These are always
    /// included in context snapshots regardless of semantic relevance.
    /// </summary>
    public async Task<IEnumerable<MemoryRecord>> GetAnchoredMemoriesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT * FROM memories
            WHERE tier = 'Anchored'
            ORDER BY occurred_at ASC
            """;

        return await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Epistemic Grounding (Apr 10, 2026): Tier-scoped semantic search. Returns only
    /// memories whose provenance matches the requested tier. Used by prompt builders
    /// to populate the Facts / Episodic / Interior sections from their correct pools.
    ///
    /// Theme P Phase P.4 (May 12, 2026) — <paramref name="minCosine"/> is the
    /// per-call retrieval noise floor. Records whose RAW cosine to the query
    /// embedding falls below this value are excluded BEFORE the top-K cut.
    /// Filtering on raw cosine (not composite) is deliberate: composite can
    /// be inflated by importance/recency on semantically-unrelated records.
    /// Default 0.0f preserves the pre-P.4 behaviour for any unmigrated caller.
    /// </summary>
    public async Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default,
        float minCosine = 0.0f)
    {
        if (_ollama is null)
        {
            _log.LogDebug("Tier search unavailable (no embedding client) — returning empty");
            return Enumerable.Empty<ScoredMemory>();
        }

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _ollama.EmbedAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to embed tier-search query — returning empty");
            return Enumerable.Empty<ScoredMemory>();
        }

        if (queryEmbedding.Length == 0)
            return Enumerable.Empty<ScoredMemory>();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        // Filter at the SQL level — only load candidate rows matching the requested tier.
        // This is more efficient than loading everything and filtering in memory.
        // Phase 6 v1.2: also exclude Compressed records from tier-scoped retrieval.
        cmd.CommandText = "SELECT * FROM memories WHERE embedding IS NOT NULL AND provenance = $tier AND tier != 'Compressed'";
        cmd.Parameters.AddWithValue("$tier", tier.ToString());

        var candidates = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        // Apr 30, 2026 — Facts tier scores on cosine + importance ONLY (no
        // recency). See ComputeRetrievalScore overload comment for the full
        // rationale. Episodic + Interior keep recency in their composite.
        var includeRecency = tier != EpistemicTier.Facts;

        // Theme P P.4 (May 12, 2026) — score every dimensional-match candidate,
        // then apply the raw-cosine noise floor BEFORE the top-K cut. The
        // pre-filter count lets us measure how often the floor activates so
        // the per-consumer thresholds can be tuned against production data.
        var scored = candidates
            .Where(r => r.Embedding is not null && r.Embedding.Length == queryEmbedding.Length)
            .Select(r =>
            {
                var cosine    = CosineSimilarity(queryEmbedding, r.Embedding!);
                var composite = ComputeRetrievalScore(queryEmbedding, r, includeRecency);
                return new ScoredMemory(r, composite, cosine);
            })
            .ToList();

        var passingFloor = minCosine > 0f
            ? scored.Where(s => s.CosineSimilarity >= minCosine).ToList()
            : scored;

        var droppedByFloor = scored.Count - passingFloor.Count;

        var ranked = passingFloor
            .OrderByDescending(x => x.CompositeScore)
            .Take(topK)
            .ToList();

        _log.LogDebug(
            "Tier search ({Tier}): {Candidates} candidates, {Results} results, top composite={TopScore:F3}, top cosine={TopCosine:F3}, includeRecency={IncludeRecency}, min_cosine={MinCosine:F2}, dropped_below_threshold={Dropped}",
            tier, candidates.Count, ranked.Count,
            ranked.Count > 0 ? ranked[0].CompositeScore   : 0f,
            ranked.Count > 0 ? ranked[0].CosineSimilarity : 0f,
            includeRecency, minCosine, droppedByFloor);

        return ranked;
    }

    /// <summary>
    /// Epistemic Grounding (Apr 10, 2026): Non-scored tier retrieval. Returns the N
    /// most recent memories of the requested tier without semantic ranking. Useful
    /// when callers want "the N most recent Interior memories" as voice/mood context
    /// regardless of any specific query.
    /// </summary>
    public async Task<IEnumerable<MemoryRecord>> GetByTierAsync(
        EpistemicTier tier, int limit = 20, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT * FROM memories
            WHERE provenance = $tier
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$tier", tier.ToString());
        cmd.Parameters.AddWithValue("$limit", limit);

        return await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Feature 16: Promote an existing memory to the Anchored tier.
    /// Anchored memories are decay-exempt and always surface in context.
    /// </summary>
    public async Task AnchorMemoryAsync(Guid id, string reason, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE memories
            SET tier = 'Anchored',
                anchor_reason = $reason,
                anchored_at = $anchored_at,
                importance = MAX(importance, 0.9)
            WHERE id = $id
            """;

        cmd.Parameters.AddWithValue("$id",          id.ToString());
        cmd.Parameters.AddWithValue("$reason",      reason);
        cmd.Parameters.AddWithValue("$anchored_at", DateTimeOffset.UtcNow.ToString("O"));

        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows > 0)
            _log.LogInformation("Anchored memory {Id}: {Reason}", id, reason);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Delete links first (foreign key)
        await using var linkCmd = conn.CreateCommand();
        linkCmd.CommandText = "DELETE FROM memory_links WHERE source_id = $id OR target_id = $id";
        linkCmd.Parameters.AddWithValue("$id", id.ToString());
        await linkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Capture content before deletion for audit
        string? contentBefore = null;
        int? typeBefore = null;
        float? importanceBefore = null;
        await using var snapCmd = conn.CreateCommand();
        snapCmd.CommandText = "SELECT content, type, importance FROM memories WHERE id = $id";
        snapCmd.Parameters.AddWithValue("$id", id.ToString());
        await using var snapReader = await snapCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await snapReader.ReadAsync(ct).ConfigureAwait(false))
        {
            contentBefore = snapReader.GetString(0);
            typeBefore = snapReader.GetInt32(1);
            importanceBefore = snapReader.GetFloat(2);
        }
        snapReader.Close();

        // Delete the memory
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (rows > 0)
        {
            await AuditAsync(conn, id.ToString(), "delete", "manual",
                contentBefore, null, typeBefore, null, importanceBefore, null, ct).ConfigureAwait(false);
            _log.LogInformation("Deleted memory \"{Id}\"", id);
        }
    }

    // ── AC5: Confabulation Flags ──────────────────────────────────────────────

    public async Task SaveConfabulationFlagAsync(
        string contactMessage,
        string aniReply,
        string? topicCategory     = null,
        string? notes             = null,
        string? canonicalCategory = null,
        CancellationToken ct      = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO confabulation_flags
                (id, flagged_at, contact_message, ani_reply, topic_category, notes, canonical_category)
            VALUES
                ($id, $flaggedAt, $contactMessage, $aniReply, $topicCategory, $notes, $canonicalCategory)
            """;

        cmd.Parameters.AddWithValue("$id",                Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$flaggedAt",         DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$contactMessage",    contactMessage);
        cmd.Parameters.AddWithValue("$aniReply",          aniReply);
        cmd.Parameters.AddWithValue("$topicCategory",     (object?)topicCategory     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$notes",             (object?)notes             ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$canonicalCategory", (object?)canonicalCategory ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        var labelSuffix = string.IsNullOrWhiteSpace(topicCategory) ? "" : $" [{topicCategory}]";
        _log.LogWarning("AC5: Confabulation flag saved{Label} — reply: \"{Reply}\"",
            labelSuffix,
            aniReply.Length > 80 ? aniReply[..80] + "…" : aniReply);
    }

    // ── CharacterState ────────────────────────────────────────────────────────

    public async Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM character_state LIMIT 1";

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(raw))
            return new CharacterStateDoc();

        return JsonSerializer.Deserialize<CharacterStateDoc>(raw, JsonDefaults.CaseInsensitive)
               ?? new CharacterStateDoc();
    }

    public async Task SaveCharacterStateAsync(CharacterStateDoc doc, CancellationToken ct = default)
    {
        doc.LastUpdated = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(doc);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO character_state (id, json) VALUES (1, $json)";
        cmd.Parameters.AddWithValue("$json", json);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── DesireState ───────────────────────────────────────────────────────────

    public async Task<DesireState> GetDesireStateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM desire_state LIMIT 1";

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(raw))
            return new DesireState();

        return JsonSerializer.Deserialize<DesireState>(raw) ?? new DesireState();
    }

    public async Task SaveDesireStateAsync(DesireState state, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(state);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO desire_state (id, json) VALUES (1, $json)";
        cmd.Parameters.AddWithValue("$json", json);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── EmotionalState ─────────────────────────────────────────────────────

    public async Task<EmotionalState> GetEmotionalStateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM emotional_state LIMIT 1";

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(raw))
            return new EmotionalState();

        return JsonSerializer.Deserialize<EmotionalState>(raw) ?? new EmotionalState();
    }

    public async Task SaveEmotionalStateAsync(EmotionalState state, CancellationToken ct = default)
    {
        state.LastUpdated = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(state);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        // Dual-write (primary state + history append) is atomic. Without the
        // transaction, a crash or cancellation between the two writes leaves
        // the history missing one record and the dashboard shows a phantom
        // jump. The two rows represent the same event and must commit together.
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO emotional_state (id, json) VALUES (1, $json)";
        cmd.Parameters.AddWithValue("$json", json);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Append to history table — ~3.5 KB/day at typical cycle frequency.
        // Enables dashboard time-series, drift detection, and research data for the paper.
        await using var historyCmd = conn.CreateCommand();
        historyCmd.Transaction = tx;
        historyCmd.CommandText = """
            INSERT INTO emotional_state_history (warmth, energy, concern, playfulness, contact_gap_tension, recorded_at)
            VALUES ($warmth, $energy, $concern, $playfulness, $tension, $recorded_at)
            """;
        historyCmd.Parameters.AddWithValue("$warmth", state.Warmth);
        historyCmd.Parameters.AddWithValue("$energy", state.Energy);
        historyCmd.Parameters.AddWithValue("$concern", state.Worry);
        historyCmd.Parameters.AddWithValue("$playfulness", state.Playfulness);
        historyCmd.Parameters.AddWithValue("$tension", state.ContactGapTension);
        historyCmd.Parameters.AddWithValue("$recorded_at", state.LastUpdated.ToString("O"));
        await historyCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // ── Emotional Contributions ─────────────────────────────────────────────

    public async Task SaveEmotionalContributionAsync(EmotionalContribution contribution, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO emotional_contributions
                (id, source_content, warmth_delta, energy_delta, concern_delta, playfulness_delta,
                 created_at, half_life_hours, category, embedding, severity, is_outreach_ready, register,
                 ml_emotion, ml_confidence, ml_sarcasm, divergence_score, associative_anchor)
            VALUES ($id, $source, $warmth, $energy, $concern, $playfulness,
                    $created, $halflife, $category, $embedding, $severity, $outreach, $register,
                    $ml_emotion, $ml_confidence, $ml_sarcasm, $divergence, $anchor)
            """;
        cmd.Parameters.AddWithValue("$id", contribution.Id.ToString());
        cmd.Parameters.AddWithValue("$source", contribution.SourceContent);
        cmd.Parameters.AddWithValue("$warmth", contribution.WarmthDelta);
        cmd.Parameters.AddWithValue("$energy", contribution.EnergyDelta);
        cmd.Parameters.AddWithValue("$concern", contribution.WorryDelta);
        cmd.Parameters.AddWithValue("$playfulness", contribution.PlayfulnessDelta);
        cmd.Parameters.AddWithValue("$created", contribution.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$halflife", contribution.HalfLifeHours);
        cmd.Parameters.AddWithValue("$category", contribution.Category.ToString());
        cmd.Parameters.AddWithValue("$embedding", contribution.Embedding is not null
            ? (object)SerialiseEmbedding(contribution.Embedding)!
            : DBNull.Value);
        cmd.Parameters.AddWithValue("$severity", contribution.Severity);
        cmd.Parameters.AddWithValue("$outreach", contribution.IsOutreachReady ? 1 : 0);
        cmd.Parameters.AddWithValue("$register", contribution.Register);
        cmd.Parameters.AddWithValue("$ml_emotion", (object?)contribution.MLEmotion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ml_confidence", (object?)contribution.MLConfidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ml_sarcasm", contribution.MLSarcasmDetected.HasValue ? (contribution.MLSarcasmDetected.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$divergence", (object?)contribution.DivergenceScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$anchor", (object?)contribution.AssociativeAnchor ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<EmotionalContribution>> GetActiveContributionsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM emotional_contributions ORDER BY created_at DESC";

        var results = new List<EmotionalContribution>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var contribution = ReadContribution(reader);
            if (!contribution.IsEffectivelyZero(now))
                results.Add(contribution);
        }
        return results;
    }

    public async Task<List<EmotionalContribution>> GetContributionsSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM emotional_contributions WHERE created_at >= $since ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));

        var results = new List<EmotionalContribution>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadContribution(reader));
        return results;
    }

    public async Task<List<string>> GetProcessedThemesAsync(int maxThemes = 5, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_content, created_at, half_life_hours FROM emotional_contributions ORDER BY created_at DESC";

        var themes = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var source = reader.GetString(0);
            var created = DateTimeOffset.Parse(reader.GetString(1));
            var halfLife = reader.GetFloat(2);
            // ~7 half-lives = effectively zero — these are "processed" themes
            var elapsed = (float)(now - created).TotalHours;
            if (elapsed > halfLife * 7 && themes.Count < maxThemes)
                themes.Add(source);
        }
        return themes;
    }

    public async Task CleanupDecayedContributionsAsync(CancellationToken ct = default)
    {
        // Delete contributions that have decayed below meaningful levels.
        // Use per-category cutoffs based on ~7 half-lives (effectively zero):
        //   Ambient (1h half-life)      → 7h
        //   Conversation (3h half-life) → 21h
        //   Global (12h half-life)      → 84h (3.5 days)
        // This prevents the ".01 × 100" pile-up where many tiny contributions
        // sum to significant values despite each one being near-zero individually.
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        cmd.CommandText = @"DELETE FROM emotional_contributions WHERE
            (category = 'Ambient' AND created_at < $ambientCutoff) OR
            (category = 'Conversation' AND created_at < $convCutoff) OR
            (category = 'Global' AND created_at < $globalCutoff)";
        cmd.Parameters.AddWithValue("$ambientCutoff", now.AddHours(-7).ToString("O"));
        cmd.Parameters.AddWithValue("$convCutoff", now.AddHours(-21).ToString("O"));
        cmd.Parameters.AddWithValue("$globalCutoff", now.AddHours(-84).ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ExpireContributionAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM emotional_contributions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Feature 37: Retroactive memory link building + duplicate merging.
    /// Scans all memories with embeddings, creates relates_to links for
    /// cosine > 0.5 pairs, and logs duplicate clusters for manual review.
    /// Heavy operation — runs once on demand via ///rebuild-links.
    /// </summary>
    public async Task<(int MergeCount, int LinkCount)> RebuildMemoryLinksAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Rebuild: starting retroactive link building...");

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, type, content, embedding FROM memories WHERE embedding IS NOT NULL ORDER BY occurred_at DESC";

        var allMemories = new List<(string Id, int Type, string Content, float[] Embedding)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(3)) continue;
            var emb = DeserialisedEmbedding((byte[])reader[3]);
            if (emb is null) continue;
            allMemories.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), emb));
        }

        _log.LogInformation("Rebuild: loaded {Count} memories with embeddings", allMemories.Count);

        int linkCount = 0;
        int dupCount = 0;
        var existingLinks = new HashSet<string>();

        // Load existing links to avoid duplicates
        await using var linkCheck = conn.CreateCommand();
        linkCheck.CommandText = "SELECT source_id || '|' || target_id FROM memory_links";
        await using var linkReader = await linkCheck.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await linkReader.ReadAsync(ct).ConfigureAwait(false))
            existingLinks.Add(linkReader.GetString(0));

        // Process in batches — compare each memory against subsequent ones
        for (int i = 0; i < allMemories.Count && !ct.IsCancellationRequested; i++)
        {
            var source = allMemories[i];
            int linksForThis = 0;

            // Compare against next 50 memories (bounded scan)
            for (int j = i + 1; j < Math.Min(i + 50, allMemories.Count); j++)
            {
                var target = allMemories[j];
                if (source.Embedding.Length != target.Embedding.Length) continue;

                var similarity = CosineSimilarity(source.Embedding, target.Embedding);

                // Log duplicates (>0.85 same type) for awareness
                if (similarity >= MergeThreshold && source.Type == target.Type)
                {
                    dupCount++;
                    _log.LogDebug("Rebuild: duplicate detected (cosine={Sim:F3}): '{A}' ↔ '{B}'",
                        similarity,
                        source.Content[..Math.Min(40, source.Content.Length)],
                        target.Content[..Math.Min(40, target.Content.Length)]);
                }

                // Create link for related memories (0.5 - 0.85)
                if (similarity is >= 0.5f and < MergeThreshold && linksForThis < 3)
                {
                    var linkKey = $"{source.Id}|{target.Id}";
                    var reverseLinkKey = $"{target.Id}|{source.Id}";
                    if (!existingLinks.Contains(linkKey) && !existingLinks.Contains(reverseLinkKey))
                    {
                        await using var insertLink = conn.CreateCommand();
                        insertLink.CommandText = """
                            INSERT OR IGNORE INTO memory_links (source_id, target_id, relationship, created_at)
                            VALUES ($source, $target, 'relates_to', $created)
                            """;
                        insertLink.Parameters.AddWithValue("$source", source.Id);
                        insertLink.Parameters.AddWithValue("$target", target.Id);
                        insertLink.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                        await insertLink.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                        existingLinks.Add(linkKey);
                        linkCount++;
                        linksForThis++;
                    }
                }
            }

            // Progress logging every 100 memories
            if (i > 0 && i % 100 == 0)
                _log.LogInformation("Rebuild: processed {Count}/{Total} memories — {Links} links created, {Dups} duplicates found",
                    i, allMemories.Count, linkCount, dupCount);
        }

        _log.LogInformation("Rebuild complete: {Links} links created, {Dups} duplicates detected across {Total} memories",
            linkCount, dupCount, allMemories.Count);

        return (dupCount, linkCount);
    }

    public async Task<int> GetLinkCountAsync(CancellationToken ct = default)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_links";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<MemoryLink>> GetAllLinksAsync(CancellationToken ct = default)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_id, target_id, relationship, created_at FROM memory_links";
        var links = new List<MemoryLink>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            links.Add(new MemoryLink
            {
                SourceId = Guid.Parse(reader.GetString(0)),
                TargetId = Guid.Parse(reader.GetString(1)),
                Relationship = reader.GetString(2),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(3)),
            });
        }
        return links;
    }

    public async Task<List<AuditEntry>> GetRecentAuditEntriesAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // SonarCloud S2077 — parameterise the LIMIT clause. The value is a typed
        // int so the actual injection risk was zero, but parameter binding is
        // the correct shape and silences the static-analysis flag.
        cmd.CommandText = "SELECT id, memory_id, action, source, content_before, content_after, type_before, type_after, importance_before, importance_after, occurred_at FROM memory_audit ORDER BY occurred_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var entries = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new AuditEntry
            {
                Id = reader.GetInt64(0),
                MemoryId = reader.GetString(1),
                Action = reader.GetString(2),
                Source = reader.GetString(3),
                ContentBefore = reader.IsDBNull(4) ? null : reader.GetString(4),
                ContentAfter = reader.IsDBNull(5) ? null : reader.GetString(5),
                TypeBefore = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                TypeAfter = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                ImportanceBefore = reader.IsDBNull(8) ? null : reader.GetFloat(8),
                ImportanceAfter = reader.IsDBNull(9) ? null : reader.GetFloat(9),
                OccurredAt = DateTimeOffset.Parse(reader.GetString(10)),
            });
        }
        return entries;
    }

    public async Task<bool> RestoreFromAuditAsync(long auditId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Find the audit entry
        await using var findCmd = conn.CreateCommand();
        findCmd.CommandText = "SELECT memory_id, action, content_before, type_before, importance_before FROM memory_audit WHERE id = $id";
        findCmd.Parameters.AddWithValue("$id", auditId);
        await using var reader = await findCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return false;

        var memoryId = reader.GetString(0);
        var action = reader.GetString(1);
        var contentBefore = reader.IsDBNull(2) ? null : reader.GetString(2);
        var typeBefore = reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3);
        var importanceBefore = reader.IsDBNull(4) ? null : (float?)reader.GetFloat(4);
        reader.Close();

        if (action != "delete" || contentBefore is null)
        {
            _log.LogWarning("Cannot restore audit entry {Id}: action={Action}, has content={HasContent}",
                auditId, action, contentBefore is not null);
            return false;
        }

        // Re-insert the deleted memory
        await using var restoreCmd = conn.CreateCommand();
        restoreCmd.CommandText = """
            INSERT OR IGNORE INTO memories (id, type, content, importance, relational_valence, occurred_at, created_at)
            VALUES ($id, $type, $content, $importance, 0.5, $occurred, $created)
            """;
        restoreCmd.Parameters.AddWithValue("$id", memoryId);
        restoreCmd.Parameters.AddWithValue("$type", typeBefore ?? 4);
        restoreCmd.Parameters.AddWithValue("$content", contentBefore);
        restoreCmd.Parameters.AddWithValue("$importance", importanceBefore ?? 0.3f);
        restoreCmd.Parameters.AddWithValue("$occurred", DateTimeOffset.UtcNow.ToString("O"));
        restoreCmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));

        var rows = await restoreCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows > 0)
        {
            await AuditAsync(conn, memoryId, "create", "restore",
                null, contentBefore, null, typeBefore, null, importanceBefore, ct).ConfigureAwait(false);
            _log.LogInformation("Restored memory {Id} from audit entry {AuditId}", memoryId, auditId);
        }
        return rows > 0;
    }

    private static EmotionalContribution ReadContribution(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var categoryStr = reader.GetString(reader.GetOrdinal("category"));
        Enum.TryParse<ImpactCategory>(categoryStr, out var category);

        var embeddingOrd = reader.GetOrdinal("embedding");
        float[]? embedding = null;
        if (!reader.IsDBNull(embeddingOrd))
        {
            var blob = (byte[])reader.GetValue(embeddingOrd);
            embedding = DeserialisedEmbedding(blob);
        }

        var contribution = new EmotionalContribution
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            SourceContent = reader.GetString(reader.GetOrdinal("source_content")),
            WarmthDelta = reader.GetFloat(reader.GetOrdinal("warmth_delta")),
            EnergyDelta = reader.GetFloat(reader.GetOrdinal("energy_delta")),
            WorryDelta = reader.GetFloat(reader.GetOrdinal("concern_delta")),
            PlayfulnessDelta = reader.GetFloat(reader.GetOrdinal("playfulness_delta")),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            HalfLifeHours = reader.GetFloat(reader.GetOrdinal("half_life_hours")),
            Category = category,
            Embedding = embedding,
            Severity = reader.GetFloat(reader.GetOrdinal("severity")),
            IsOutreachReady = reader.GetInt32(reader.GetOrdinal("is_outreach_ready")) == 1,
            Register = TryGetRegister(reader),
        };

        // ML classification fields — may not exist in older DBs
        try
        {
            var mlOrd = reader.GetOrdinal("ml_emotion");
            if (!reader.IsDBNull(mlOrd)) contribution.MLEmotion = reader.GetString(mlOrd);
            var confOrd = reader.GetOrdinal("ml_confidence");
            if (!reader.IsDBNull(confOrd)) contribution.MLConfidence = reader.GetFloat(confOrd);
            var sarcOrd = reader.GetOrdinal("ml_sarcasm");
            if (!reader.IsDBNull(sarcOrd)) contribution.MLSarcasmDetected = reader.GetInt32(sarcOrd) == 1;
            var divOrd = reader.GetOrdinal("divergence_score");
            if (!reader.IsDBNull(divOrd)) contribution.DivergenceScore = reader.GetFloat(divOrd);
            var anchorOrd = reader.GetOrdinal("associative_anchor");
            if (!reader.IsDBNull(anchorOrd)) contribution.AssociativeAnchor = reader.GetString(anchorOrd);
        }
        catch { /* columns may not exist in older DBs — safe to ignore */ }

        return contribution;
    }

    private static string TryGetRegister(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        try
        {
            var ord = reader.GetOrdinal("register");
            return reader.IsDBNull(ord) ? "Wistful" : reader.GetString(ord);
        }
        catch { return "Wistful"; } // column may not exist in older DBs
    }

    // ── Feature 4: Relationship health ────────────────────────────────────────

    public async Task<RelationshipHealth> GetRelationshipHealthAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM relationship_health LIMIT 1";

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(raw))
            return new RelationshipHealth();

        return JsonSerializer.Deserialize<RelationshipHealth>(raw) ?? new RelationshipHealth();
    }

    public async Task SaveRelationshipHealthAsync(RelationshipHealth health, CancellationToken ct = default)
    {
        health.LastCalculated = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(health);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO relationship_health (id, json) VALUES (1, $json)";
        cmd.Parameters.AddWithValue("$json", json);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<EmotionalStateSnapshot>> GetEmotionalHistoryAsync(int hours, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT warmth, energy, concern, playfulness, contact_gap_tension, recorded_at
            FROM emotional_state_history
            WHERE recorded_at > $cutoff
            ORDER BY recorded_at ASC
            """;
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddHours(-hours).ToString("O"));

        var results = new List<EmotionalStateSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new EmotionalStateSnapshot(
                reader.GetFloat(0), reader.GetFloat(1), reader.GetFloat(2), reader.GetFloat(3),
                reader.GetFloat(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }
        return results;
    }

    public async Task<int> GetRecentMessageCountAsync(int days, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM memories
            WHERE type = $type
              AND content LIKE 'Conversation (%'
              AND occurred_at > $cutoff
            """;
        cmd.Parameters.AddWithValue("$type", (int)MemoryType.Episodic);
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToString("O"));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    public async Task<float> GetAverageConversationValenceAsync(int days, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT AVG(relational_valence) FROM memories
            WHERE type = $type
              AND content LIKE 'Conversation (%'
              AND occurred_at > $cutoff
            """;
        cmd.Parameters.AddWithValue("$type", (int)MemoryType.Episodic);
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToString("O"));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is DBNull or null ? 0.5f : Convert.ToSingle(result);
    }

    public async Task<(int outreach, int inbound)> GetInitiativeBalanceAsync(int days, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Outreach count
        await using var outCmd = conn.CreateCommand();
        outCmd.CommandText = """
            SELECT COUNT(*) FROM memories
            WHERE type = $type
              AND content LIKE '%reached out:%'
              AND occurred_at > $cutoff
            """;
        outCmd.Parameters.AddWithValue("$type", (int)MemoryType.Episodic);
        outCmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToString("O"));
        var outreach = Convert.ToInt32(await outCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

        // Inbound count (conversations that the contact initiated)
        await using var inCmd = conn.CreateCommand();
        inCmd.CommandText = """
            SELECT COUNT(*) FROM memories
            WHERE type = $type
              AND content LIKE 'Conversation (%'
              AND occurred_at > $cutoff
            """;
        inCmd.Parameters.AddWithValue("$type", (int)MemoryType.Episodic);
        inCmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToString("O"));
        var inbound = Convert.ToInt32(await inCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

        return (outreach, inbound);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private void InitialiseSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // WAL mode: readers don't block writers and vice versa
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memories (
                id           TEXT PRIMARY KEY,
                type         INTEGER NOT NULL,
                content      TEXT    NOT NULL,
                raw_json     TEXT,
                importance       REAL    NOT NULL DEFAULT 0,
                relational_valence  REAL    NOT NULL DEFAULT 0,
                embedding    BLOB,
                is_resolved  INTEGER NOT NULL DEFAULT 0,
                source_name  TEXT,
                occurred_at  TEXT    NOT NULL,
                created_at   TEXT    NOT NULL,
                resolved_at  TEXT,
                tier         TEXT    NOT NULL DEFAULT 'Standard',
                anchor_reason TEXT,
                anchored_at  TEXT
            );

            CREATE TABLE IF NOT EXISTS character_state (
                id   INTEGER PRIMARY KEY,
                json TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS desire_state (
                id   INTEGER PRIMARY KEY,
                json TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS emotional_state (
                id   INTEGER PRIMARY KEY,
                json TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS relationship_health (
                id   INTEGER PRIMARY KEY,
                json TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS emotional_state_history (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                warmth              REAL NOT NULL,
                energy              REAL NOT NULL,
                concern             REAL NOT NULL,
                playfulness         REAL NOT NULL,
                contact_gap_tension REAL NOT NULL DEFAULT 0,
                recorded_at         TEXT NOT NULL
            );

            -- Feature 15: Memory contradiction flagging
            CREATE TABLE IF NOT EXISTS memory_contradictions (
                new_memory_id       TEXT NOT NULL,
                existing_memory_id  TEXT NOT NULL,
                reason              TEXT NOT NULL,
                similarity          REAL NOT NULL,
                flagged_at          TEXT NOT NULL,
                is_resolved         INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (new_memory_id, existing_memory_id)
            );

            -- Emotional contributions — per-thought decay model
            CREATE TABLE IF NOT EXISTS emotional_contributions (
                id              TEXT PRIMARY KEY,
                source_content  TEXT NOT NULL,
                warmth_delta    REAL NOT NULL,
                energy_delta    REAL NOT NULL,
                concern_delta   REAL NOT NULL,
                playfulness_delta REAL NOT NULL,
                created_at      TEXT NOT NULL,
                half_life_hours REAL NOT NULL,
                category        TEXT NOT NULL,
                embedding       BLOB,
                severity        REAL NOT NULL DEFAULT 1.0,
                is_outreach_ready INTEGER NOT NULL DEFAULT 0
            );

            -- AC5: Confabulation feedback — stores flagged responses for pattern analysis.
            -- `topic_category` is the freeform researcher note from Mark's ///tag command
            -- (e.g. "confabulation", "temporal confusion", "training artifact").
            -- `canonical_category` is the taxonomized research-analysis label, assigned
            -- later during analysis. Values follow the scheme documented at
            -- docs/research/tag-canonical-mapping.md (Apr 22, 2026):
            --   type-1-creative-elaboration .. type-9-fabricated-source (the 9 confab
            --   types from the blog post / Paper 2 §5.7), pipeline-repetition,
            --   pipeline-truncation, temporal-confusion, register-observation,
            --   composite-multi-type. Null means unclassified.
            CREATE TABLE IF NOT EXISTS confabulation_flags (
                id                 TEXT PRIMARY KEY,
                flagged_at         TEXT NOT NULL,
                contact_message    TEXT NOT NULL,
                ani_reply          TEXT NOT NULL,
                topic_category     TEXT,
                notes              TEXT,
                canonical_category TEXT
            );

            -- Feature 31: Linked memory graph (A-MEM-inspired)
            CREATE TABLE IF NOT EXISTS memory_links (
                source_id    TEXT NOT NULL,
                target_id    TEXT NOT NULL,
                relationship TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                PRIMARY KEY (source_id, target_id, relationship),
                FOREIGN KEY (source_id) REFERENCES memories(id),
                FOREIGN KEY (target_id) REFERENCES memories(id)
            );

            CREATE INDEX IF NOT EXISTS ix_memory_links_source ON memory_links (source_id);
            CREATE INDEX IF NOT EXISTS ix_memory_links_target ON memory_links (target_id);

            CREATE INDEX IF NOT EXISTS ix_confab_flags_time ON confabulation_flags (flagged_at DESC);
            CREATE INDEX IF NOT EXISTS ix_memories_type ON memories (type);
            CREATE INDEX IF NOT EXISTS ix_memories_occurred ON memories (occurred_at DESC);
            CREATE INDEX IF NOT EXISTS ix_emotional_history_time ON emotional_state_history (recorded_at DESC);
            CREATE INDEX IF NOT EXISTS ix_contributions_created ON emotional_contributions (created_at DESC);

            -- Memory audit log: tracks every create, update, delete for rollback capability.
            -- Added April 5, 2026 after auto-corrector deleted 128 valid memories with no recovery.
            CREATE TABLE IF NOT EXISTS memory_audit (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                memory_id       TEXT NOT NULL,
                action          TEXT NOT NULL,       -- 'create', 'update', 'delete', 'merge'
                source          TEXT NOT NULL,       -- 'cognitive-cycle', 'auto-corrector', 'merge', 'manual', 'import'
                content_before  TEXT,                -- full content before change (null for create)
                content_after   TEXT,                -- full content after change (null for delete)
                type_before     INTEGER,             -- memory type before
                type_after      INTEGER,             -- memory type after
                importance_before REAL,
                importance_after  REAL,
                occurred_at     TEXT NOT NULL         -- when the change happened
            );
            CREATE INDEX IF NOT EXISTS ix_audit_memory ON memory_audit (memory_id);
            CREATE INDEX IF NOT EXISTS ix_audit_time ON memory_audit (occurred_at DESC);
            CREATE INDEX IF NOT EXISTS ix_audit_action ON memory_audit (action);
            """;

        cmd.ExecuteNonQuery();

        // Migration: rename mark_valence → relational_valence for existing databases
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA table_info(memories)";
        using var reader = pragmaCmd.ExecuteReader();
        var hasOldColumn = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "mark_valence")
            {
                hasOldColumn = true;
                break;
            }
        }
        reader.Close();

        if (hasOldColumn)
        {
            using var renameCmd = conn.CreateCommand();
            renameCmd.CommandText = "ALTER TABLE memories RENAME COLUMN mark_valence TO relational_valence";
            renameCmd.ExecuteNonQuery();
        }

        // Migration: add Feature 16 anchored memory tier columns if missing
        using var pragmaCmd2 = conn.CreateCommand();
        pragmaCmd2.CommandText = "PRAGMA table_info(memories)";
        using var reader2 = pragmaCmd2.ExecuteReader();
        var hasTierColumn = false;
        while (reader2.Read())
        {
            if (reader2.GetString(1) == "tier")
            {
                hasTierColumn = true;
                break;
            }
        }
        reader2.Close();

        if (!hasTierColumn)
        {
            using var addTier = conn.CreateCommand();
            addTier.CommandText = "ALTER TABLE memories ADD COLUMN tier TEXT NOT NULL DEFAULT 'Standard'";
            addTier.ExecuteNonQuery();

            using var addReason = conn.CreateCommand();
            addReason.CommandText = "ALTER TABLE memories ADD COLUMN anchor_reason TEXT";
            addReason.ExecuteNonQuery();

            using var addAt = conn.CreateCommand();
            addAt.CommandText = "ALTER TABLE memories ADD COLUMN anchored_at TEXT";
            addAt.ExecuteNonQuery();
        }

        // Migration: Epistemic Grounding (Apr 10, 2026) — add provenance column for tier separation.
        // Default 'Episodic' is the safest default for un-backfilled rows; BackfillProvenanceAsync
        // sets the correct tier based on source_name heuristics.
        // See docs/spec/design/ANI-Epistemic-Grounding-Architecture.md
        using var pragmaProv = conn.CreateCommand();
        pragmaProv.CommandText = "PRAGMA table_info(memories)";
        using var readerProv = pragmaProv.ExecuteReader();
        var hasProvenanceColumn = false;
        while (readerProv.Read())
        {
            if (readerProv.GetString(1) == "provenance")
            {
                hasProvenanceColumn = true;
                break;
            }
        }
        readerProv.Close();

        if (!hasProvenanceColumn)
        {
            using var addProv = conn.CreateCommand();
            addProv.CommandText = "ALTER TABLE memories ADD COLUMN provenance TEXT NOT NULL DEFAULT 'Episodic'";
            addProv.ExecuteNonQuery();

            // Backfill provenance for existing rows using the heuristic. We do this inline
            // on the migration path so pre-existing memories get their correct tier the
            // moment the column is added, rather than waiting for a separate backfill pass.
            // The heuristic is deterministic and idempotent, so re-running it is safe.
            BackfillProvenance(conn);
        }

        // Migration: Feature 17 — add contact_gap_tension column to emotional_state_history
        using var pragmaCmd3 = conn.CreateCommand();
        pragmaCmd3.CommandText = "PRAGMA table_info(emotional_state_history)";
        using var reader3 = pragmaCmd3.ExecuteReader();
        var hasTensionColumn = false;
        while (reader3.Read())
        {
            if (reader3.GetString(1) == "contact_gap_tension")
            {
                hasTensionColumn = true;
                break;
            }
        }
        reader3.Close();

        if (!hasTensionColumn)
        {
            using var addTension = conn.CreateCommand();
            addTension.CommandText = "ALTER TABLE emotional_state_history ADD COLUMN contact_gap_tension REAL NOT NULL DEFAULT 0";
            addTension.ExecuteNonQuery();
        }

        // Migration: Phase 1b — add severity and is_outreach_ready columns to emotional_contributions
        using var pragmaCmd4 = conn.CreateCommand();
        pragmaCmd4.CommandText = "PRAGMA table_info(emotional_contributions)";
        using var reader4 = pragmaCmd4.ExecuteReader();
        var hasSeverityColumn = false;
        while (reader4.Read())
        {
            if (reader4.GetString(1) == "severity")
            {
                hasSeverityColumn = true;
                break;
            }
        }
        reader4.Close();

        if (!hasSeverityColumn)
        {
            using var addSeverity = conn.CreateCommand();
            addSeverity.CommandText = "ALTER TABLE emotional_contributions ADD COLUMN severity REAL NOT NULL DEFAULT 1.0";
            addSeverity.ExecuteNonQuery();

            using var addOutreach = conn.CreateCommand();
            addOutreach.CommandText = "ALTER TABLE emotional_contributions ADD COLUMN is_outreach_ready INTEGER NOT NULL DEFAULT 0";
            addOutreach.ExecuteNonQuery();
        }

        // Migration: add register column to emotional_contributions
        using var pragmaCmd5 = conn.CreateCommand();
        pragmaCmd5.CommandText = "PRAGMA table_info(emotional_contributions)";
        using var reader5 = pragmaCmd5.ExecuteReader();
        var hasRegisterColumn = false;
        while (reader5.Read())
        {
            if (reader5.GetString(1) == "register")
            {
                hasRegisterColumn = true;
                break;
            }
        }
        reader5.Close();

        if (!hasRegisterColumn)
        {
            using var addRegister = conn.CreateCommand();
            addRegister.CommandText = "ALTER TABLE emotional_contributions ADD COLUMN register TEXT NOT NULL DEFAULT 'Wistful'";
            addRegister.ExecuteNonQuery();
        }

        // Migration: add ML classification columns to emotional_contributions
        using var pragmaCmd6 = conn.CreateCommand();
        pragmaCmd6.CommandText = "PRAGMA table_info(emotional_contributions)";
        using var reader6 = pragmaCmd6.ExecuteReader();
        var hasMLColumns = false;
        while (reader6.Read())
        {
            if (reader6.GetString(1) == "ml_emotion")
            {
                hasMLColumns = true;
                break;
            }
        }
        reader6.Close();

        if (!hasMLColumns)
        {
            using var addMLEmotion = conn.CreateCommand();
            addMLEmotion.CommandText = "ALTER TABLE emotional_contributions ADD COLUMN ml_emotion TEXT";
            addMLEmotion.ExecuteNonQuery();

            using var addMLConfidence = conn.CreateCommand();
            addMLConfidence.CommandText = "ALTER TABLE emotional_contributions ADD COLUMN ml_confidence REAL";
            addMLConfidence.ExecuteNonQuery();

            using var addMLSarcasm = conn.CreateCommand();
            addMLSarcasm.CommandText = "ALTER TABLE emotional_contributions ADD COLUMN ml_sarcasm INTEGER";
            addMLSarcasm.ExecuteNonQuery();

            using var addDivergence = conn.CreateCommand();
            addDivergence.CommandText = "ALTER TABLE emotional_contributions ADD COLUMN divergence_score REAL";
            addDivergence.ExecuteNonQuery();
        }

        // Migration: add associative_anchor column
        using var pragmaCmd7 = conn.CreateCommand();
        pragmaCmd7.CommandText = "PRAGMA table_info(emotional_contributions)";
        using var reader7 = pragmaCmd7.ExecuteReader();
        var hasAnchorColumn = false;
        while (reader7.Read())
        {
            if (reader7.GetString(1) == "associative_anchor")
            {
                hasAnchorColumn = true;
                break;
            }
        }
        reader7.Close();

        if (!hasAnchorColumn)
        {
            using var addAnchor = conn.CreateCommand();
            addAnchor.CommandText = "ALTER TABLE emotional_contributions ADD COLUMN associative_anchor TEXT";
            addAnchor.ExecuteNonQuery();
        }

        // Migration (Apr 22, 2026): add canonical_category column to confabulation_flags.
        // The column is nullable because values are assigned during research analysis,
        // not at ///tag time. See docs/research/tag-canonical-mapping.md for the
        // taxonomy and mapping rules.
        using var pragmaConfab = conn.CreateCommand();
        pragmaConfab.CommandText = "PRAGMA table_info(confabulation_flags)";
        using var readerConfab = pragmaConfab.ExecuteReader();
        var hasCanonicalCategoryColumn = false;
        while (readerConfab.Read())
        {
            if (readerConfab.GetString(1) == "canonical_category")
            {
                hasCanonicalCategoryColumn = true;
                break;
            }
        }
        readerConfab.Close();

        if (!hasCanonicalCategoryColumn)
        {
            using var addCanonical = conn.CreateCommand();
            addCanonical.CommandText = "ALTER TABLE confabulation_flags ADD COLUMN canonical_category TEXT";
            addCanonical.ExecuteNonQuery();
        }

        // One-time orphan sweep: with Foreign Keys=True now enabled on the
        // connection, existing orphaned memory_links rows (links whose source
        // or target no longer exists in memories) would not be caught by the
        // constraint because the constraint only applies to new writes. Clean
        // them out on startup. Safe and idempotent: after the first run,
        // future writes are constraint-checked, so no new orphans accumulate.
        using var orphanCmd = conn.CreateCommand();
        orphanCmd.CommandText = """
            DELETE FROM memory_links
            WHERE source_id NOT IN (SELECT id FROM memories)
               OR target_id NOT IN (SELECT id FROM memories)
            """;
        var orphansRemoved = orphanCmd.ExecuteNonQuery();
        if (orphansRemoved > 0)
        {
            _log.LogInformation(
                "Memory integrity sweep: removed {Count} orphaned memory_links rows on startup",
                orphansRemoved);
        }
    }

    /// <summary>
    /// Epistemic Grounding (Apr 10, 2026): Backfills the provenance column for all
    /// existing memory records based on source_name and type heuristics. Called once
    /// as part of the migration that adds the provenance column. Deterministic and
    /// idempotent — re-running is safe.
    ///
    /// Heuristic is encapsulated in <see cref="ProvenanceBackfill"/>.
    /// </summary>
    private static void BackfillProvenance(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var idsByTier = new Dictionary<EpistemicTier, List<string>>
        {
            [EpistemicTier.Facts]    = new(),
            [EpistemicTier.Episodic] = new(),
            [EpistemicTier.Interior] = new(),
        };

        using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.CommandText = "SELECT id, source_name, type, content FROM memories";
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var sourceName = reader.IsDBNull(1) ? null : reader.GetString(1);
                var type = (MemoryType)reader.GetInt32(2);
                var content = reader.IsDBNull(3) ? null : reader.GetString(3);
                var tier = ProvenanceBackfill.ClassifyProvenance(sourceName, type, content);
                idsByTier[tier].Add(id);
            }
        }

        // One prepared UPDATE per tier, rebinding just the id between calls.
        foreach (var (tier, ids) in idsByTier)
        {
            if (ids.Count == 0) continue;

            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE memories SET provenance = $tier WHERE id = $id";
            var tierParam = updateCmd.Parameters.Add("$tier", Microsoft.Data.Sqlite.SqliteType.Text);
            var idParam = updateCmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            tierParam.Value = tier.ToString();

            foreach (var id in ids)
            {
                idParam.Value = id;
                updateCmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Write an audit log entry for any memory change (create, update, delete, merge).
    /// Captures full content snapshots for rollback capability.
    /// </summary>
    private async Task AuditAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        string memoryId, string action, string source,
        string? contentBefore, string? contentAfter,
        int? typeBefore, int? typeAfter,
        float? importanceBefore, float? importanceAfter,
        CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memory_audit
                    (memory_id, action, source, content_before, content_after,
                     type_before, type_after, importance_before, importance_after, occurred_at)
                VALUES ($memoryId, $action, $source, $contentBefore, $contentAfter,
                        $typeBefore, $typeAfter, $importanceBefore, $importanceAfter, $occurredAt)
                """;
            cmd.Parameters.AddWithValue("$memoryId", memoryId);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$contentBefore", (object?)contentBefore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$contentAfter", (object?)contentAfter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$typeBefore", (object?)typeBefore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$typeAfter", (object?)typeAfter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$importanceBefore", (object?)importanceBefore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$importanceAfter", (object?)importanceAfter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit failure must never block the primary operation, but it must
            // also not be silent. The audit table is the rollback safety net
            // (128-memory loss incident predates its existence). A broken audit
            // with no log makes a future incident undetectable.
            _log.LogWarning(ex, "Audit write failed for memory {MemoryId} action {Action} source {Source}",
                memoryId, action, source);
        }
    }

    private static async Task<List<MemoryRecord>> ReadRecordsAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        var results = new List<MemoryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tierOrdinal = reader.GetOrdinal("tier");
            var decayTier = reader.IsDBNull(tierOrdinal) ? DecayTier.Standard
                : Enum.TryParse<DecayTier>(reader.GetString(tierOrdinal), out var parsedDecay) ? parsedDecay
                : DecayTier.Standard;

            // Epistemic Grounding: provenance column added Apr 10, 2026.
            // Default to Episodic for pre-migration rows read before backfill runs.
            var provenanceOrdinal = HasColumn(reader, "provenance") ? reader.GetOrdinal("provenance") : -1;
            var provenance = provenanceOrdinal < 0 || reader.IsDBNull(provenanceOrdinal)
                ? EpistemicTier.Episodic
                : Enum.TryParse<EpistemicTier>(reader.GetString(provenanceOrdinal), out var parsedProv)
                    ? parsedProv
                    : EpistemicTier.Episodic;

            results.Add(new MemoryRecord
            {
                Id          = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Type        = (MemoryType)reader.GetInt32(reader.GetOrdinal("type")),
                Content     = reader.GetString(reader.GetOrdinal("content")),
                RawJson     = reader.IsDBNull(reader.GetOrdinal("raw_json"))     ? null : reader.GetString(reader.GetOrdinal("raw_json")),
                Importance  = (float)reader.GetDouble(reader.GetOrdinal("importance")),
                RelationalValence = (float)reader.GetDouble(reader.GetOrdinal("relational_valence")),
                Embedding   = reader.IsDBNull(reader.GetOrdinal("embedding"))    ? null : DeserialisedEmbedding((byte[])reader["embedding"]),
                IsResolved  = reader.GetInt32(reader.GetOrdinal("is_resolved")) == 1,
                SourceName  = reader.IsDBNull(reader.GetOrdinal("source_name"))  ? null : reader.GetString(reader.GetOrdinal("source_name")),
                OccurredAt  = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("occurred_at"))),
                CreatedAt   = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                ResolvedAt  = reader.IsDBNull(reader.GetOrdinal("resolved_at"))  ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("resolved_at"))),
                DecayTier   = decayTier,
                Provenance  = provenance,
                AnchorReason = reader.IsDBNull(reader.GetOrdinal("anchor_reason")) ? null : reader.GetString(reader.GetOrdinal("anchor_reason")),
                AnchoredAt  = reader.IsDBNull(reader.GetOrdinal("anchored_at"))  ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("anchored_at"))),
            });
        }

        return results;
    }

    /// <summary>
    /// Checks if a column exists in the current reader's result schema.
    /// Used for backward-compatible reads against pre-migration rows — e.g., reading
    /// a memory row from a database that hasn't yet run the provenance migration.
    /// </summary>
    private static bool HasColumn(System.Data.Common.DbDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task<List<MemoryContradiction>> GetFlaggedContradictionsAsync(
        bool includeResolved = false, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = includeResolved
            ? """
              SELECT mc.new_memory_id, mc.existing_memory_id, mc.reason, mc.similarity,
                     mc.flagged_at, mc.is_resolved, m1.content, m2.content
              FROM memory_contradictions mc
              LEFT JOIN memories m1 ON m1.id = mc.new_memory_id
              LEFT JOIN memories m2 ON m2.id = mc.existing_memory_id
              ORDER BY mc.flagged_at DESC
              """
            : """
              SELECT mc.new_memory_id, mc.existing_memory_id, mc.reason, mc.similarity,
                     mc.flagged_at, mc.is_resolved, m1.content, m2.content
              FROM memory_contradictions mc
              LEFT JOIN memories m1 ON m1.id = mc.new_memory_id
              LEFT JOIN memories m2 ON m2.id = mc.existing_memory_id
              WHERE mc.is_resolved = 0
              ORDER BY mc.flagged_at DESC
              """;

        var results = new List<MemoryContradiction>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new MemoryContradiction
            {
                NewMemoryId = Guid.Parse(reader.GetString(0)),
                ExistingMemoryId = Guid.Parse(reader.GetString(1)),
                Reason = reader.GetString(2),
                Similarity = reader.GetFloat(3),
                FlaggedAt = DateTimeOffset.Parse(reader.GetString(4)),
                IsResolved = reader.GetInt32(5) == 1,
                NewContent = reader.IsDBNull(6) ? "" : reader.GetString(6),
                ExistingContent = reader.IsDBNull(7) ? "" : reader.GetString(7),
            });
        }

        return results;
    }

    public async Task ResolveContradictionAsync(Guid newMemoryId, Guid existingMemoryId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE memory_contradictions
            SET is_resolved = 1
            WHERE new_memory_id = $new_id AND existing_memory_id = $existing_id
            """;
        cmd.Parameters.AddWithValue("$new_id", newMemoryId.ToString());
        cmd.Parameters.AddWithValue("$existing_id", existingMemoryId.ToString());

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static byte[]? SerialiseEmbedding(float[]? embedding)
    {
        if (embedding is null) return null;
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserialisedEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
