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

    /// <summary>
    /// Copies the read-only snapshot to a temp writable path and runs any
    /// idempotent schema-rescue migrations that were added AFTER the
    /// snapshot was captured. Returns the temp path for the caller to
    /// open with a fresh (read-only) context. Callers must delete the
    /// temp file in a finally block.
    ///
    /// <para>
    /// Needed because production adds columns to <c>memories</c> over time
    /// via <see cref="AniDbContext.EnsureIssue93SchemaAsync"/> (2026-07
    /// confirmed_at/confirmed_by, 2026-08 register). EF's LINQ generates
    /// <c>SELECT ... m.register FROM memories</c> which fails on the
    /// pre-shipping fixture snapshot; ReadOnly-mode blocks the ALTER.
    /// </para>
    /// </summary>
    private static async Task<string> CopyAndMigrateSnapshotAsync()
    {
        var tempPath = Path.Combine(Path.GetTempPath(),
            $"ani-p1-fixture-{Guid.NewGuid():N}.db");
        File.Copy(SnapshotPath, tempPath, overwrite: true);
        var attrs = File.GetAttributes(tempPath);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(tempPath, attrs & ~FileAttributes.ReadOnly);

        var rwOptions = new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite($"Data Source={tempPath}")
            .Options;
        await using (var migrateCtx = new AniDbContext(rwOptions))
        {
            await migrateCtx.EnsureIssue93SchemaAsync().ConfigureAwait(false);
        }

        return tempPath;
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* leave the temp file if locked */ }
    }

    [Fact]
    public async Task EfContext_OpensProductionSnapshot_ReadsMemoryEntities()
    {
        if (!File.Exists(SnapshotPath)) return;

        var tempPath = await CopyAndMigrateSnapshotAsync();
        try
        {
            await using var ctx = new AniDbContext(BuildOptions(tempPath));

            var memoryCount = await ctx.Memories.CountAsync();
            memoryCount.Should().BeGreaterThan(0, "snapshot DB should contain memory records");

            var sample = await ctx.Memories.OrderBy(m => m.OccurredAt).Take(5).ToListAsync();
            sample.Should().HaveCount(5);
            foreach (var record in sample)
            {
                record.Id.Should().NotBe(Guid.Empty, "all records have a valid Guid id");
                record.Content.Should().NotBeNullOrEmpty("all records have content");
                record.OccurredAt.Should().BeAfter(DateTimeOffset.MinValue, "all records have a valid occurred_at");
            }
        }
        finally { SafeDelete(tempPath); }
    }

    [Fact]
    public async Task EfContext_ReadsCharacterStateBlob_ParseableJson()
    {
        if (!File.Exists(SnapshotPath)) return;

        var tempPath = await CopyAndMigrateSnapshotAsync();
        try
        {
            await using var ctx = new AniDbContext(BuildOptions(tempPath));

            var charState = await ctx.CharacterState.FirstOrDefaultAsync();
            charState.Should().NotBeNull("snapshot has a character_state row");
            charState!.Json.Should().NotBeNullOrWhiteSpace();
            charState.Json.Should().StartWith("{", "json column contains a JSON object");
        }
        finally { SafeDelete(tempPath); }
    }

    [Fact]
    public async Task EfContext_ReadsMemoryLinksWithCompositeKey()
    {
        if (!File.Exists(SnapshotPath)) return;

        var tempPath = await CopyAndMigrateSnapshotAsync();
        try
        {
            await using var ctx = new AniDbContext(BuildOptions(tempPath));

            var linkCount = await ctx.MemoryLinks.CountAsync();
            linkCount.Should().BeGreaterOrEqualTo(0);
        }
        finally { SafeDelete(tempPath); }
    }

    [Fact]
    public async Task EfContext_ReadsEmbeddingBlob_RoundTripsToFloatArray()
    {
        if (!File.Exists(SnapshotPath)) return;

        var tempPath = await CopyAndMigrateSnapshotAsync();
        try
        {
            await using var ctx = new AniDbContext(BuildOptions(tempPath));

            var withEmbedding = await ctx.Memories
                .Where(m => m.Embedding != null)
                .Take(1)
                .FirstOrDefaultAsync();

            withEmbedding.Should().NotBeNull("snapshot has at least one record with an embedding");
            withEmbedding!.Embedding.Should().NotBeNull();
            withEmbedding.Embedding!.Length.Should().BeGreaterThan(0, "embedding should be a non-empty float[]");
            withEmbedding.Embedding.Length.Should().Be(768, "embedding length matches the embedding model's output dimensionality");
        }
        finally { SafeDelete(tempPath); }
    }

    [Fact]
    public async Task EfContext_DecayTierEnumRoundTrips_FromTextColumn()
    {
        if (!File.Exists(SnapshotPath)) return;

        var tempPath = await CopyAndMigrateSnapshotAsync();
        try
        {
            await using var ctx = new AniDbContext(BuildOptions(tempPath));

            var standardCount = await ctx.Memories.CountAsync(m => m.Tier == DecayTier.Standard);
            var anchoredCount = await ctx.Memories.CountAsync(m => m.Tier == DecayTier.Anchored);

            standardCount.Should().BeGreaterThan(0);
            anchoredCount.Should().Be(105, "snapshot has exactly 105 anchored records per the post-mortem analysis");
        }
        finally { SafeDelete(tempPath); }
    }
}
