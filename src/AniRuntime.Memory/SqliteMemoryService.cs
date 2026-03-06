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
    private readonly ILogger<SqliteMemoryService>    _log;
    // Keeps in-memory databases alive for the lifetime of this service instance.
    // For file-based databases this is unused but harmless.
    private readonly SqliteConnection                _keepAlive;

    public SqliteMemoryService(IOptions<AniOptions> options, ILogger<SqliteMemoryService> log)
    {
        _log = log;
        var dbPath = options.Value.MemoryDbPath;

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

    public async Task SaveAsync(MemoryRecord record, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO memories
                (id, type, content, raw_json, importance, mark_valence, embedding,
                 is_resolved, source_name, occurred_at, created_at, resolved_at)
            VALUES
                ($id, $type, $content, $raw_json, $importance, $mark_valence, $embedding,
                 $is_resolved, $source_name, $occurred_at, $created_at, $resolved_at)
            """;

        cmd.Parameters.AddWithValue("$id",           record.Id.ToString());
        cmd.Parameters.AddWithValue("$type",         (int)record.Type);
        cmd.Parameters.AddWithValue("$content",      record.Content);
        cmd.Parameters.AddWithValue("$raw_json",     (object?)record.RawJson      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$importance",   record.Importance);
        cmd.Parameters.AddWithValue("$mark_valence", record.MarkValence);
        cmd.Parameters.AddWithValue("$embedding",    (object?)SerialiseEmbedding(record.Embedding) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_resolved",  record.IsResolved ? 1 : 0);
        cmd.Parameters.AddWithValue("$source_name",  (object?)record.SourceName   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$occurred_at",  record.OccurredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$created_at",   record.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$resolved_at",  (object?)record.ResolvedAt?.ToString("O") ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        // Phase 1: return all records that have embeddings; caller provides the query text.
        // Full semantic search (embedding the query then cosine similarity) requires
        // an active OllamaClient — that dependency is injected at the service level in Phase 2.
        // For now, return the most recent records with embeddings as a useful approximation.
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT * FROM memories
            WHERE embedding IS NOT NULL
            ORDER BY occurred_at DESC
            LIMIT $limit
            """;

        cmd.Parameters.AddWithValue("$limit", topK);

        return await ReadRecordsAsync(cmd, ct).ConfigureAwait(false);
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

    // ── CharacterState ────────────────────────────────────────────────────────

    public async Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM character_state LIMIT 1";

        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(raw))
            return new CharacterStateDoc();

        return JsonSerializer.Deserialize<CharacterStateDoc>(raw) ?? new CharacterStateDoc();
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
                importance   REAL    NOT NULL DEFAULT 0,
                mark_valence REAL    NOT NULL DEFAULT 0,
                embedding    BLOB,
                is_resolved  INTEGER NOT NULL DEFAULT 0,
                source_name  TEXT,
                occurred_at  TEXT    NOT NULL,
                created_at   TEXT    NOT NULL,
                resolved_at  TEXT
            );

            CREATE TABLE IF NOT EXISTS character_state (
                id   INTEGER PRIMARY KEY,
                json TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS desire_state (
                id   INTEGER PRIMARY KEY,
                json TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_memories_type ON memories (type);
            CREATE INDEX IF NOT EXISTS ix_memories_occurred ON memories (occurred_at DESC);
            """;

        cmd.ExecuteNonQuery();
    }

    private static async Task<List<MemoryRecord>> ReadRecordsAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        var results = new List<MemoryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new MemoryRecord
            {
                Id          = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Type        = (MemoryType)reader.GetInt32(reader.GetOrdinal("type")),
                Content     = reader.GetString(reader.GetOrdinal("content")),
                RawJson     = reader.IsDBNull(reader.GetOrdinal("raw_json"))     ? null : reader.GetString(reader.GetOrdinal("raw_json")),
                Importance  = (float)reader.GetDouble(reader.GetOrdinal("importance")),
                MarkValence = (float)reader.GetDouble(reader.GetOrdinal("mark_valence")),
                Embedding   = reader.IsDBNull(reader.GetOrdinal("embedding"))    ? null : DeserialisedEmbedding((byte[])reader["embedding"]),
                IsResolved  = reader.GetInt32(reader.GetOrdinal("is_resolved")) == 1,
                SourceName  = reader.IsDBNull(reader.GetOrdinal("source_name"))  ? null : reader.GetString(reader.GetOrdinal("source_name")),
                OccurredAt  = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("occurred_at"))),
                CreatedAt   = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                ResolvedAt  = reader.IsDBNull(reader.GetOrdinal("resolved_at"))  ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("resolved_at"))),
            });
        }

        return results;
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
