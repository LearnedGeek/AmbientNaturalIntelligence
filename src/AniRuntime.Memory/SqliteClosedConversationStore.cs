using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Memory;

/// <summary>
/// Vibe Loop V1 (Apr 29, 2026) — SQLite-backed
/// <see cref="IClosedConversationStore"/>. Same database file as
/// <c>SqliteMemoryService</c> (deleted 2026-05-18) and
/// <see cref="SqliteConversationService"/>; new dedicated table
/// <c>closed_conversation_records</c> per V1.0's locked storage decision.
///
/// Schema initialisation is idempotent (CREATE TABLE IF NOT EXISTS); the
/// constructor opens a keep-alive connection so the in-memory shared-cache
/// SQLite mode used by tests retains state across the test method.
/// </summary>
public class SqliteClosedConversationStore : IClosedConversationStore, IDisposable
{
    private readonly string                                   _connectionString;
    private readonly ILogger<SqliteClosedConversationStore>   _log;
    private readonly SqliteConnection                         _keepAlive;

    public SqliteClosedConversationStore(
        IOptions<AniOptions> options,
        ILogger<SqliteClosedConversationStore> log)
    {
        _log = log;

        var dbPath = options.Value.MemoryDbPath;

        if (dbPath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || !dbPath.Contains(Path.DirectorySeparatorChar)
               && !dbPath.Contains('/') && !dbPath.Contains('\\') && !dbPath.Contains('.'))
        {
            _connectionString = $"Data Source={dbPath};Mode=Memory;Cache=Shared";
        }
        else
        {
            _connectionString = $"Data Source={dbPath}";
        }

        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        InitialiseSchema();
    }

    public void Dispose() => _keepAlive.Dispose();

    private void InitialiseSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS closed_conversation_records (
                id                          TEXT PRIMARY KEY,
                thread_id                   TEXT NOT NULL,
                closed_at                   TEXT NOT NULL,
                gist                        TEXT NOT NULL,
                topic_keywords_json         TEXT NOT NULL,
                mark_register_json          TEXT NOT NULL,
                ani_register_json           TEXT NOT NULL,
                outcome_signal_seed_json    TEXT NOT NULL,
                outcome_signal_valence      REAL NOT NULL,
                turn_count                  INTEGER NOT NULL,
                duration_seconds            REAL NOT NULL,
                embedding                   BLOB,
                validity                    TEXT NOT NULL DEFAULT 'valid'
            );

            CREATE INDEX IF NOT EXISTS ix_closed_conv_closed_at
                ON closed_conversation_records (closed_at DESC);

            CREATE INDEX IF NOT EXISTS ix_closed_conv_thread_id
                ON closed_conversation_records (thread_id);
            """;
        cmd.ExecuteNonQuery();

        // Theme J J.5h-data (Issue #47, 2026-05-21): retro-add the validity
        // column on databases that pre-date it. The CREATE TABLE IF NOT EXISTS
        // above won't add the column when the table already exists; the
        // ALTER TABLE below covers that case. Mirrors the Feature 38
        // emergence_log pattern.
        try
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText =
                "ALTER TABLE closed_conversation_records ADD COLUMN validity TEXT NOT NULL DEFAULT 'valid'";
            alterCmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* Column already exists — safe to ignore */ }

        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText =
            "CREATE INDEX IF NOT EXISTS ix_closed_conv_validity ON closed_conversation_records (validity)";
        idxCmd.ExecuteNonQuery();
    }

    public async Task SaveAsync(ClosedConversationRecord record, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO closed_conversation_records (
                id, thread_id, closed_at, gist, topic_keywords_json,
                mark_register_json, ani_register_json, outcome_signal_seed_json,
                outcome_signal_valence, turn_count, duration_seconds, embedding,
                validity
            )
            VALUES (
                $id, $thread_id, $closed_at, $gist, $topic_keywords_json,
                $mark_register_json, $ani_register_json, $outcome_signal_seed_json,
                $outcome_signal_valence, $turn_count, $duration_seconds, $embedding,
                $validity
            )
            ON CONFLICT(id) DO UPDATE SET
                thread_id                = excluded.thread_id,
                closed_at                = excluded.closed_at,
                gist                     = excluded.gist,
                topic_keywords_json      = excluded.topic_keywords_json,
                mark_register_json       = excluded.mark_register_json,
                ani_register_json        = excluded.ani_register_json,
                outcome_signal_seed_json = excluded.outcome_signal_seed_json,
                outcome_signal_valence   = excluded.outcome_signal_valence,
                turn_count               = excluded.turn_count,
                duration_seconds         = excluded.duration_seconds,
                embedding                = excluded.embedding,
                validity                 = excluded.validity
            """;

        cmd.Parameters.AddWithValue("$id",                       record.Id.ToString());
        cmd.Parameters.AddWithValue("$thread_id",                record.ThreadId.ToString());
        cmd.Parameters.AddWithValue("$closed_at",                record.ClosedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$gist",                     record.Gist);
        cmd.Parameters.AddWithValue("$topic_keywords_json",      JsonSerializer.Serialize(record.TopicKeywords, JsonDefaults.CamelCase));
        cmd.Parameters.AddWithValue("$mark_register_json",       JsonSerializer.Serialize(record.MarkRegister, JsonDefaults.CamelCase));
        cmd.Parameters.AddWithValue("$ani_register_json",        JsonSerializer.Serialize(record.AniRegister, JsonDefaults.CamelCase));
        cmd.Parameters.AddWithValue("$outcome_signal_seed_json", JsonSerializer.Serialize(record.OutcomeSignalSeedVector, JsonDefaults.CamelCase));
        cmd.Parameters.AddWithValue("$outcome_signal_valence",   record.OutcomeSignalValence);
        cmd.Parameters.AddWithValue("$turn_count",               record.TurnCount);
        cmd.Parameters.AddWithValue("$duration_seconds",         record.DurationSeconds);
        cmd.Parameters.AddWithValue("$embedding",                (object?)SerialiseEmbedding(record.Embedding) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$validity",                 string.IsNullOrWhiteSpace(record.Validity) ? "valid" : record.Validity);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<ClosedConversationRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => GetByIdCoreAsync(id, includeInvalid: false, ct);

    public Task<ClosedConversationRecord?> GetByIdIncludingInvalidAsync(Guid id, CancellationToken ct = default)
        => GetByIdCoreAsync(id, includeInvalid: true, ct);

    private async Task<ClosedConversationRecord?> GetByIdCoreAsync(Guid id, bool includeInvalid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = includeInvalid
            ? "SELECT * FROM closed_conversation_records WHERE id = $id LIMIT 1"
            : "SELECT * FROM closed_conversation_records WHERE id = $id AND validity = 'valid' LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public Task<ClosedConversationRecord?> GetByThreadIdAsync(Guid threadId, CancellationToken ct = default)
        => GetByThreadIdCoreAsync(threadId, includeInvalid: false, ct);

    public Task<ClosedConversationRecord?> GetByThreadIdIncludingInvalidAsync(Guid threadId, CancellationToken ct = default)
        => GetByThreadIdCoreAsync(threadId, includeInvalid: true, ct);

    private async Task<ClosedConversationRecord?> GetByThreadIdCoreAsync(Guid threadId, bool includeInvalid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = includeInvalid
            ? "SELECT * FROM closed_conversation_records WHERE thread_id = $thread_id ORDER BY closed_at DESC LIMIT 1"
            : "SELECT * FROM closed_conversation_records WHERE thread_id = $thread_id AND validity = 'valid' ORDER BY closed_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$thread_id", threadId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public Task<IEnumerable<ClosedConversationRecord>> GetRecentAsync(int limit = 10, CancellationToken ct = default)
        => GetRecentCoreAsync(limit, includeInvalid: false, ct);

    public Task<IEnumerable<ClosedConversationRecord>> GetRecentIncludingInvalidAsync(int limit = 10, CancellationToken ct = default)
        => GetRecentCoreAsync(limit, includeInvalid: true, ct);

    private async Task<IEnumerable<ClosedConversationRecord>> GetRecentCoreAsync(int limit, bool includeInvalid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = includeInvalid
            ? "SELECT * FROM closed_conversation_records ORDER BY closed_at DESC LIMIT $limit"
            : "SELECT * FROM closed_conversation_records WHERE validity = 'valid' ORDER BY closed_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<ClosedConversationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRecord(reader));
        return results;
    }

    private static ClosedConversationRecord ReadRecord(SqliteDataReader reader)
    {
        var topicJson    = reader.GetString(reader.GetOrdinal("topic_keywords_json"));
        var markJson     = reader.GetString(reader.GetOrdinal("mark_register_json"));
        var aniJson      = reader.GetString(reader.GetOrdinal("ani_register_json"));
        var outcomeJson  = reader.GetString(reader.GetOrdinal("outcome_signal_seed_json"));
        var embeddingOrd = reader.GetOrdinal("embedding");

        return new ClosedConversationRecord
        {
            Id                      = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            ThreadId                = Guid.Parse(reader.GetString(reader.GetOrdinal("thread_id"))),
            ClosedAt                = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("closed_at"))),
            Gist                    = reader.GetString(reader.GetOrdinal("gist")),
            TopicKeywords           = JsonSerializer.Deserialize<List<string>>(topicJson) ?? new(),
            MarkRegister            = JsonSerializer.Deserialize<Dictionary<string, float>>(markJson) ?? new(),
            AniRegister             = JsonSerializer.Deserialize<Dictionary<string, float>>(aniJson) ?? new(),
            OutcomeSignalSeedVector = JsonSerializer.Deserialize<Dictionary<string, float>>(outcomeJson) ?? new(),
            OutcomeSignalValence    = (float)reader.GetDouble(reader.GetOrdinal("outcome_signal_valence")),
            TurnCount               = reader.GetInt32(reader.GetOrdinal("turn_count")),
            DurationSeconds         = reader.GetDouble(reader.GetOrdinal("duration_seconds")),
            Embedding               = reader.IsDBNull(embeddingOrd) ? null : DeserialiseEmbedding((byte[])reader.GetValue(embeddingOrd)),
            Validity                = ReadValidity(reader),
        };
    }

    /// <summary>
    /// Read <c>validity</c> defensively. The column exists on all writes
    /// post-J.5h-data, but if a reader is invoked against a database that
    /// hasn't yet had the ALTER TABLE run (e.g., older snapshots opened
    /// for forensic comparison), the column may be absent — default to
    /// <c>"valid"</c> rather than throwing.
    /// </summary>
    private static string ReadValidity(SqliteDataReader reader)
    {
        try
        {
            var ord = reader.GetOrdinal("validity");
            return reader.IsDBNull(ord) ? "valid" : reader.GetString(ord);
        }
        catch (IndexOutOfRangeException)
        {
            return "valid";
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static byte[]? SerialiseEmbedding(float[]? embedding)
    {
        if (embedding is null) return null;
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserialiseEmbedding(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
