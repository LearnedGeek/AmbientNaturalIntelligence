using AniRuntime.Emergence;
using AniRuntime.Emergence.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

public class EmergenceStoreTests : IDisposable
{
    private readonly EmergenceStore _store;

    public EmergenceStoreTests()
    {
        var options = Options.Create(new EmergenceOptions
        {
            Enabled = true,
            EmergenceDbPath = $"emergence-store-test-{Guid.NewGuid():N}",
        });
        _store = new EmergenceStore(options, NullLogger<EmergenceStore>.Instance);
    }

    public void Dispose() => _store.Dispose();

    // ── Resonance Records ─────────────────────────────────────────────────

    [Fact]
    public async Task SaveResonance_ThenGet_ReturnsRecord()
    {
        var record = new ResonanceRecord
        {
            Theme = "snow imagery",
            ResonanceScore = 0.65f,
            OccurrenceCount = 1,
            Type = "Aesthetic",
        };

        await _store.SaveResonanceAsync(record);

        var records = await _store.GetResonanceRecordsAsync();
        records.Should().ContainSingle(r => r.Theme == "snow imagery");
        records[0].ResonanceScore.Should().BeApproximately(0.65f, 0.001f);
        records[0].Type.Should().Be("Aesthetic");
    }

    [Fact]
    public async Task SaveResonance_Upsert_UpdatesExisting()
    {
        var record = new ResonanceRecord
        {
            Theme = "playful teasing",
            ResonanceScore = 0.5f,
            OccurrenceCount = 1,
        };

        await _store.SaveResonanceAsync(record);

        // Bump the same record
        record.ResonanceScore = 1.1f;
        record.OccurrenceCount = 2;
        record.LastObserved = DateTimeOffset.UtcNow.AddMinutes(5);
        await _store.SaveResonanceAsync(record);

        var records = await _store.GetResonanceRecordsAsync();
        records.Should().ContainSingle();
        records[0].ResonanceScore.Should().BeApproximately(1.1f, 0.001f);
        records[0].OccurrenceCount.Should().Be(2);
    }

    [Fact]
    public async Task GetResonanceRecords_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            await _store.SaveResonanceAsync(new ResonanceRecord
            {
                Theme = $"theme-{i}",
                ResonanceScore = i * 0.1f,
                OccurrenceCount = 1,
            });
        }

        var records = await _store.GetResonanceRecordsAsync(limit: 3);
        records.Should().HaveCount(3);
    }

    // ── Log Entries ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteLogEntry_ThenGetRecent_ReturnsEntry()
    {
        var entry = new EmergenceLogEntry
        {
            EntryType = "CycleScored",
            ResonanceScore = 0.42f,
            DetailsJson = "{\"score\": 0.42}",
            CycleObservationJson = "{\"innerThought\": \"test\"}",
        };

        await _store.WriteLogEntryAsync(entry);

        var entries = await _store.GetRecentLogEntriesAsync();
        entries.Should().ContainSingle();
        entries[0].EntryType.Should().Be("CycleScored");
        entries[0].ResonanceScore.Should().BeApproximately(0.42f, 0.001f);
        entries[0].CycleObservationJson.Should().Contain("test");
    }

    [Fact]
    public async Task WriteLogEntry_NullScore_StoresAsNull()
    {
        var entry = new EmergenceLogEntry
        {
            EntryType = "ResonanceDecay",
            ResonanceScore = null,
            DetailsJson = "{}",
        };

        await _store.WriteLogEntryAsync(entry);

        var entries = await _store.GetRecentLogEntriesAsync();
        entries[0].ResonanceScore.Should().BeNull();
    }

    [Fact]
    public async Task GetRecentLogEntries_OrderedByTimestampDesc()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.WriteLogEntryAsync(new EmergenceLogEntry
        {
            Timestamp = now.AddMinutes(-10),
            EntryType = "CycleScored",
            DetailsJson = "{}",
        });
        await _store.WriteLogEntryAsync(new EmergenceLogEntry
        {
            Timestamp = now,
            EntryType = "ResonanceBump",
            DetailsJson = "{}",
        });

        var entries = await _store.GetRecentLogEntriesAsync();
        entries[0].EntryType.Should().Be("ResonanceBump", "most recent entry first");
        entries[1].EntryType.Should().Be("CycleScored");
    }

    // ── Stats ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStats_EmptyDb_ReturnsZeros()
    {
        var stats = await _store.GetStatsAsync();

        stats.ResonanceCount.Should().Be(0);
        stats.LogEntryCount.Should().Be(0);
        stats.LastScoredAt.Should().BeNull();
    }

    [Fact]
    public async Task GetStats_WithData_ReturnsCorrectCounts()
    {
        await _store.SaveResonanceAsync(new ResonanceRecord { Theme = "a", ResonanceScore = 0.5f, OccurrenceCount = 1 });
        await _store.SaveResonanceAsync(new ResonanceRecord { Theme = "b", ResonanceScore = 0.3f, OccurrenceCount = 1 });
        await _store.WriteLogEntryAsync(new EmergenceLogEntry { EntryType = "CycleScored", DetailsJson = "{}" });

        var stats = await _store.GetStatsAsync();

        stats.ResonanceCount.Should().Be(2);
        stats.LogEntryCount.Should().Be(1);
        stats.LastScoredAt.Should().NotBeNull();
    }
}
