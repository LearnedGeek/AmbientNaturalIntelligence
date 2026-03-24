using System.Text.Json;
using AniRuntime.Core.Utilities;
using AniRuntime.Emergence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Emergence;

/// <summary>
/// SQLite-backed store for emergence layer data.
/// Separate database (ani-emergence.db) from the main runtime (ani-memory.db).
/// Delete the file to roll back all emergence data without affecting Ani's core state.
///
/// Follows the same patterns as SqliteMemoryService: WAL mode, keep-alive connection,
/// InitialiseSchema on construction.
/// </summary>
public class EmergenceStore : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _keepAlive;
    private readonly ILogger<EmergenceStore> _log;

    public EmergenceStore(
        IOptions<EmergenceOptions> options,
        ILogger<EmergenceStore> log)
    {
        _log = log;
        var dbPath = options.Value.EmergenceDbPath;

        if (dbPath.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || !dbPath.Contains(Path.DirectorySeparatorChar)
               && !dbPath.Contains('/') && !dbPath.Contains('\\') && !dbPath.Contains('.'))
        {
            _connectionString = $"Data Source={dbPath};Mode=Memory;Cache=Shared";
        }
        else
        {
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

    // ── Schema ──────────────────────────────────────────────────────────────

    private void InitialiseSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS resonance_records (
                id               TEXT PRIMARY KEY,
                theme            TEXT NOT NULL,
                resonance_score  REAL NOT NULL DEFAULT 0,
                occurrence_count INTEGER NOT NULL DEFAULT 0,
                first_observed   TEXT NOT NULL,
                last_observed    TEXT NOT NULL,
                type             TEXT NOT NULL DEFAULT 'Relational',
                origin           TEXT NOT NULL DEFAULT 'Observed'
            );

            CREATE TABLE IF NOT EXISTS emergence_log (
                id                       INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp                TEXT NOT NULL,
                entry_type               TEXT NOT NULL,
                resonance_score          REAL,
                details_json             TEXT NOT NULL DEFAULT '{}',
                cycle_observation_json   TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_emergence_log_time ON emergence_log (timestamp DESC);
            CREATE INDEX IF NOT EXISTS ix_emergence_log_type ON emergence_log (entry_type);
            CREATE INDEX IF NOT EXISTS ix_resonance_theme    ON resonance_records (theme);

            -- Feature 38: Emergence taxonomy column (nullable — null means no emergence detected)
            -- ALTER TABLE is idempotent when wrapped in a try; SQLite doesn't support IF NOT EXISTS for columns.

            -- E2 schema (created now, unused in E1 — avoids future migration)
            CREATE TABLE IF NOT EXISTS preference_signals (
                id                        TEXT PRIMARY KEY,
                description               TEXT NOT NULL,
                confidence                REAL NOT NULL DEFAULT 0,
                stability                 REAL NOT NULL DEFAULT 0,
                signal_type               TEXT NOT NULL,
                first_signaled            TEXT NOT NULL,
                supporting_resonance_ids  TEXT,
                written_to_character      INTEGER NOT NULL DEFAULT 0,
                written_at                TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // Feature 38: Add emergence_types column if it doesn't exist
        try
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE emergence_log ADD COLUMN emergence_types TEXT";
            alterCmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* Column already exists — safe to ignore */ }

        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_emergence_log_types ON emergence_log (emergence_types)";
        idxCmd.ExecuteNonQuery();

        _log.LogInformation("Emergence database initialised ({Conn})", _connectionString);
    }

    // ── Resonance Records ───────────────────────────────────────────────────

    public async Task SaveResonanceAsync(ResonanceRecord record, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO resonance_records (id, theme, resonance_score, occurrence_count, first_observed, last_observed, type, origin)
            VALUES (@id, @theme, @score, @count, @first, @last, @type, @origin)
            ON CONFLICT(id) DO UPDATE SET
                resonance_score  = @score,
                occurrence_count = @count,
                last_observed    = @last
            """;
        cmd.Parameters.AddWithValue("@id", record.Id);
        cmd.Parameters.AddWithValue("@theme", record.Theme);
        cmd.Parameters.AddWithValue("@score", record.ResonanceScore);
        cmd.Parameters.AddWithValue("@count", record.OccurrenceCount);
        cmd.Parameters.AddWithValue("@first", record.FirstObserved.ToString("o"));
        cmd.Parameters.AddWithValue("@last", record.LastObserved.ToString("o"));
        cmd.Parameters.AddWithValue("@type", record.Type);
        cmd.Parameters.AddWithValue("@origin", record.Origin);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResonanceRecord>> GetResonanceRecordsAsync(
        int limit = 50, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM resonance_records ORDER BY last_observed DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var records = new List<ResonanceRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            records.Add(new ResonanceRecord
            {
                Id              = reader.GetString(0),
                Theme           = reader.GetString(1),
                ResonanceScore  = reader.GetFloat(2),
                OccurrenceCount = reader.GetInt32(3),
                FirstObserved   = DateTimeOffset.Parse(reader.GetString(4)),
                LastObserved    = DateTimeOffset.Parse(reader.GetString(5)),
                Type            = reader.GetString(6),
                Origin          = reader.GetString(7),
            });
        }
        return records;
    }

    // ── Emergence Log ───────────────────────────────────────────────────────

    public async Task WriteLogEntryAsync(EmergenceLogEntry entry, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO emergence_log (timestamp, entry_type, resonance_score, details_json, cycle_observation_json, emergence_types)
            VALUES (@ts, @type, @score, @details, @obs, @etypes)
            """;
        cmd.Parameters.AddWithValue("@ts", entry.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("@type", entry.EntryType);
        cmd.Parameters.AddWithValue("@score", entry.ResonanceScore.HasValue ? (object)entry.ResonanceScore.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@details", entry.DetailsJson);
        cmd.Parameters.AddWithValue("@obs", entry.CycleObservationJson is not null ? (object)entry.CycleObservationJson : DBNull.Value);
        cmd.Parameters.AddWithValue("@etypes", entry.EmergenceTypesJson is not null ? (object)entry.EmergenceTypesJson : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EmergenceLogEntry>> GetRecentLogEntriesAsync(
        int limit = 50, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, entry_type, resonance_score, details_json, cycle_observation_json, emergence_types FROM emergence_log ORDER BY timestamp DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var entries = new List<EmergenceLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new EmergenceLogEntry
            {
                Id                   = reader.GetInt64(0),
                Timestamp            = DateTimeOffset.Parse(reader.GetString(1)),
                EntryType            = reader.GetString(2),
                ResonanceScore       = reader.IsDBNull(3) ? null : reader.GetFloat(3),
                DetailsJson          = reader.GetString(4),
                CycleObservationJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                EmergenceTypesJson   = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }
        return entries;
    }

    // ── Emergence Type Queries (Feature 38) ────────────────────────────────

    public async Task<Dictionary<string, int>> GetTypeDistributionAsync(
        int days = 30, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("o");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT emergence_types FROM emergence_log
            WHERE emergence_types IS NOT NULL AND timestamp >= @cutoff
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var distribution = new Dictionary<string, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var types = JsonSerializer.Deserialize<List<string>>(json);
            if (types is null) continue;

            foreach (var type in types)
            {
                distribution.TryGetValue(type, out var count);
                distribution[type] = count + 1;
            }
        }
        return distribution;
    }

    public async Task<IReadOnlyList<EmergenceLogEntry>> GetHighlightsAsync(
        int limit = 10, float minScore = 0.4f, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, timestamp, entry_type, resonance_score, details_json, cycle_observation_json, emergence_types
            FROM emergence_log
            WHERE emergence_types IS NOT NULL AND resonance_score >= @minScore
            ORDER BY resonance_score DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@minScore", minScore);
        cmd.Parameters.AddWithValue("@limit", limit);

        var entries = new List<EmergenceLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new EmergenceLogEntry
            {
                Id                   = reader.GetInt64(0),
                Timestamp            = DateTimeOffset.Parse(reader.GetString(1)),
                EntryType            = reader.GetString(2),
                ResonanceScore       = reader.IsDBNull(3) ? null : reader.GetFloat(3),
                DetailsJson          = reader.GetString(4),
                CycleObservationJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                EmergenceTypesJson   = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }
        return entries;
    }

    public async Task<int> BackfillEmergenceTypesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Read all entries that have CycleObservation JSON but no emergence types
        await using var readCmd = conn.CreateCommand();
        readCmd.CommandText = """
            SELECT id, cycle_observation_json FROM emergence_log
            WHERE cycle_observation_json IS NOT NULL
            """;

        var updates = new List<(long id, string typesJson)>();
        await using var reader = await readCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var obsJson = reader.GetString(1);

            try
            {
                var obs = JsonSerializer.Deserialize<Core.Models.CycleObservation>(obsJson, JsonDefaults.CamelCase);
                if (obs is null) continue;

                var types = EmergenceClassifier.Classify(obs);
                if (types.Count > 0)
                    updates.Add((id, JsonSerializer.Serialize(types)));
            }
            catch { /* skip malformed entries */ }
        }

        // Apply updates
        foreach (var (id, typesJson) in updates)
        {
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE emergence_log SET emergence_types = @types WHERE id = @id";
            updateCmd.Parameters.AddWithValue("@types", typesJson);
            updateCmd.Parameters.AddWithValue("@id", id);
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return updates.Count;
    }

    // ── Stats ───────────────────────────────────────────────────────────────

    public async Task<EmergenceStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM resonance_records),
                (SELECT COUNT(*) FROM emergence_log),
                (SELECT MAX(timestamp) FROM emergence_log)
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new EmergenceStats
            {
                ResonanceCount = reader.GetInt32(0),
                LogEntryCount  = reader.GetInt32(1),
                LastScoredAt   = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
            };
        }
        return new EmergenceStats();
    }
}

public class EmergenceStats
{
    public int ResonanceCount { get; set; }
    public int LogEntryCount { get; set; }
    public DateTimeOffset? LastScoredAt { get; set; }
}
