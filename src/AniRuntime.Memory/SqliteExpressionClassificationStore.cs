using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Memory;

/// <summary>
/// Issue #41 (Theme I prerequisite, 2026-05-21) — SQLite-backed
/// <see cref="IExpressionClassificationStore"/>. Same database file as
/// <see cref="SqliteClosedConversationStore"/>; new dedicated table
/// <c>expression_classifications</c>. Schema-migration pattern matches
/// the J.5h <c>validity</c>-column pattern (idempotent
/// <c>CREATE TABLE IF NOT EXISTS</c> + <c>ALTER TABLE</c> retro-add).
///
/// Replaces the pre-#41 JSON file persistence in
/// <c>Research.razor.SaveConversationResultsAsync</c>. The aggregate
/// cross-tab + Cramér's V are now computable from any consumer (dashboard
/// heatmap, paper-figure tooling, future <c>ExpressionClassificationEndpoints</c>)
/// over the same per-row data.
/// </summary>
public class SqliteExpressionClassificationStore : IExpressionClassificationStore, IDisposable
{
    private readonly string                                       _connectionString;
    private readonly ILogger<SqliteExpressionClassificationStore> _log;
    private readonly SqliteConnection                             _keepAlive;

    public SqliteExpressionClassificationStore(
        IOptions<AniOptions> options,
        ILogger<SqliteExpressionClassificationStore> log)
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
            CREATE TABLE IF NOT EXISTS expression_classifications (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                source              TEXT    NOT NULL,
                thread_id           TEXT,
                message_id          INTEGER,
                source_file         TEXT,
                source_index        INTEGER,
                role                TEXT,
                content_hash        TEXT,
                felt_register       TEXT,
                expressed_emotion   TEXT,
                confidence          REAL,
                classified_at       TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_expr_class_classified_at
                ON expression_classifications (classified_at DESC);

            CREATE INDEX IF NOT EXISTS ix_expr_class_source
                ON expression_classifications (source);

            CREATE INDEX IF NOT EXISTS ix_expr_class_thread_id
                ON expression_classifications (thread_id);

            -- Idempotency: source + content_hash dedup. Partial unique index
            -- (only enforced when both fields populated) so legacy nullable
            -- rows don't collide.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_expr_class_source_hash
                ON expression_classifications (source, content_hash)
                WHERE content_hash IS NOT NULL;
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task SaveAsync(ExpressionClassificationRecord record, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expression_classifications (
                source, thread_id, message_id, source_file, source_index,
                role, content_hash, felt_register, expressed_emotion,
                confidence, classified_at
            )
            VALUES (
                $source, $thread_id, $message_id, $source_file, $source_index,
                $role, $content_hash, $felt_register, $expressed_emotion,
                $confidence, $classified_at
            )
            ON CONFLICT (source, content_hash) WHERE content_hash IS NOT NULL DO NOTHING
            """;

        cmd.Parameters.AddWithValue("$source",            record.Source);
        cmd.Parameters.AddWithValue("$thread_id",         (object?)record.ThreadId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$message_id",        (object?)record.MessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source_file",       (object?)record.SourceFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source_index",      (object?)record.SourceIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role",              (object?)record.Role ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$content_hash",      (object?)record.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$felt_register",     (object?)record.FeltRegister ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expressed_emotion", (object?)record.ExpressedEmotion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$confidence",        (object?)record.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$classified_at",     record.ClassifiedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IEnumerable<ExpressionClassificationRecord>> GetRecentAsync(
        int limit = 100, string? source = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = source is null
            ? "SELECT * FROM expression_classifications ORDER BY classified_at DESC LIMIT $limit"
            : "SELECT * FROM expression_classifications WHERE source = $source ORDER BY classified_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        if (source is not null) cmd.Parameters.AddWithValue("$source", source);

        var results = new List<ExpressionClassificationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRecord(reader));
        return results;
    }

    public async Task<IEnumerable<ExpressionClassificationRecord>> GetByThreadIdAsync(
        Guid threadId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM expression_classifications WHERE thread_id = $thread_id ORDER BY classified_at ASC";
        cmd.Parameters.AddWithValue("$thread_id", threadId.ToString());

        var results = new List<ExpressionClassificationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRecord(reader));
        return results;
    }

    private static ExpressionClassificationRecord ReadRecord(SqliteDataReader reader)
    {
        var threadOrd     = reader.GetOrdinal("thread_id");
        var messageOrd    = reader.GetOrdinal("message_id");
        var sourceFileOrd = reader.GetOrdinal("source_file");
        var sourceIdxOrd  = reader.GetOrdinal("source_index");
        var roleOrd       = reader.GetOrdinal("role");
        var hashOrd       = reader.GetOrdinal("content_hash");
        var feltOrd       = reader.GetOrdinal("felt_register");
        var exprOrd       = reader.GetOrdinal("expressed_emotion");
        var confOrd       = reader.GetOrdinal("confidence");

        return new ExpressionClassificationRecord
        {
            Id                = reader.GetInt64(reader.GetOrdinal("id")),
            Source            = reader.GetString(reader.GetOrdinal("source")),
            ThreadId          = reader.IsDBNull(threadOrd)     ? null : Guid.Parse(reader.GetString(threadOrd)),
            MessageId         = reader.IsDBNull(messageOrd)    ? null : reader.GetInt64(messageOrd),
            SourceFile        = reader.IsDBNull(sourceFileOrd) ? null : reader.GetString(sourceFileOrd),
            SourceIndex       = reader.IsDBNull(sourceIdxOrd)  ? null : reader.GetInt32(sourceIdxOrd),
            Role              = reader.IsDBNull(roleOrd)       ? null : reader.GetString(roleOrd),
            ContentHash       = reader.IsDBNull(hashOrd)       ? null : reader.GetString(hashOrd),
            FeltRegister      = reader.IsDBNull(feltOrd)       ? null : reader.GetString(feltOrd),
            ExpressedEmotion  = reader.IsDBNull(exprOrd)       ? null : reader.GetString(exprOrd),
            Confidence        = reader.IsDBNull(confOrd)       ? null : (float)reader.GetDouble(confOrd),
            ClassifiedAt      = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("classified_at"))),
        };
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }
}
