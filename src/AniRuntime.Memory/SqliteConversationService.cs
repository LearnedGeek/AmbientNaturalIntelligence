using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Utilities;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Memory;

/// <summary>
/// SQLite-backed IConversationService. Uses the same database as SqliteMemoryService.
///
/// Tables:
///   - conversation_threads  — one row per conversation thread
///   - conversation_messages — one row per message, FK to thread
///
/// When a thread is closed, the full exchange is saved as a single episodic
/// memory record via IMemoryService, giving semantic search access to past conversations.
/// </summary>
public class SqliteConversationService : IConversationService, IDisposable
{
    private readonly string                                _connectionString;
    private readonly IMemoryService                        _memory;
    private readonly ILogger<SqliteConversationService>    _log;
    private readonly SqliteConnection                      _keepAlive;

    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.CamelCase;

    public SqliteConversationService(
        IOptions<AniOptions> options,
        IMemoryService memory,
        ILogger<SqliteConversationService> log)
    {
        _memory = memory;
        _log    = log;

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

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<ConversationThread?> GetActiveThreadAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT id, started_at, last_message_at, is_active, initiated_by
            FROM conversation_threads
            WHERE is_active = 1
            ORDER BY last_message_at DESC
            LIMIT 1
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var thread = ReadThread(reader);
        thread.Messages = await LoadMessagesAsync(conn, thread.Id, ct).ConfigureAwait(false);
        return thread;
    }

    public async Task<ConversationThread?> GetThreadAsync(Guid threadId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT id, started_at, last_message_at, is_active, initiated_by
            FROM conversation_threads
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", threadId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        var thread = ReadThread(reader);
        thread.Messages = await LoadMessagesAsync(conn, thread.Id, ct).ConfigureAwait(false);
        return thread;
    }

    public async Task<List<ConversationThread>> GetRecentThreadsAsync(int limit = 10, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT id, started_at, last_message_at, is_active, initiated_by
            FROM conversation_threads
            ORDER BY last_message_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var threads = new List<ConversationThread>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var thread = ReadThread(reader);
            thread.Messages = await LoadMessagesAsync(conn, thread.Id, ct).ConfigureAwait(false);
            threads.Add(thread);
        }

        return threads;
    }

    public async Task SaveThreadAsync(ConversationThread thread, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO conversation_threads (id, started_at, last_message_at, is_active, initiated_by)
            VALUES ($id, $started_at, $last_message_at, $is_active, $initiated_by)
            ON CONFLICT(id) DO UPDATE SET
                last_message_at = $last_message_at,
                is_active       = $is_active
            """;

        cmd.Parameters.AddWithValue("$id",              thread.Id.ToString());
        cmd.Parameters.AddWithValue("$started_at",      thread.StartedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$last_message_at", thread.LastMessageAt.ToString("o"));
        cmd.Parameters.AddWithValue("$is_active",       thread.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$initiated_by",    thread.InitiatedBy);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task AddMessageAsync(Guid threadId, ConversationMessage message, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Insert the message
        await using var msgCmd = conn.CreateCommand();
        msgCmd.CommandText = """
            INSERT INTO conversation_messages (thread_id, role, content, sent_at)
            VALUES ($thread_id, $role, $content, $sent_at)
            """;
        msgCmd.Parameters.AddWithValue("$thread_id", threadId.ToString());
        msgCmd.Parameters.AddWithValue("$role",      message.Role);
        msgCmd.Parameters.AddWithValue("$content",   message.Content);
        msgCmd.Parameters.AddWithValue("$sent_at",   message.SentAt.ToString("o"));

        await msgCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Update thread's last_message_at
        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = """
            UPDATE conversation_threads
            SET last_message_at = $sent_at
            WHERE id = $thread_id
            """;
        updateCmd.Parameters.AddWithValue("$thread_id", threadId.ToString());
        updateCmd.Parameters.AddWithValue("$sent_at",   message.SentAt.ToString("o"));

        await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Save each message as episodic memory with auto-embedding so it survives
        // thread expiration and is findable via semantic search. Fixes BUG-010:
        // without this, expired conversation context is lost and re-engagement
        // on the same topic triggers confabulation (Michigan incident).
        try
        {
            var character = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
            var contactName = character.PrimaryContactName ?? "Mark";

            await _memory.SaveAsync(new MemoryRecord
            {
                Type           = MemoryType.Episodic,
                Content        = MemoryPrefixes.FormatSpeaker(message.Role, character.Name, contactName, message.Content),
                Importance     = 0.6f,
                RelationalValence = message.Role == Roles.Mark ? 0.7f : 0.5f,
                SourceName     = "conversation",
                OccurredAt     = message.SentAt,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save conversation message as episodic memory — conversation still recorded");
        }
    }

    public async Task CloseThreadAsync(Guid threadId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Mark thread inactive
        await using var closeCmd = conn.CreateCommand();
        closeCmd.CommandText = """
            UPDATE conversation_threads SET is_active = 0 WHERE id = $id
            """;
        closeCmd.Parameters.AddWithValue("$id", threadId.ToString());
        await closeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // Load the full thread to create the episodic memory
        var messages = await LoadMessagesAsync(conn, threadId, ct).ConfigureAwait(false);

        if (messages.Count > 0)
        {
            // Load character state for dynamic name mapping in thread summary
            var character = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
            var summary = BuildThreadSummary(messages, character.Name, character.PrimaryContactName);
            await _memory.SaveAsync(new MemoryRecord
            {
                Type       = MemoryType.Episodic,
                Content    = summary,
                Importance = 0.8f,
                SourceName = "conversation",
                OccurredAt = messages[^1].SentAt,
            }, ct).ConfigureAwait(false);

            _log.LogInformation(
                "Conversation thread {ThreadId} closed — {MessageCount} messages saved as episodic memory",
                threadId, messages.Count);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static ConversationThread ReadThread(SqliteDataReader reader) => new()
    {
        Id            = Guid.Parse(reader.GetString(0)),
        StartedAt     = DateTimeOffset.Parse(reader.GetString(1)),
        LastMessageAt = DateTimeOffset.Parse(reader.GetString(2)),
        IsActive      = reader.GetInt32(3) == 1,
        InitiatedBy   = reader.GetString(4),
    };

    private async Task<List<ConversationMessage>> LoadMessagesAsync(
        SqliteConnection conn, Guid threadId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT role, content, sent_at
            FROM conversation_messages
            WHERE thread_id = $thread_id
            ORDER BY sent_at ASC
            """;
        cmd.Parameters.AddWithValue("$thread_id", threadId.ToString());

        var messages = new List<ConversationMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messages.Add(new ConversationMessage
            {
                Role    = reader.GetString(0),
                Content = reader.GetString(1),
                SentAt  = DateTimeOffset.Parse(reader.GetString(2)),
            });
        }

        return messages;
    }

    private static string BuildThreadSummary(
        List<ConversationMessage> messages, string companionName, string contactName)
    {
        var lines = messages.Select(m =>
        {
            var name = m.Role == Roles.Ani ? companionName : contactName;
            return $"{name}: {m.Content}";
        });

        return $"Conversation ({messages.Count} messages):\n{string.Join("\n", lines)}";
    }

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

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS conversation_threads (
                id              TEXT PRIMARY KEY,
                started_at      TEXT NOT NULL,
                last_message_at TEXT NOT NULL,
                is_active       INTEGER NOT NULL DEFAULT 1,
                initiated_by    TEXT NOT NULL DEFAULT 'mark'
            );

            CREATE TABLE IF NOT EXISTS conversation_messages (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                thread_id TEXT NOT NULL,
                role      TEXT NOT NULL,
                content   TEXT NOT NULL,
                sent_at   TEXT NOT NULL,
                FOREIGN KEY (thread_id) REFERENCES conversation_threads(id)
            );

            CREATE INDEX IF NOT EXISTS ix_conv_messages_thread
                ON conversation_messages (thread_id, sent_at ASC);
            """;

        cmd.ExecuteNonQuery();
    }
}
