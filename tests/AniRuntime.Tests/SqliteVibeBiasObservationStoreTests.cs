using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #46 (2026-05-21) spec tests for
/// <see cref="SqliteVibeBiasObservationStore"/>. Pins schema +
/// write/read roundtrip + nullable thread_id + UPSERT semantics +
/// observed_at DESC ordering. Real in-memory SQLite (no mocks) per the
/// store-layer testing convention; the strict-mock discipline applies to
/// consumer-layer tests, not to the schema layer itself. Mirrors
/// <see cref="SqliteClosedConversationStoreTests"/> in shape.
/// </summary>
public class SqliteVibeBiasObservationStoreTests : IDisposable
{
    private readonly SqliteVibeBiasObservationStore _store;

    public SqliteVibeBiasObservationStoreTests()
    {
        var dbName  = $"ani-vibebias-obs-test-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions { MemoryDbPath = dbName });
        _store = new SqliteVibeBiasObservationStore(
            options, NullLogger<SqliteVibeBiasObservationStore>.Instance);
    }

    public void Dispose() => _store.Dispose();

    private static VibeBiasObservationRecord SampleRecord(
        Guid?           id          = null,
        DateTimeOffset? observedAt  = null,
        string          callSite    = "reply",
        Guid?           threadId    = null,
        string          register    = "Tenderness",
        float           value       = 0.62f) => new()
    {
        Id                    = id ?? Guid.NewGuid(),
        ObservedAt            = observedAt ?? DateTimeOffset.UtcNow,
        CallSite              = callSite,
        ThreadId              = threadId,
        ContactName           = "Mark",
        CandidateCount        = 100,
        TierBreakdownLight    = 60,
        TierBreakdownMedium   = 3,
        TierBreakdownHeavy    = 37,
        SurfacedRecordsJson   = """[{"recordId":"00000000-0000-0000-0000-000000000001","weight":0.45,"tier":"Heavy","ageHours":15.1,"valence":1.0}]""",
        RecommendedRegister   = register,
        RecommendedValue      = value,
        DiversityScoreReason  = "MMR surfaced top 1 (lambda=0.30)",
    };

    [Fact]
    public async Task NewStore_HasEmptySchema()
    {
        var recent = await _store.GetRecentAsync(10);
        recent.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_RoundtripsAllFields_WithThreadId()
    {
        var threadId = Guid.NewGuid();
        var original = SampleRecord(threadId: threadId);

        await _store.SaveAsync(original);
        var loaded = (await _store.GetRecentAsync(10)).Single();

        loaded.Id.Should().Be(original.Id);
        loaded.ObservedAt.Should().BeCloseTo(original.ObservedAt, TimeSpan.FromMilliseconds(1));
        loaded.CallSite.Should().Be(original.CallSite);
        loaded.ThreadId.Should().Be(threadId);
        loaded.ContactName.Should().Be(original.ContactName);
        loaded.CandidateCount.Should().Be(original.CandidateCount);
        loaded.TierBreakdownLight.Should().Be(original.TierBreakdownLight);
        loaded.TierBreakdownMedium.Should().Be(original.TierBreakdownMedium);
        loaded.TierBreakdownHeavy.Should().Be(original.TierBreakdownHeavy);
        loaded.SurfacedRecordsJson.Should().Be(original.SurfacedRecordsJson);
        loaded.RecommendedRegister.Should().Be(original.RecommendedRegister);
        loaded.RecommendedValue.Should().BeApproximately(original.RecommendedValue, 0.0001f);
        loaded.DiversityScoreReason.Should().Be(original.DiversityScoreReason);
    }

    /// <summary>
    /// SPEC: ThreadId is nullable. The outreach call-site case (pre-thread)
    /// must persist with NULL and read back as null — Gate 3 queries must
    /// be able to filter on outreach vs. reply records separately.
    /// </summary>
    [Fact]
    public async Task SaveAsync_NullThreadId_RoundtripsAsNull()
    {
        var original = SampleRecord(callSite: "outreach", threadId: null);

        await _store.SaveAsync(original);
        var loaded = (await _store.GetRecentAsync(10)).Single();

        loaded.ThreadId.Should().BeNull();
        loaded.CallSite.Should().Be("outreach");
    }

    /// <summary>
    /// SPEC: SaveAsync is idempotent on Id (UPSERT). Re-saving the same
    /// id with mutated fields replaces the existing row; only one row
    /// remains.
    /// </summary>
    [Fact]
    public async Task SaveAsync_OnConflictId_UpsertsRow()
    {
        var record = SampleRecord(register: "Tenderness", value: 0.4f);
        await _store.SaveAsync(record);

        var mutated = record with { RecommendedRegister = "Playfulness", RecommendedValue = 0.9f };
        await _store.SaveAsync(mutated);

        var all = (await _store.GetRecentAsync(10)).ToList();
        all.Should().HaveCount(1, "UPSERT on conflict — no second row created");
        all[0].RecommendedRegister.Should().Be("Playfulness");
        all[0].RecommendedValue.Should().BeApproximately(0.9f, 0.0001f);
    }

    /// <summary>
    /// SPEC: GetRecentAsync orders by observed_at DESC and respects the
    /// limit. Most recent first — the dashboard / Gate 3 review pattern.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_OrdersByObservedAtDescAndRespectsLimit()
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        await _store.SaveAsync(SampleRecord(observedAt: t0,                  register: "oldest"));
        await _store.SaveAsync(SampleRecord(observedAt: t0.AddMinutes(60),  register: "middle"));
        await _store.SaveAsync(SampleRecord(observedAt: t0.AddMinutes(120), register: "newest"));

        var topTwo = (await _store.GetRecentAsync(2)).ToList();

        topTwo.Should().HaveCount(2);
        topTwo[0].RecommendedRegister.Should().Be("newest", "most recent must come first");
        topTwo[1].RecommendedRegister.Should().Be("middle");
    }

    /// <summary>
    /// SPEC: both call sites coexist in the same table. The dashboard /
    /// Gate 3 review must be able to query mixed records and distinguish
    /// outreach vs. reply via the call_site field.
    /// </summary>
    [Fact]
    public async Task SaveAsync_MixedCallSites_CoexistInTable()
    {
        await _store.SaveAsync(SampleRecord(callSite: "outreach", threadId: null));
        await _store.SaveAsync(SampleRecord(callSite: "reply",    threadId: Guid.NewGuid()));

        var all = (await _store.GetRecentAsync(10)).ToList();
        all.Should().HaveCount(2);
        all.Select(r => r.CallSite).Should().BeEquivalentTo(new[] { "outreach", "reply" });
    }
}
