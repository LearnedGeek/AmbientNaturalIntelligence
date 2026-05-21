using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// V1.1 spec tests for <see cref="SqliteClosedConversationStore"/> — pins
/// the schema + write/read roundtrip + UPSERT semantics + index-driven
/// recency ordering. Uses real in-memory SQLite (no mocks) per the
/// existing convention for store-layer tests; the strict-mock discipline
/// applies to consumer-layer tests, not to the schema layer itself.
/// </summary>
public class SqliteClosedConversationStoreTests : IDisposable
{
    private readonly SqliteClosedConversationStore _store;

    public SqliteClosedConversationStoreTests()
    {
        var dbName = $"ani-vibeloop-test-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions { MemoryDbPath = dbName });
        _store = new SqliteClosedConversationStore(
            options, NullLogger<SqliteClosedConversationStore>.Instance);
    }

    public void Dispose() => _store.Dispose();

    private static ClosedConversationRecord SampleRecord(
        Guid? id = null, Guid? threadId = null, DateTimeOffset? closedAt = null,
        string gist = "Mark teased Ani about being distracted by him while trying to work; she leaned in playfully.",
        float valence = 0.4f) => new()
    {
        Id            = id ?? Guid.NewGuid(),
        ThreadId      = threadId ?? Guid.NewGuid(),
        ClosedAt      = closedAt ?? DateTimeOffset.UtcNow,
        Gist          = gist,
        TopicKeywords = new List<string> { "work", "distraction", "teasing" },
        MarkRegister  = new Dictionary<string, float>
        {
            ["Tenderness"] = 0.3f, ["Playfulness"] = 0.7f, ["Longing"] = 0.1f,
        },
        AniRegister = new Dictionary<string, float>
        {
            ["Tenderness"] = 0.6f, ["Playfulness"] = 0.4f, ["Curiosity"] = 0.2f,
        },
        OutcomeSignalSeedVector = new Dictionary<string, float>
        {
            ["Tenderness"] = +0.3f, ["Playfulness"] = +0.4f, ["Hurt"] = -0.1f,
        },
        OutcomeSignalValence = valence,
        TurnCount            = 4,
        DurationSeconds      = 22 * 60,
        Embedding            = new[] { 0.1f, 0.2f, 0.3f, 0.4f },
    };

    /// <summary>
    /// SPEC: schema is created on construction; a fresh store has zero
    /// records but successfully responds to GetRecentAsync / GetByIdAsync.
    /// </summary>
    [Fact]
    public async Task NewStore_HasEmptySchemaButRespondsToReads()
    {
        var byId     = await _store.GetByIdAsync(Guid.NewGuid());
        var byThread = await _store.GetByThreadIdAsync(Guid.NewGuid());
        var recent   = await _store.GetRecentAsync(10);

        byId.Should().BeNull();
        byThread.Should().BeNull();
        recent.Should().BeEmpty();
    }

    /// <summary>
    /// SPEC: write+read roundtrip preserves every field — gist, structured
    /// keyword list, both register dicts, outcome vector + scalar valence,
    /// turn metadata, and the embedding blob.
    /// </summary>
    [Fact]
    public async Task SaveAsync_RoundtripsAllFields()
    {
        var original = SampleRecord();

        await _store.SaveAsync(original);
        var loaded = await _store.GetByIdAsync(original.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(original.Id);
        loaded.ThreadId.Should().Be(original.ThreadId);
        loaded.ClosedAt.Should().BeCloseTo(original.ClosedAt, TimeSpan.FromMilliseconds(1));
        loaded.Gist.Should().Be(original.Gist);
        loaded.TopicKeywords.Should().Equal(original.TopicKeywords);
        loaded.MarkRegister.Should().Equal(original.MarkRegister);
        loaded.AniRegister.Should().Equal(original.AniRegister);
        loaded.OutcomeSignalSeedVector.Should().Equal(original.OutcomeSignalSeedVector);
        loaded.OutcomeSignalValence.Should().BeApproximately(original.OutcomeSignalValence, 0.0001f);
        loaded.TurnCount.Should().Be(original.TurnCount);
        loaded.DurationSeconds.Should().Be(original.DurationSeconds);
        loaded.Embedding.Should().Equal(original.Embedding);
    }

    /// <summary>
    /// SPEC: SaveAsync is idempotent on Id (UPSERT semantics). Re-saving
    /// the same record with mutated fields replaces the existing row.
    /// </summary>
    [Fact]
    public async Task SaveAsync_OnConflictId_UpsertsRow()
    {
        var record = SampleRecord(valence: 0.4f);
        await _store.SaveAsync(record);

        record.OutcomeSignalValence = -0.7f;
        record.Gist                 = "rewrite of the gist";
        await _store.SaveAsync(record);

        var loaded = await _store.GetByIdAsync(record.Id);
        loaded.Should().NotBeNull();
        loaded!.OutcomeSignalValence.Should().BeApproximately(-0.7f, 0.0001f);
        loaded.Gist.Should().Be("rewrite of the gist");

        var all = await _store.GetRecentAsync(10);
        all.Should().HaveCount(1, "UPSERT on conflict — no second row created");
    }

    /// <summary>
    /// SPEC: GetByThreadIdAsync returns the record for that thread; null
    /// when no record exists for the thread. There is at most one record
    /// per thread by design (one close event).
    /// </summary>
    [Fact]
    public async Task GetByThreadIdAsync_FindsByThreadId()
    {
        var threadId = Guid.NewGuid();
        var record   = SampleRecord(threadId: threadId);

        await _store.SaveAsync(record);
        var loaded = await _store.GetByThreadIdAsync(threadId);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(record.Id);
    }

    [Fact]
    public async Task GetByThreadIdAsync_UnknownThread_ReturnsNull()
    {
        var loaded = await _store.GetByThreadIdAsync(Guid.NewGuid());
        loaded.Should().BeNull();
    }

    /// <summary>
    /// SPEC: GetRecentAsync orders by closed_at DESC and respects the
    /// limit. The most recently-closed records come first; this is the
    /// V1.4 outreach prompt's primary read pattern.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_OrdersByClosedAtDescAndRespectsLimit()
    {
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        await _store.SaveAsync(SampleRecord(closedAt: t0,                  gist: "oldest"));
        await _store.SaveAsync(SampleRecord(closedAt: t0.AddMinutes(60),  gist: "middle"));
        await _store.SaveAsync(SampleRecord(closedAt: t0.AddMinutes(120), gist: "newest"));

        var topTwo = (await _store.GetRecentAsync(2)).ToList();

        topTwo.Should().HaveCount(2);
        topTwo[0].Gist.Should().Be("newest", "most recent must come first");
        topTwo[1].Gist.Should().Be("middle");
    }

    /// <summary>
    /// SPEC: NULL embedding survives the roundtrip. The summarizer pipeline
    /// (V1.2) is what populates Embedding; an early-write before embedding
    /// is computed must persist + read back as null.
    /// </summary>
    [Fact]
    public async Task SaveAsync_NullEmbedding_RoundtripsAsNull()
    {
        var record = SampleRecord();
        record.Embedding = null;

        await _store.SaveAsync(record);
        var loaded = await _store.GetByIdAsync(record.Id);

        loaded.Should().NotBeNull();
        loaded!.Embedding.Should().BeNull();
    }

    /// <summary>
    /// J.5h-data SPEC (Issue #47): validity defaults to "valid" on a fresh
    /// record and round-trips through Save → Read.
    /// </summary>
    [Fact]
    public async Task SaveAsync_DefaultValidity_RoundtripsAsValid()
    {
        var record = SampleRecord();
        record.Validity.Should().Be("valid", "fresh-record default per the model");

        await _store.SaveAsync(record);
        var loaded = await _store.GetByIdAsync(record.Id);

        loaded.Should().NotBeNull();
        loaded!.Validity.Should().Be("valid");
    }

    /// <summary>
    /// J.5h-data SPEC: explicit validity value (e.g., "invalid_fabrication")
    /// round-trips through Save → Read when the audit override is set.
    /// </summary>
    [Fact]
    public async Task SaveAsync_ExplicitValidity_RoundtripsExactly()
    {
        var record = SampleRecord();
        record.Validity = "invalid_fabrication";

        await _store.SaveAsync(record);
        var loaded = await _store.GetByIdIncludingInvalidAsync(record.Id);

        loaded.Should().NotBeNull();
        loaded!.Validity.Should().Be("invalid_fabrication");
    }

    /// <summary>
    /// J.5h-data SPEC: by default, GetByIdAsync filters out non-valid
    /// records. Substrate consumers never see quarantined fabrications.
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_InvalidRecord_DefaultFilterHidesIt()
    {
        var record = SampleRecord();
        record.Validity = "invalid_fabrication";
        await _store.SaveAsync(record);

        var hidden = await _store.GetByIdAsync(record.Id);
        hidden.Should().BeNull("default filter must exclude non-valid records");

        var visible = await _store.GetByIdIncludingInvalidAsync(record.Id);
        visible.Should().NotBeNull("audit override surfaces quarantined records for paper-figure / audit work");
        visible!.Validity.Should().Be("invalid_fabrication");
    }

    /// <summary>
    /// J.5h-data SPEC: GetByThreadIdAsync also default-filters; invalid
    /// records don't surface through thread-lookup either.
    /// </summary>
    [Fact]
    public async Task GetByThreadIdAsync_InvalidRecord_DefaultFilterHidesIt()
    {
        var threadId = Guid.NewGuid();
        var record   = SampleRecord(threadId: threadId);
        record.Validity = "invalid_fabrication";
        await _store.SaveAsync(record);

        (await _store.GetByThreadIdAsync(threadId)).Should().BeNull();
        (await _store.GetByThreadIdIncludingInvalidAsync(threadId)).Should().NotBeNull();
    }

    /// <summary>
    /// J.5h-data SPEC: GetRecentAsync default-excludes non-valid records;
    /// GetRecentIncludingInvalidAsync returns them. This is the load-bearing read
    /// path — V1.5 bias + outreach grounding go through here.
    /// </summary>
    [Fact]
    public async Task GetRecentAsync_DefaultExcludesInvalid_IncludeInvalidReturnsAll()
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var validRecord   = SampleRecord(closedAt: t0.AddMinutes(20), gist: "valid record");
        var invalidRecord = SampleRecord(closedAt: t0.AddMinutes(10), gist: "invalid record");
        invalidRecord.Validity = "invalid_fabrication";

        await _store.SaveAsync(validRecord);
        await _store.SaveAsync(invalidRecord);

        var defaultRead = (await _store.GetRecentAsync(10)).ToList();
        defaultRead.Should().HaveCount(1, "default filter must exclude non-valid");
        defaultRead[0].Gist.Should().Be("valid record");

        var fullRead = (await _store.GetRecentIncludingInvalidAsync(10)).ToList();
        fullRead.Should().HaveCount(2, "audit override surfaces both");
        fullRead.Select(r => r.Validity).Should().BeEquivalentTo(new[] { "valid", "invalid_fabrication" });
    }

    /// <summary>
    /// J.5h-data SPEC: UPSERT preserves the validity value through
    /// re-saves. A re-summarization pass that updates the gist must NOT
    /// accidentally re-validate a previously-quarantined record unless it
    /// explicitly sets Validity = "valid".
    /// </summary>
    [Fact]
    public async Task SaveAsync_OnConflictId_PreservesExplicitValidity()
    {
        var record = SampleRecord();
        record.Validity = "invalid_fabrication";
        await _store.SaveAsync(record);

        record.Gist = "rewritten gist content";
        // Validity left as invalid_fabrication — the rewrite should NOT
        // silently re-validate.
        await _store.SaveAsync(record);

        var loaded = await _store.GetByIdIncludingInvalidAsync(record.Id);
        loaded.Should().NotBeNull();
        loaded!.Validity.Should().Be("invalid_fabrication");
        loaded.Gist.Should().Be("rewritten gist content");
    }



    /// <summary>
    /// SPEC: empty register dicts and empty topic-keyword lists roundtrip
    /// as empty collections, not null. Defensive against JSON-deserialise
    /// quirks.
    /// </summary>
    [Fact]
    public async Task SaveAsync_EmptyCollections_RoundtripAsEmpty()
    {
        var record = SampleRecord();
        record.TopicKeywords           = new();
        record.MarkRegister            = new();
        record.AniRegister             = new();
        record.OutcomeSignalSeedVector = new();

        await _store.SaveAsync(record);
        var loaded = await _store.GetByIdAsync(record.Id);

        loaded.Should().NotBeNull();
        loaded!.TopicKeywords.Should().BeEmpty();
        loaded.MarkRegister.Should().BeEmpty();
        loaded.AniRegister.Should().BeEmpty();
        loaded.OutcomeSignalSeedVector.Should().BeEmpty();
    }
}
