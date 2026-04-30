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
/// <see cref="SqliteMemoryService"/> and
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
                embedding                   BLOB
            );

            CREATE INDEX IF NOT EXISTS ix_closed_conv_closed_at
                ON closed_conversation_records (closed_at DESC);

            CREATE INDEX IF NOT EXISTS ix_closed_conv_thread_id
                ON closed_conversation_records (thread_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task SaveAsync(ClosedConversationRecord record, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO closed_conversation_records (
                id, thread_id, closed_at, gist, topic_keywords_json,
                mark_register_json, ani_register_json, outcome_signal_seed_json,
                outcome_signal_valence, turn_count, duration_seconds, embedding
            )
            VALUES (
                $id, $thread_id, $closed_at, $gist, $topic_keywords_json,
                $mark_register_json, $ani_register_json, $outcome_signal_seed_json,
                $outcome_signal_valence, $turn_count, $duration_seconds, $embedding
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
                embedding                = excluded.embedding
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

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ClosedConversationRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM closed_conversation_records WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task<ClosedConversationRecord?> GetByThreadIdAsync(Guid threadId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM closed_conversation_records WHERE thread_id = $thread_id ORDER BY closed_at DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$thread_id", threadId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task<IEnumerable<ClosedConversationRecord>> GetRecentAsync(int limit = 10, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM closed_conversation_records ORDER BY closed_at DESC LIMIT $limit";
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
        };
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
