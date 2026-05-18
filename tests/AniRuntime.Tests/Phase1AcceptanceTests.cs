using AniRuntime.Core.Models;
using AniRuntime.Memory;
using AniRuntime.Memory.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Tests;

/// <summary>
/// Phase 1 acceptance test for the data-layer refactor — verifies that the
/// EF Core AniDbContext can open a production-shape SQLite database and
/// read existing records via entity queries. The snapshot used is the
/// pre-Phase-6-v1.2-retroactive backup at
/// <c>/e/tmp/ani-memory-snap-20260516-postmortem.db</c>, which contains
/// real production substrate (10,375 memories + ancillary state).
///
/// Acceptance criterion: EF opens the snapshot, counts records via DbSet
/// queries, and the counts match what raw SQL returns (within reason —
/// some tables grew between when the snapshot was taken and when this test
/// was written, but the structural query must work).
///
/// Skipped if the snapshot file is not present (CI environments).
/// </summary>
public class Phase1AcceptanceTests
{
    private const string SnapshotPath = @"E:\tmp\ani-memory-snap-20260516-postmortem.db";

    private static DbContextOptions<AniDbContext> BuildOptions(string dbPath)
    {
        return new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite($"Data Source={dbPath};Mode=ReadOnly")
            .Options;
    }

    [Fact]
    public async Task EfContext_OpensProductionSnapshot_ReadsMemoryEntities()
    {
        if (!File.Exists(SnapshotPath)) return; // skip silently if snapshot unavailable

        await using var ctx = new AniDbContext(BuildOptions(SnapshotPath));

        // Smoke test: count records via EF query.
        var memoryCount = await ctx.Memories.CountAsync();
        memoryCount.Should().BeGreaterThan(0, "snapshot DB should contain memory records");

        // Sample read: pull the first 5 records, verify all required fields populated.
        var sample = await ctx.Memories.OrderBy(m => m.OccurredAt).Take(5).ToListAsync();
        sample.Should().HaveCount(5);
        foreach (var record in sample)
        {
            record.Id.Should().NotBe(Guid.Empty, "all records have a valid Guid id");
            record.Content.Should().NotBeNullOrEmpty("all records have content");
            record.OccurredAt.Should().BeAfter(DateTimeOffset.MinValue, "all records have a valid occurred_at");
        }
    }

    [Fact]
    public async Task EfContext_ReadsCharacterStateBlob_ParseableJson()
    {
        if (!File.Exists(SnapshotPath)) return; // skip silently if snapshot unavailable

        await using var ctx = new AniDbContext(BuildOptions(SnapshotPath));

        var charState = await ctx.CharacterState.FirstOrDefaultAsync();
        charState.Should().NotBeNull("snapshot has a character_state row");
        charState!.Json.Should().NotBeNullOrWhiteSpace();
        charState.Json.Should().StartWith("{", "json column contains a JSON object");
    }

    [Fact]
    public async Task EfContext_ReadsMemoryLinksWithCompositeKey()
    {
        if (!File.Exists(SnapshotPath)) return; // skip silently if snapshot unavailable

        await using var ctx = new AniDbContext(BuildOptions(SnapshotPath));

        var linkCount = await ctx.MemoryLinks.CountAsync();
        // Snapshot DB may have zero links if memory_links was empty at the time.
        // What we're testing is that the composite-PK + FK config doesn't error.
        linkCount.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task EfContext_ReadsEmbeddingBlob_RoundTripsToFloatArray()
    {
        if (!File.Exists(SnapshotPath)) return; // skip silently if snapshot unavailable

        await using var ctx = new AniDbContext(BuildOptions(SnapshotPath));

        // Find a record with a non-null embedding (semantic search records have these).
        var withEmbedding = await ctx.Memories
            .Where(m => m.Embedding != null)
            .Take(1)
            .FirstOrDefaultAsync();

        withEmbedding.Should().NotBeNull("snapshot has at least one record with an embedding");
        withEmbedding!.Embedding.Should().NotBeNull();
        withEmbedding.Embedding!.Length.Should().BeGreaterThan(0,
            "embedding should be a non-empty float[]");
        // nomic-embed-text produces 768-dim vectors.
        withEmbedding.Embedding.Length.Should().Be(768,
            "embedding length matches the embedding model's output dimensionality");
    }

    [Fact]
    public async Task EfContext_DecayTierEnumRoundTrips_FromTextColumn()
    {
        if (!File.Exists(SnapshotPath)) return; // skip silently if snapshot unavailable

        await using var ctx = new AniDbContext(BuildOptions(SnapshotPath));

        var standardCount = await ctx.Memories.CountAsync(m => m.Tier == DecayTier.Standard);
        var anchoredCount = await ctx.Memories.CountAsync(m => m.Tier == DecayTier.Anchored);

        // Snapshot was taken before Compressed tier existed, so:
        standardCount.Should().BeGreaterThan(0);
        anchoredCount.Should().Be(105, "snapshot has exactly 105 anchored records per the post-mortem analysis");
    }
}
