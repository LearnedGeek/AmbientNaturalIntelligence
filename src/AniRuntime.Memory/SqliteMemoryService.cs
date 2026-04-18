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
    private static readonly HashSet<MemoryType> DedupableTypes = new()
    {
        MemoryType.InnerThought,
        MemoryType.Perception,
        MemoryType.Semantic,  // Feature 30: profile facts are prime merge candidates
    };

    // Merge quality gate: regex patterns for detecting confabulated specifics
    // in merged output that weren't present in either source.
    private static readonly Regex NumberPattern = new(@"\b\d+\b", RegexOptions.Compiled);
    private static readonly Regex TimePattern = new(@"\b\d{1,2}:\d{2}\b|\b\d{1,2}\s*(am|pm|AM|PM)\b", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new(@"\b[A-Z][a-z]+(?:\s[A-Z][a-z]+)\b", RegexOptions.Compiled);

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

        cmd.CommandText = """
            SELECT * FROM memories
            WHERE type = $type
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;

        cmd.Parameters.AddWithValue("$type",  (int)type);
        cmd.Parameters.AddWithValue("$limit", limit);

        return await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IEnumerable<MemoryRecord>> SearchAsync(
        string query, int topK = 10, CancellationToken ct = default)
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

        cmd.CommandText = "SELECT * FROM memories WHERE embedding IS NOT NULL";

        var candidates = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        var ranked = candidates
            .Where(r => r.Embedding is not null && r.Embedding.Length == queryEmbedding.Length)
            .Select(r => (record: r, score: ComputeRetrievalScore(queryEmbedding, r)))
            .OrderByDescending(x => x.score)
            .Take(topK)
            .ToList();

        if (ranked.Count > 0)
        {
            var top = ranked[0];
            var cosine = CosineSimilarity(queryEmbedding, top.record.Embedding!);
            _log.LogDebug(
                "Semantic search: {Candidates} candidates, top score={Score:F3} (cosine={Cosine:F3}, importance={Importance:F2}, type={Type}): {Content}",
                candidates.Count, top.score, cosine, top.record.Importance, top.record.Type,
                top.record.Content.Length > 80 ? top.record.Content[..80] + "..." : top.record.Content);
        }

        return ranked.Select(x => x.record);
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

        cmd.CommandText = "SELECT * FROM memories WHERE embedding IS NOT NULL";

        var candidates = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        var ranked = candidates
            .Where(r => r.Embedding is not null && r.Embedding.Length == queryEmbedding.Length)
            .Select(r =>
            {
                var cosine = CosineSimilarity(queryEmbedding, r.Embedding!);
                var composite = ComputeRetrievalScore(queryEmbedding, r);
                return new ScoredMemory(r, composite, cosine);
            })
            .OrderByDescending(x => x.CompositeScore)
            .Take(topK)
            .ToList();

        if (ranked.Count > 0)
        {
            var top = ranked[0];
            _log.LogDebug(
                "Scored search: {Candidates} candidates, top composite={Composite:F3} cosine={Cosine:F3} (type={Type}): {Content}",
                candidates.Count, top.CompositeScore, top.CosineSimilarity, top.Record.Type,
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
                        // Small bonus for being linked to a direct match
                        linkedCandidates.Add(new ScoredMemory(linked, composite + 0.05f, cosine));
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

        cmd.CommandText = "SELECT * FROM memories WHERE embedding IS NOT NULL AND type = $type";
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
    /// Feature 20 + Feature 24: Park et al. three-way retrieval scoring with type-aware decay.
    /// score = α×cosine + β×importance + γ×recency_decay
    ///
    /// Recency decay: e^(-t/λ') where t = hours since creation, λ' = base λ × type multiplier.
    /// Feature 24: Episodic/Semantic memories decay slower (2× base λ), Perceptions decay faster (0.5×).
    /// This means a personally relevant episodic memory stays retrievable ~2 weeks while
    /// a routine RSS perception fades after ~3.5 days.
    /// </summary>
    private float ComputeRetrievalScore(float[] queryEmbedding, MemoryRecord record)
    {
        var cosine = CosineSimilarity(queryEmbedding, record.Embedding!);
        var importance = record.Importance;  // already 0.0–1.0

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

            // Quality gate: reject merges that introduce confabulated specifics.
            // If the merged output contains numbers, times, or proper nouns that
            // weren't in either source, the LLM invented them during rewrite.
            // Good drift adds depth. Bad drift adds noise. Gate by quality, not time.
            if (ContainsNovelSpecifics(merged, existingContent, newContent))
            {
                _log.LogDebug("Merge quality gate: rejected — merged content introduces novel specifics not in either source");
                return null;
            }

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

    /// <summary>
    /// Quality gate for merge output. Checks whether the merged content introduces
    /// specific factual claims (numbers, times, proper nouns) that weren't present
    /// in either source. This distinguishes good drift (adding depth/emotional nuance)
    /// from bad drift (confabulating details during rewrite).
    ///
    /// Design principle: don't gate emergence by time, gate it by quality.
    /// </summary>
    private static bool ContainsNovelSpecifics(string merged, string source1, string source2)
    {
        var sources = $"{source1} {source2}";

        // Check for numbers in merged output that aren't in either source
        var mergedNumbers = NumberPattern.Matches(merged).Select(m => m.Value).ToHashSet();
        var sourceNumbers = NumberPattern.Matches(sources).Select(m => m.Value).ToHashSet();
        var novelNumbers = mergedNumbers.Except(sourceNumbers).ToList();
        if (novelNumbers.Count > 0)
            return true;

        // Check for specific times invented during merge
        var mergedTimes = TimePattern.Matches(merged).Select(m => m.Value).ToHashSet();
        var sourceTimes = TimePattern.Matches(sources).Select(m => m.Value).ToHashSet();
        if (mergedTimes.Except(sourceTimes).Any())
            return true;

        // Check for proper nouns (capitalized multi-word names) not in sources
        var mergedNames = NamePattern.Matches(merged).Select(m => m.Value).ToHashSet();
        var sourceNames = NamePattern.Matches(sources).Select(m => m.Value).ToHashSet();
        if (mergedNames.Except(sourceNames).Any())
            return true;

        return false;
    }

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

        cmd.CommandText = """
            SELECT * FROM memories
            WHERE source_name IS NULL OR source_name != 'reflection'
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        return await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);
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

        var idList = string.Join(",", sourceIds.Select(id => $"'{id}'"));
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT target_id FROM memory_links WHERE source_id IN ({idList})
            UNION
            SELECT DISTINCT source_id FROM memory_links WHERE target_id IN ({idList})
            """;

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
    /// </summary>
    public async Task<IEnumerable<ScoredMemory>> SearchByTierAsync(
        string query, EpistemicTier tier, int topK = 5, CancellationToken ct = default)
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
        cmd.CommandText = "SELECT * FROM memories WHERE embedding IS NOT NULL AND provenance = $tier";
        cmd.Parameters.AddWithValue("$tier", tier.ToString());

        var candidates = await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);

        var ranked = candidates
            .Where(r => r.Embedding is not null && r.Embedding.Length == queryEmbedding.Length)
            .Select(r =>
            {
                var cosine = CosineSimilarity(queryEmbedding, r.Embedding!);
                var composite = ComputeRetrievalScore(queryEmbedding, r);
                return new ScoredMemory(r, composite, cosine);
            })
            .OrderByDescending(x => x.CompositeScore)
            .Take(topK)
            .ToList();

        _log.LogDebug(
            "Tier search ({Tier}): {Candidates} candidates, {Results} results, top composite={TopScore:F3}",
            tier, candidates.Count, ranked.Count,
            ranked.Count > 0 ? ranked[0].CompositeScore : 0f);

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

    public async Task SaveConfabulationFlagAsync(string contactMessage, string aniReply, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO confabulation_flags (id, flagged_at, contact_message, ani_reply)
            VALUES ($id, $flaggedAt, $contactMessage, $aniReply)
            """;

        cmd.Parameters.AddWithValue("$id",             Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$flaggedAt",      DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$contactMessage", contactMessage);
        cmd.Parameters.AddWithValue("$aniReply",       aniReply);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _log.LogWarning("AC5: Confabulation flag saved — reply: \"{Reply}\"",
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
        cmd.CommandText = $"SELECT id, memory_id, action, source, content_before, content_after, type_before, type_after, importance_before, importance_after, occurred_at FROM memory_audit ORDER BY occurred_at DESC LIMIT {limit}";

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

            -- AC5: Confabulation feedback — stores flagged responses for pattern analysis
            CREATE TABLE IF NOT EXISTS confabulation_flags (
                id              TEXT PRIMARY KEY,
                flagged_at      TEXT NOT NULL,
                contact_message TEXT NOT NULL,
                ani_reply       TEXT NOT NULL,
                topic_category  TEXT,
                notes           TEXT
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
            selectCmd.CommandText = "SELECT id, source_name, type FROM memories";
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var sourceName = reader.IsDBNull(1) ? null : reader.GetString(1);
                var type = (MemoryType)reader.GetInt32(2);
                var tier = ProvenanceBackfill.ClassifyProvenance(sourceName, type);
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
