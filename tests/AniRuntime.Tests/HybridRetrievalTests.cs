using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #93 Phase 4 (2026-07-09) — contract tests for hybrid RRF retrieval.
/// Two shapes here: pure-function tests of the fusion helper, and a
/// SQLite/FTS5 integration test that exercises the end-to-end path against
/// an in-memory DB seeded with a synthetic version of the empirical anchor
/// (Interior record about "back from teaching" + WCTC confirmation record).
///
/// The empirical validation on the real snapshot lives in the Phase 4
/// commit message + docs/research/ANI-Retrieval-Consultation-2026-07-08.md;
/// these tests exist to prevent regression of the wiring, not to prove
/// the retrieval quality gain (which is measured against real data).
/// </summary>
public class HybridRetrievalTests
{
    // ─── SanitizeFtsQuery ─────────────────────────────────────────────────
    //
    // Contract: tokenize the input, drop single-letter tokens + a small
    // stopword list, dedupe, and join with " OR " so FTS5's MATCH
    // becomes "any of these terms" (BM25-native semantics) instead of
    // the default implicit AND (which matches near-zero records for
    // long natural-language queries).

    [Fact]
    public void SanitizeFtsQuery_JoinsTokensWithOr()
    {
        EfSemanticSearchComposer.SanitizeFtsQuery("hey babe back from teaching!")
            .Should().Be("hey OR babe OR back OR teaching",
                because: "OR-joined so BM25 can rank records that contain ANY term");
    }

    [Fact]
    public void SanitizeFtsQuery_DropsStopwords()
    {
        EfSemanticSearchComposer.SanitizeFtsQuery("i was just checking in on you")
            .Should().Be("checking",
                because: "stopwords like 'i', 'was', 'just', 'in', 'on', 'you' are filtered");
    }

    [Fact]
    public void SanitizeFtsQuery_DropsPunctuationAndDedupes()
    {
        EfSemanticSearchComposer.SanitizeFtsQuery("WCTC! WCTC? Waukesha, WCTC.")
            .Should().Be("wctc OR waukesha");
    }

    [Fact]
    public void SanitizeFtsQuery_DropsSingleLetterTokens()
    {
        EfSemanticSearchComposer.SanitizeFtsQuery("a b c teaching x y z")
            .Should().Be("teaching");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a i o")]  // all single letters
    [InlineData("the and or but")]  // all stopwords
    public void SanitizeFtsQuery_EmptyOrAllFilteredReturnsEmpty(string input)
    {
        EfSemanticSearchComposer.SanitizeFtsQuery(input).Should().BeEmpty();
    }

    // ─── FuseByRrf — contract behavior ────────────────────────────────────

    private static ScoredMemory Make(float composite, string content, Guid? id = null)
    {
        return new ScoredMemory(
            new MemoryRecord
            {
                Id      = id ?? Guid.NewGuid(),
                Type    = MemoryType.Semantic,
                Content = content,
            },
            composite,
            0f);
    }

    [Fact]
    public void FuseByRrf_EmptyList_ReturnsEmpty()
    {
        var result = EfSemanticSearchComposer.FuseByRrf(
            new List<ScoredMemory>(), new Dictionary<Guid, int>(),
            topK: 5, k: 60, out var rescues);
        result.Should().BeEmpty();
        rescues.Should().Be(0);
    }

    [Fact]
    public void FuseByRrf_EmptyBm25Map_FallsBackToCompositeOrder()
    {
        var a = Make(0.9f, "a");
        var b = Make(0.7f, "b");
        var c = Make(0.5f, "c");

        var result = EfSemanticSearchComposer.FuseByRrf(
            new List<ScoredMemory> { c, a, b },
            new Dictionary<Guid, int>(),
            topK: 3, k: 60, out var rescues);

        result.Should().HaveCount(3);
        result[0].Record.Content.Should().Be("a", "highest composite wins when BM25 map empty");
        result[1].Record.Content.Should().Be("b");
        result[2].Record.Content.Should().Be("c");
        rescues.Should().Be(0, "no BM25 signal means no rescues");
    }

    [Fact]
    public void FuseByRrf_BM25TopHitOverridesCompositeRank47()
    {
        // The empirical anchor case, synthesised.
        // "wctc" has composite rank 3 (of 3), BM25 rank 1.
        // Two emotional character-seed records have composite rank 1 and 2 but
        // no BM25 rank. topK=2. WCTC record must appear in top-2 via RRF.
        var seedA = Make(0.90f, "seed-A");
        var seedB = Make(0.85f, "seed-B");
        var wctc  = Make(0.60f, "wctc");

        var bm25 = new Dictionary<Guid, int>
        {
            { wctc.Record.Id, 1 },
        };

        var result = EfSemanticSearchComposer.FuseByRrf(
            new List<ScoredMemory> { seedA, seedB, wctc },
            bm25,
            topK: 2, k: 60, out var rescues);

        result.Should().HaveCount(2);
        result.Select(r => r.Record.Content).Should().Contain("wctc",
            "BM25 rank 1 rescues the entity-relevant record into top-2");
        rescues.Should().Be(1, "wctc was not in composite top-2 but landed in fused top-2");
    }

    [Fact]
    public void FuseByRrf_RecordInBothTopsPromotesAboveComposite()
    {
        // A record that ranks 1 in both composite and BM25 should stay #1.
        // A record only in composite top-K should still be present.
        var both = Make(0.95f, "both");
        var compositeOnly = Make(0.80f, "compositeOnly");
        var bm25Only = Make(0.50f, "bm25Only");

        var bm25 = new Dictionary<Guid, int>
        {
            { both.Record.Id, 1 },
            { bm25Only.Record.Id, 2 },
        };

        var result = EfSemanticSearchComposer.FuseByRrf(
            new List<ScoredMemory> { both, compositeOnly, bm25Only },
            bm25,
            topK: 2, k: 60, out var _);

        result[0].Record.Content.Should().Be("both");
        result[1].Record.Content.Should().Be("bm25Only", "bm25 rank 2 outranks composite rank 2 alone");
    }

    // ─── FTS5 integration ─────────────────────────────────────────────────

    [Fact]
    public async Task EnsureFtsIndexAsync_CreatesVirtualTableAndBackfills()
    {
        var options = new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        await using var ctx = new AniDbContext(options);
        await ctx.Database.OpenConnectionAsync();
        await ctx.Database.EnsureCreatedAsync();

        // Pre-seed a few memories.
        ctx.Memories.AddRange(
            new Memory.Entities.MemoryEntity
            {
                Id = Guid.NewGuid(), Type = MemoryType.Semantic,
                Content = "Mark texted: hey babe! back from teaching! 10pm now",
                OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                Provenance = EpistemicTier.Facts, Validity = "valid",
            },
            new Memory.Entities.MemoryEntity
            {
                Id = Guid.NewGuid(), Type = MemoryType.Semantic,
                Content = "hazel eyes the kind that make me want to paint walls to match",
                OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                Provenance = EpistemicTier.Facts, Validity = "valid",
            });
        await ctx.SaveChangesAsync();

        // Init.
        await ctx.EnsureFtsIndexAsync();

        // Verify the FTS5 table indexes the seeded content.
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memories_fts";
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        count.Should().Be(2);

        // Verify BM25 can match. "teaching" should hit only the first record.
        cmd.CommandText = "SELECT memory_id FROM memories_fts WHERE memories_fts MATCH 'teaching' ORDER BY bm25(memories_fts)";
        await using var reader = await cmd.ExecuteReaderAsync();
        var matched = new List<string>();
        while (await reader.ReadAsync())
            matched.Add(reader.GetString(0));
        matched.Should().HaveCount(1, "only one seeded record contains 'teaching'");
    }

    [Fact]
    public async Task EnsureFtsIndexAsync_IsIdempotent()
    {
        var options = new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        await using var ctx = new AniDbContext(options);
        await ctx.Database.OpenConnectionAsync();
        await ctx.Database.EnsureCreatedAsync();

        // Two consecutive calls must not throw.
        await ctx.EnsureFtsIndexAsync();
        await ctx.EnsureFtsIndexAsync();
    }

    [Fact]
    public async Task InsertTrigger_KeepsFtsSynced()
    {
        var options = new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        await using var ctx = new AniDbContext(options);
        await ctx.Database.OpenConnectionAsync();
        await ctx.Database.EnsureCreatedAsync();
        await ctx.EnsureFtsIndexAsync();

        // Insert AFTER init — trigger must propagate to FTS5.
        var id = Guid.NewGuid();
        ctx.Memories.Add(new Memory.Entities.MemoryEntity
        {
            Id = id, Type = MemoryType.Semantic,
            Content = "Waukesha County Technical College is where the classes happen",
            OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            Provenance = EpistemicTier.Facts, Validity = "valid",
        });
        await ctx.SaveChangesAsync();

        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT memory_id FROM memories_fts WHERE memories_fts MATCH 'Waukesha'";
        var mid = (string?)await cmd.ExecuteScalarAsync();
        mid.Should().Be(id.ToString(), "trigger must have propagated the insert to FTS5");
    }
}
