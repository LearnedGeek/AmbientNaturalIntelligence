using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Memory;

/// <summary>
/// Vibe Loop V1.5a (2026-05-21) — SQLite-backed
/// <see cref="IVibeBiasObservationStore"/>. Same database file and
/// connection-string convention as <see cref="SqliteClosedConversationStore"/>;
/// new dedicated table <c>vibe_bias_observations</c>. See GitHub Issue #46.
///
/// Schema initialisation is idempotent (CREATE TABLE IF NOT EXISTS); the
/// constructor opens a keep-alive connection so the in-memory shared-cache
/// SQLite mode used by tests retains state across the test method.
/// </summary>
public class SqliteVibeBiasObservationStore : IVibeBiasObservationStore, IDisposable
{
    private readonly string                                       _connectionString;
    private readonly ILogger<SqliteVibeBiasObservationStore>      _log;
    private readonly SqliteConnection                             _keepAlive;

    public SqliteVibeBiasObservationStore(
        IOptions<AniOptions> options,
        ILogger<SqliteVibeBiasObservationStore> log)
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
            CREATE TABLE IF NOT EXISTS vibe_bias_observations (
                id                          TEXT PRIMARY KEY,
                observed_at                 TEXT NOT NULL,
                call_site                   TEXT NOT NULL,
                thread_id                   TEXT,
                contact_name                TEXT NOT NULL,
                candidate_count             INTEGER NOT NULL,
                tier_breakdown_light        INTEGER NOT NULL,
                tier_breakdown_medium       INTEGER NOT NULL,
                tier_breakdown_heavy        INTEGER NOT NULL,
                surfaced_records_json       TEXT NOT NULL,
                recommended_register        TEXT NOT NULL,
                recommended_value           REAL NOT NULL,
                diversity_score_reason      TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_vibe_bias_obs_observed_at
                ON vibe_bias_observations (observed_at DESC);

            CREATE INDEX IF NOT EXISTS ix_vibe_bias_obs_thread_id
                ON vibe_bias_observations (thread_id);

            CREATE INDEX IF NOT EXISTS ix_vibe_bias_obs_call_site
                ON vibe_bias_observations (call_site);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task SaveAsync(VibeBiasObservationRecord record, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vibe_bias_observations (
                id, observed_at, call_site, thread_id, contact_name,
                candidate_count,
                tier_breakdown_light, tier_breakdown_medium, tier_breakdown_heavy,
                surfaced_records_json, recommended_register, recommended_value,
                diversity_score_reason
            )
            VALUES (
                $id, $observed_at, $call_site, $thread_id, $contact_name,
                $candidate_count,
                $tier_light, $tier_medium, $tier_heavy,
                $surfaced_json, $recommended_register, $recommended_value,
                $diversity_score_reason
            )
            ON CONFLICT(id) DO UPDATE SET
                observed_at             = excluded.observed_at,
                call_site               = excluded.call_site,
                thread_id               = excluded.thread_id,
                contact_name            = excluded.contact_name,
                candidate_count         = excluded.candidate_count,
                tier_breakdown_light    = excluded.tier_breakdown_light,
                tier_breakdown_medium   = excluded.tier_breakdown_medium,
                tier_breakdown_heavy    = excluded.tier_breakdown_heavy,
                surfaced_records_json   = excluded.surfaced_records_json,
                recommended_register    = excluded.recommended_register,
                recommended_value       = excluded.recommended_value,
                diversity_score_reason  = excluded.diversity_score_reason
            """;

        cmd.Parameters.AddWithValue("$id",                     record.Id.ToString());
        cmd.Parameters.AddWithValue("$observed_at",            record.ObservedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$call_site",              record.CallSite);
        cmd.Parameters.AddWithValue("$thread_id",              (object?)record.ThreadId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contact_name",           record.ContactName);
        cmd.Parameters.AddWithValue("$candidate_count",        record.CandidateCount);
        cmd.Parameters.AddWithValue("$tier_light",             record.TierBreakdownLight);
        cmd.Parameters.AddWithValue("$tier_medium",            record.TierBreakdownMedium);
        cmd.Parameters.AddWithValue("$tier_heavy",             record.TierBreakdownHeavy);
        cmd.Parameters.AddWithValue("$surfaced_json",          record.SurfacedRecordsJson);
        cmd.Parameters.AddWithValue("$recommended_register",   record.RecommendedRegister);
        cmd.Parameters.AddWithValue("$recommended_value",      record.RecommendedValue);
        cmd.Parameters.AddWithValue("$diversity_score_reason", record.DiversityScoreReason);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IEnumerable<VibeBiasObservationRecord>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vibe_bias_observations ORDER BY observed_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<VibeBiasObservationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRecord(reader));
        return results;
    }

    private static VibeBiasObservationRecord ReadRecord(SqliteDataReader reader)
    {
        var threadOrd = reader.GetOrdinal("thread_id");
        return new VibeBiasObservationRecord
        {
            Id                    = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            ObservedAt            = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("observed_at"))),
            CallSite              = reader.GetString(reader.GetOrdinal("call_site")),
            ThreadId              = reader.IsDBNull(threadOrd) ? null : Guid.Parse(reader.GetString(threadOrd)),
            ContactName           = reader.GetString(reader.GetOrdinal("contact_name")),
            CandidateCount        = reader.GetInt32(reader.GetOrdinal("candidate_count")),
            TierBreakdownLight    = reader.GetInt32(reader.GetOrdinal("tier_breakdown_light")),
            TierBreakdownMedium   = reader.GetInt32(reader.GetOrdinal("tier_breakdown_medium")),
            TierBreakdownHeavy    = reader.GetInt32(reader.GetOrdinal("tier_breakdown_heavy")),
            SurfacedRecordsJson   = reader.GetString(reader.GetOrdinal("surfaced_records_json")),
            RecommendedRegister   = reader.GetString(reader.GetOrdinal("recommended_register")),
            RecommendedValue      = (float)reader.GetDouble(reader.GetOrdinal("recommended_value")),
            DiversityScoreReason  = reader.GetString(reader.GetOrdinal("diversity_score_reason")),
        };
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }
}
