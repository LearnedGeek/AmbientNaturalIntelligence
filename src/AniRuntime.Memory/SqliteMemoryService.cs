using System.Text.Json;
using AniRuntime.Core;
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

    public SqliteMemoryService(
        IOptions<AniOptions> options,
        ILogger<SqliteMemoryService> log,
        IOllamaClient? ollama = null)
    {
        _log     = log;
        _ollama  = ollama;
        _options = options.Value;
        var dbPath = _options.MemoryDbPath;

        if (dbPath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || !dbPath.Contains(Path.DirectorySeparatorChar)
               && !dbPath.Contains('/') && !dbPath.Contains('\\') && !dbPath.Contains('.'))
        {
            // Named in-memory database (e.g. "ani-test-abc123").
            // Cache=Shared lets subsequent connections see the same database.
            // The keep-alive connection prevents the database from being dropped
            // between operations (in-memory databases live only while at least one
            // connection to them is open).
            _connectionString = $"Data Source={dbPath};Mode=Memory;Cache=Shared";
        }
        else
        {
            // Ensure the data directory exists for file-based databases
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={dbPath}";
        }

        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        InitialiseSchema();
    }

    public void Dispose() => _keepAlive.Dispose();

    // ── Public API ────────────────────────────────────────────────────────────

    // Semantic deduplication threshold — records with cosine similarity above this
    // within the dedup window are considered duplicates and skipped (BUG-011).
    private const float SemanticDedupThreshold = 0.85f;
    private static readonly TimeSpan SemanticDedupWindow = TimeSpan.FromHours(4);

    // Memory types that should be deduped. Episodic events (conversations, outreach)
    // should never be deduped — each one is a distinct event even if content is similar.
    private static readonly HashSet<MemoryType> DedupableTypes = new()
    {
        MemoryType.InnerThought,
        MemoryType.Perception,
    };

    public async Task SaveAsync(MemoryRecord record, CancellationToken ct = default)
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

        // BUG-011: Semantic deduplication — if a semantically near-identical record of the
        // same type was saved recently, skip this insert. Prevents thought loops from
        // polluting memory with dozens of variations on "the shape of silence."
        if (record.Embedding is not null && DedupableTypes.Contains(record.Type))
        {
            if (await IsSemanticallyDuplicateAsync(record, ct).ConfigureAwait(false))
            {
                _log.LogDebug("Semantic dedup: skipping {Type} — too similar to recent memory: {Content}",
                    record.Type, record.Content[..Math.Min(50, record.Content.Length)]);
                return;
            }
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO memories
                (id, type, content, raw_json, importance, relational_valence, embedding,
                 is_resolved, source_name, occurred_at, created_at, resolved_at,
                 tier, anchor_reason, anchored_at)
            VALUES
                ($id, $type, $content, $raw_json, $importance, $relational_valence, $embedding,
                 $is_resolved, $source_name, $occurred_at, $created_at, $resolved_at,
                 $tier, $anchor_reason, $anchored_at)
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
        cmd.Parameters.AddWithValue("$tier",         record.Tier.ToString());
        cmd.Parameters.AddWithValue("$anchor_reason", (object?)record.AnchorReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$anchored_at",  (object?)record.AnchoredAt?.ToString("O") ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _log.LogDebug("Saved {Type} memory: {Content}", record.Type, record.Content[..Math.Min(50, record.Content.Length)]);

        // Feature 15: Post-save contradiction check for factual memory types.
        // Semantic and Episodic memories can contain contradictory facts — check
        // similar existing memories for conflicts. Inner thoughts and perceptions
        // are subjective and don't need contradiction checking.
        if (record.Embedding is not null && record.Type is MemoryType.Semantic or MemoryType.Episodic)
        {
            try
            {
                await CheckForContradictionsAsync(record, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Feature 15: Contradiction check failed — memory saved without flagging");
            }
        }
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
        if (record.Tier == MemoryTier.Anchored)
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
    private async Task<bool> IsSemanticallyDuplicateAsync(MemoryRecord record, CancellationToken ct)
    {
        try
        {
            var cutoff = (record.OccurredAt - SemanticDedupWindow).ToString("O");

            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd  = conn.CreateCommand();

            cmd.CommandText = """
                SELECT embedding FROM memories
                WHERE type = $type
                  AND embedding IS NOT NULL
                  AND occurred_at > $cutoff
                ORDER BY occurred_at DESC
                LIMIT 20
                """;
            cmd.Parameters.AddWithValue("$type", (int)record.Type);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) continue;

                var existingEmbedding = DeserialisedEmbedding((byte[])reader[0]);
                if (existingEmbedding is null || existingEmbedding.Length != record.Embedding!.Length)
                    continue;

                var similarity = CosineSimilarity(record.Embedding!, existingEmbedding);
                if (similarity >= SemanticDedupThreshold)
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Semantic dedup check failed — saving record to be safe");
            return false;
        }
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

    // ── CharacterState ────────────────────────────────────────────────────────

    public async Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM character_state LIMIT 1";

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(raw))
            return new CharacterStateDoc();

        return JsonSerializer.Deserialize<CharacterStateDoc>(raw,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
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

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO emotional_state (id, json) VALUES (1, $json)";
        cmd.Parameters.AddWithValue("$json", json);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Append to history table — ~3.5 KB/day at typical cycle frequency.
        // Enables dashboard time-series, drift detection, and research data for the paper.
        await using var historyCmd = conn.CreateCommand();
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
    }

    // ── Emotional Contributions ─────────────────────────────────────────────

    public async Task SaveEmotionalContributionAsync(EmotionalContribution contribution, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO emotional_contributions
                (id, source_content, warmth_delta, energy_delta, concern_delta, playfulness_delta,
                 created_at, half_life_hours, category, embedding, severity, is_outreach_ready)
            VALUES ($id, $source, $warmth, $energy, $concern, $playfulness,
                    $created, $halflife, $category, $embedding, $severity, $outreach)
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
        // Remove contributions older than 24 hours — well past any decay curve
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM emotional_contributions WHERE created_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddHours(-24).ToString("O"));
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

        return new EmotionalContribution
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
        };
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

            CREATE INDEX IF NOT EXISTS ix_memories_type ON memories (type);
            CREATE INDEX IF NOT EXISTS ix_memories_occurred ON memories (occurred_at DESC);
            CREATE INDEX IF NOT EXISTS ix_emotional_history_time ON emotional_state_history (recorded_at DESC);
            CREATE INDEX IF NOT EXISTS ix_contributions_created ON emotional_contributions (created_at DESC);
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
    }

    private static async Task<List<MemoryRecord>> ReadRecordsAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        var results = new List<MemoryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tierOrdinal = reader.GetOrdinal("tier");
            var tier = reader.IsDBNull(tierOrdinal) ? MemoryTier.Standard
                : Enum.TryParse<MemoryTier>(reader.GetString(tierOrdinal), out var parsed) ? parsed
                : MemoryTier.Standard;

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
                Tier        = tier,
                AnchorReason = reader.IsDBNull(reader.GetOrdinal("anchor_reason")) ? null : reader.GetString(reader.GetOrdinal("anchor_reason")),
                AnchoredAt  = reader.IsDBNull(reader.GetOrdinal("anchored_at"))  ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("anchored_at"))),
            });
        }

        return results;
    }

    /// <summary>
    /// Feature 15: Check if a newly saved memory contradicts existing similar memories.
    /// Uses cosine similarity to find candidates (0.6-0.85) and LLM to evaluate contradiction.
    /// The range 0.6-0.85 targets "same topic, possibly different claims" — above 0.85 is
    /// dedup territory, below 0.6 is likely unrelated content.
    /// </summary>
    private async Task CheckForContradictionsAsync(MemoryRecord newRecord, CancellationToken ct)
    {
        if (_ollama is null || newRecord.Embedding is null)
            return;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT id, content, embedding FROM memories
            WHERE type = $type
              AND id != $id
              AND embedding IS NOT NULL
            ORDER BY occurred_at DESC
            LIMIT 30
            """;
        cmd.Parameters.AddWithValue("$type", (int)newRecord.Type);
        cmd.Parameters.AddWithValue("$id", newRecord.Id.ToString());

        var candidates = new List<(string id, string content, float similarity)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(2)) continue;

            var existingEmbedding = DeserialisedEmbedding((byte[])reader[2]);
            var similarity = CosineSimilarity(newRecord.Embedding, existingEmbedding);

            // Sweet spot: same topic (>0.6) but not a duplicate (<0.85)
            if (similarity is >= 0.6f and < SemanticDedupThreshold)
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), similarity));
            }
        }

        if (candidates.Count == 0) return;

        // Check top 3 most similar for contradiction using LLM
        foreach (var (existingId, existingContent, similarity) in candidates.OrderByDescending(c => c.similarity).Take(3))
        {
            var contradictionReason = await DetectContradictionAsync(
                newRecord.Content, existingContent, ct).ConfigureAwait(false);

            if (contradictionReason is null) continue;

            // Flag the contradiction
            await using var flagConn = await OpenAsync(ct).ConfigureAwait(false);
            await using var flagCmd = flagConn.CreateCommand();
            flagCmd.CommandText = """
                INSERT OR IGNORE INTO memory_contradictions
                    (new_memory_id, existing_memory_id, reason, similarity, flagged_at)
                VALUES ($new_id, $existing_id, $reason, $similarity, $flagged_at)
                """;
            flagCmd.Parameters.AddWithValue("$new_id", newRecord.Id.ToString());
            flagCmd.Parameters.AddWithValue("$existing_id", existingId);
            flagCmd.Parameters.AddWithValue("$reason", contradictionReason);
            flagCmd.Parameters.AddWithValue("$similarity", similarity);
            flagCmd.Parameters.AddWithValue("$flagged_at", DateTimeOffset.UtcNow.ToString("O"));

            await flagCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Feature 15: Contradiction flagged — \"{New}\" vs \"{Existing}\": {Reason}",
                newRecord.Content[..Math.Min(40, newRecord.Content.Length)],
                existingContent[..Math.Min(40, existingContent.Length)],
                contradictionReason);
        }
    }

    /// <summary>
    /// Feature 15: Uses LLM to determine if two semantically similar memories contradict each other.
    /// Returns a brief explanation if they contradict, null if they're consistent.
    /// </summary>
    private async Task<string?> DetectContradictionAsync(
        string newContent, string existingContent, CancellationToken ct)
    {
        if (_ollama is null) return null;

        var system = """
            You compare two memory records for factual contradiction.
            A contradiction means the two records make INCOMPATIBLE FACTUAL CLAIMS about the same topic.

            NOT contradictions (return false):
            - Different messages from the same person at different times (people say different things in different messages)
            - Different people saying different things (that's just conversation)
            - Different topics mentioned in different messages
            - Playful, hypothetical, or imaginative statements (wrestling fantasies, costume plans, jokes)
            - Complementary or elaborating information
            - Emotional expressions that differ in tone or intensity

            TRUE contradictions (return true):
            - "Mark's birthday is March 5" vs "Mark's birthday is June 12"
            - "She has two kids" vs "She has no children"
            - Direct factual claims that cannot both be true

            Respond in JSON: { "contradicts": true/false, "reason": "brief explanation" }
            If they don't contradict, reason can be empty.
            """;

        var user = $"""
            Memory A (newer): "{newContent}"
            Memory B (older): "{existingContent}"

            Do these two memories make contradictory factual claims?
            """;

        var raw = await _ollama.ChatJsonAsync(system, Array.Empty<ChatMessage>(), user, ct)
            .ConfigureAwait(false);

        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(raw.Trim());
            if (doc.RootElement.TryGetProperty("contradicts", out var c) && c.GetBoolean())
            {
                return doc.RootElement.TryGetProperty("reason", out var r)
                    ? r.GetString() ?? "contradiction detected"
                    : "contradiction detected";
            }
        }
        catch
        {
            _log.LogDebug("Feature 15: Contradiction detection parse failure: {Raw}", raw);
        }

        return null;
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
