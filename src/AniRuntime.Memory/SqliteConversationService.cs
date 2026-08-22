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
    private readonly IClosedConversationSummarizer         _summarizer;
    private readonly IClosedConversationStore              _closedStore;
    private readonly ILogger<SqliteConversationService>    _log;
    private readonly SqliteConnection                      _keepAlive;

    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.CamelCase;

    public SqliteConversationService(
        IOptions<AniOptions> options,
        IMemoryService memory,
        IClosedConversationSummarizer summarizer,
        IClosedConversationStore closedStore,
        ILogger<SqliteConversationService> log)
    {
        _memory      = memory;
        _summarizer  = summarizer;
        _closedStore = closedStore;
        _log         = log;

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
        // Admin command defense-in-depth (Apr 28, 2026): commands starting with
        // "///" are administrative metadata, not relational events. They MUST NOT
        // enter conversation_messages — doing so causes downstream leaks
        // (CloseThreadAsync includes them in the summary; structured per-speaker
        // summary surfaces them into prompt-builders; cognitive cycle reads
        // them and treats the relational state as having received a reply). The
        // primary defense is at TwilioInboundPerceptionSource (route directly,
        // skip thread ops). This guard is defense-in-depth: if any future code
        // path calls AddMessageAsync with admin content (test injection,
        // dashboard direct-write, etc.), the row is rejected at the data layer
        // and the substrate stays clean.
        //
        // Pre-Apr-28 history: an earlier short-circuit only skipped the Episodic
        // memory save AFTER the INSERT — that left the thread-message row in
        // place and produced the leak vector this fix closes. See Apr 28
        // gap-watch row "Architectural concern: conversation_messages misused
        // as a delivery channel for non-conversation events."
        var content = message.Content ?? string.Empty;
        if (content.TrimStart().StartsWith("///"))
        {
            _log.LogDebug("Admin command detected in AddMessageAsync — rejecting at data layer (defense-in-depth): {Preview}",
                content.Length > 60 ? content[..60] : content);
            return;
        }

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
        //
        // **Gate-fallback suppression** (May 4, 2026 — Phase Tracker gap-watch
        // row May 4 evening). The J.5a re-eval gate's SafeAcknowledgement
        // fall-through is a substrate-thinness artifact, not a substantive
        // utterance. Persisting it as Episodic re-enters it into the
        // retrieval pool on the next cycle (observed at log line 4560 May 4:
        // "I said to Mark: 'mmm, sorry — give me a second to gather my
        // thoughts'" surfaced as J0_RETRIEVAL_TEMPORAL rank=0). Three
        // fall-throughs in 23h means three such records compound into the
        // substrate the regen draws from. Skip the Episodic write for the
        // SafeAck — the conversation_messages row above still persists, so
        // active-thread continuity is preserved (the model knows it just
        // said "give me a sec"); only the broader retrieval pool is spared.
        if (message.Role == Roles.Ani && content == GateFallbacks.SafeAcknowledgement)
        {
            _log.LogInformation(
                "Skipping Episodic persist for J.5a SafeAcknowledgement fall-through — preserves active-thread continuity, prevents retrieval-pool pollution.");
            return;
        }

        try
        {
            var character = await _memory.GetCharacterStateAsync(ct).ConfigureAwait(false);
            var contactName = character.PrimaryContactName ?? "Mark";

            // F-2 Phase 1 P6 (2026-08-22) — conversation-message Episodic
            // records: attribution derived from role. Mark's turns are
            // his verified utterances (came from Twilio inbound); Ani's
            // turns are her verified composed replies. Descriptor points
            // to the conversation source-name so audit can trace.
            var convAttribution = message.Role == Roles.Mark
                ? AniRuntime.Core.Models.AttributionTriple.MarkAt(message.SentAt, $"conversation:{message.Role}:{message.SentAt:O}")
                : AniRuntime.Core.Models.AttributionTriple.AniAt(message.SentAt);
            await _memory.SaveAsync(new MemoryRecord
            {
                Type           = MemoryType.Episodic,
                Content        = MemoryPrefixes.FormatSpeaker(message.Role, character.Name, contactName, content),
                Importance     = 0.6f,
                RelationalValence = message.Role == Roles.Mark ? 0.7f : 0.5f,
                SourceName     = "conversation",
                OccurredAt     = message.SentAt,
                // Epistemic Grounding (Apr 10): Verbatim conversation messages
                // (both sides) are Episodic tier — "what was said," retrieved
                // for continuity, never treated as factual grounding.
                // Mark's assertions ALSO flow into Facts tier via the separate
                // twilio-inbound perception source path.
                Provenance     = EpistemicTier.Episodic,
                AttributedTo               = convAttribution.AttributedTo,
                AttributedAt               = convAttribution.AttributedAt,
                AttributedSourceRecordId   = convAttribution.SourceRecordId,
                AttributedSourceDescriptor = convAttribution.SourceDescriptor,
                AttributionTrust           = convAttribution.Trust,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to save conversation message as episodic memory — conversation still recorded");
        }
    }

    /// <summary>
    /// Vibe Loop V1.3 (Apr 29, 2026) — closing a thread now produces a
    /// structured <see cref="ClosedConversationRecord"/> via the V1.2
    /// summarizer and persists it through the V1.1 store. The pre-V1
    /// verbatim "Conversation (N messages):" Episodic write is GONE —
    /// that was the producer-side leak surface that fed the Apr 29
    /// outreach-prompt parrot recurrence.
    ///
    /// Per-message <c>conversation_messages</c> rows stay intact;
    /// verbatim fidelity-when-needed lives there. The
    /// <c>ClosedConversationRecord</c> is the gist surface for retrieval.
    /// Two surfaces, two purposes — substrate-typing pattern.
    ///
    /// Failure handling: if summarization fails (LLM unreachable, etc.)
    /// the thread is STILL marked inactive — relational state advances
    /// regardless of whether the record was produced. We DO NOT fall
    /// back to writing the verbatim Episodic record on failure; that
    /// would re-open the leak this rewrite closes.
    /// </summary>
    public async Task CloseThreadAsync(Guid threadId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Mark thread inactive — done first so relational state advances
        // even if summarization fails downstream.
        await using var closeCmd = conn.CreateCommand();
        closeCmd.CommandText = """
            UPDATE conversation_threads SET is_active = 0 WHERE id = $id
            """;
        closeCmd.Parameters.AddWithValue("$id", threadId.ToString());
        await closeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var threadMeta = await LoadThreadMetaAsync(conn, threadId, ct).ConfigureAwait(false);
        var messages   = await LoadMessagesAsync(conn, threadId, ct).ConfigureAwait(false);

        if (messages.Count == 0 || threadMeta is null)
        {
            _log.LogDebug("CloseThreadAsync {ThreadId}: empty thread; no record produced.", threadId);
            return;
        }

        threadMeta.Messages = messages;

        try
        {
            // F-1 Phase 8c: summariser now returns IClosedConversationEnvelope
            // for producer-boundary provenance; unwrap the record here for
            // storage. Envelope provenance is not persisted — the store
            // reads/writes the record shape directly.
            var envelope = await _summarizer.SummariseAsync(threadMeta, ct).ConfigureAwait(false);
            var record   = envelope.Content;
            await _closedStore.SaveAsync(record, ct).ConfigureAwait(false);

            _log.LogInformation(
                "Vibe Loop V1: thread {ThreadId} closed — {MessageCount} messages → ClosedConversationRecord {RecordId} (valence={Valence:+0.00;-0.00})",
                threadId, messages.Count, record.Id, record.OutcomeSignalValence);
        }
        catch (Exception ex)
        {
            // Non-fatal: thread is already marked inactive. Logged and
            // dropped — we deliberately do NOT fall back to writing a
            // verbatim Episodic record (that's the leak this closes).
            _log.LogWarning(ex,
                "Vibe Loop V1: thread {ThreadId} close-time summarization failed; no ClosedConversationRecord written for this thread.",
                threadId);
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

    private async Task<ConversationThread?> LoadThreadMetaAsync(
        SqliteConnection conn, Guid threadId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, started_at, last_message_at, is_active, initiated_by
            FROM conversation_threads
            WHERE id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", threadId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadThread(reader) : null;
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
