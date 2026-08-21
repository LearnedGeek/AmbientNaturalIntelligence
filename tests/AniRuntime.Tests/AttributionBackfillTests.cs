using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using AniRuntime.Memory.Backfill;
using AniRuntime.Memory.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P3 (2026-08-21) — verifies the
/// backfill heuristic (<see cref="AttributionBackfill.InferAttribution"/>)
/// against every row of the D4 heuristic table from the design plan.
///
/// <para>
/// Heuristic is a pure function — no DB access, no I/O. Tests hit it
/// directly with fixture <see cref="MemoryEntity"/> instances. Runner-level
/// integration is exercised separately by <see cref="RunAsync_UpdatesRecords_IdempotentGuardHoldsOnSecondRun"/>.
/// </para>
/// </summary>
public class AttributionBackfillTests
{
    private static MemoryEntity Entity(
        EpistemicTier provenance,
        string?       sourceName = null,
        string?       content    = null,
        MemoryType    type       = MemoryType.Episodic)
    {
        return new MemoryEntity
        {
            Id         = Guid.NewGuid(),
            Type       = type,
            Content    = content ?? "",
            Provenance = provenance,
            SourceName = sourceName,
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt  = DateTimeOffset.UtcNow,
        };
    }

    // ── D4 heuristic table rows ──────────────────────────────────────

    [Fact]
    public void Facts_CharacterSeed_AttributesToMarkCanonical()
    {
        var e = Entity(EpistemicTier.Facts, sourceName: "character-seed");

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.Mark);
        triple.AttributedAt.Should().BeNull("canonical content is timeless");
        triple.Trust.Should().Be("verified");
        triple.SourceDescriptor.Should().StartWith("character-seed:");
    }

    [Fact]
    public void Facts_TwilioInbound_AttributesToMarkWithTimestamp()
    {
        var e = Entity(EpistemicTier.Facts, sourceName: "twilio-inbound");

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.Mark);
        triple.AttributedAt.Should().Be(e.OccurredAt);
        triple.Trust.Should().Be("verified");
        triple.SourceDescriptor.Should().StartWith("twilio-inbound:");
    }

    [Theory]
    [InlineData("rss")]
    [InlineData("weather")]
    [InlineData("time")]
    [InlineData("contact-state")]
    public void Facts_WorldSources_AttributeToWorld(string sourceName)
    {
        var e = Entity(EpistemicTier.Facts, sourceName: sourceName);

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.World);
        triple.AttributedAt.Should().Be(e.OccurredAt);
        triple.Trust.Should().Be("verified");
        triple.SourceDescriptor.Should().StartWith($"{sourceName}:");
    }

    [Fact]
    public void ReflectionSource_AttributesToAni_RegardlessOfTier()
    {
        // "reflection" SourceName trumps provenance-based inference
        // because reflections are always Ani's synthesis output.
        var e = Entity(EpistemicTier.Facts, sourceName: "reflection");

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.AttributedAt.Should().Be(e.OccurredAt);
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void Episodic_MarkSaidPrefix_AttributesToMark()
    {
        var e = Entity(EpistemicTier.Episodic, content: "Mark said: hey babe how was your day");

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.Mark);
        triple.AttributedAt.Should().Be(e.OccurredAt);
        triple.Trust.Should().Be("verified");
        triple.SourceDescriptor.Should().Be("episodic-prefix-inferred");
    }

    [Fact]
    public void Episodic_IReachedOutPrefix_AttributesToAni()
    {
        var e = Entity(EpistemicTier.Episodic,
            content: "I reached out to Mark: \"thinking about you\"");

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.AttributedAt.Should().Be(e.OccurredAt);
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void Episodic_UnknownPrefix_LandsAsUnknownHistorical()
    {
        // System-generated conversation summaries, multi-turn blocks,
        // etc. can't be inferred from prefix. Manual-curation tail.
        var e = Entity(EpistemicTier.Episodic, content: "Conversation (5 messages): ...");

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.Unknown);
        triple.Trust.Should().Be("unverified-historical");
    }

    [Fact]
    public void Interior_AttributesToAniUnverifiedHistorical()
    {
        // 12:04-shape corruption class: Interior record's OWN author is
        // trivially Ani (Interior tier == Ani-authored) but internal
        // content claims cannot be retroactively verified.
        var e = Entity(EpistemicTier.Interior,
            content: "I keep replaying how you said 'mmm baby you're back'",
            type: MemoryType.InnerThought);

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.AttributedAt.Should().BeNull();
        triple.SourceRecordId.Should().BeNull();
        triple.SourceDescriptor.Should().BeNull();
        triple.Trust.Should().Be("unverified-historical",
            "internal 'you said X' claims from pre-F-2 cycles cannot be retroactively verified");
    }

    [Fact]
    public void UnknownProvenance_LandsAsUnknownHistorical()
    {
        // Unmatched shape — fallback to Unknown for manual review.
        var e = new MemoryEntity
        {
            Id         = Guid.NewGuid(),
            Provenance = (EpistemicTier)999,  // synthesized invalid tier
            Content    = "some content",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        var triple = AttributionBackfill.InferAttribution(e);

        triple.AttributedTo.Should().Be(AttributedTo.Unknown);
        triple.Trust.Should().Be("unverified-historical");
    }

    // ── Runner-level integration ─────────────────────────────────────

    private static IDbContextFactory<AniDbContext> InMemoryFactory()
    {
        // Shared in-memory DB across ContextFactory scopes so the runner's
        // ctxFactory.CreateDbContextAsync produces contexts pointing at
        // the same store. Use a named shared cache connection.
        var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"DataSource=file:memdb-{Guid.NewGuid():N}?mode=memory&cache=shared");
        conn.Open();
        var options = new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite(conn)
            .Options;
        using (var seed = new AniDbContext(options))
        {
            seed.Database.EnsureCreated();
        }
        return new SharedConnectionFactory(options);
    }

    private sealed class SharedConnectionFactory : IDbContextFactory<AniDbContext>
    {
        private readonly DbContextOptions<AniDbContext> _options;
        public SharedConnectionFactory(DbContextOptions<AniDbContext> options) => _options = options;
        public AniDbContext CreateDbContext() => new AniDbContext(_options);
    }

    [Fact]
    public async Task RunAsync_UpdatesRecords_IdempotentGuardHoldsOnSecondRun()
    {
        var factory = InMemoryFactory();

        // Seed a mix of records covering three heuristic paths.
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Memories.AddRange(
                new MemoryEntity
                {
                    Id = Guid.NewGuid(), Type = MemoryType.Perception,
                    Content = "Mark texted: hey", Provenance = EpistemicTier.Facts,
                    SourceName = "twilio-inbound",
                    OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                },
                new MemoryEntity
                {
                    Id = Guid.NewGuid(), Type = MemoryType.InnerThought,
                    Content = "quiet morning at the bookstore",
                    Provenance = EpistemicTier.Interior,
                    OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                },
                new MemoryEntity
                {
                    Id = Guid.NewGuid(), Type = MemoryType.Episodic,
                    Content = "Conversation (3 messages): ...",
                    Provenance = EpistemicTier.Episodic,
                    OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
                });
            await seed.SaveChangesAsync();
        }

        var log1 = new List<string>();
        var summary1 = await AttributionBackfill.RunAsync(
            factory, isDryRun: false, order: "oldest", limit: 0,
            logProgress: log1.Add);

        summary1.Loaded.Should().Be(3);
        summary1.Processed.Should().Be(3);
        summary1.Written.Should().Be(3, "all three records went from Unknown → some attribution");
        summary1.PerAuthor.Should().ContainKey("Mark");
        summary1.PerAuthor.Should().ContainKey("Ani");
        summary1.PerAuthor.Should().ContainKey("Unknown");
        summary1.PerTrust.Should().ContainKey("verified");
        summary1.PerTrust.Should().ContainKey("unverified-historical");

        // Second run: idempotent guard (WHERE attributed_to = 0) means
        // no rows are candidates anymore.
        var log2 = new List<string>();
        var summary2 = await AttributionBackfill.RunAsync(
            factory, isDryRun: false, order: "oldest", limit: 0,
            logProgress: log2.Add);

        summary2.Loaded.Should().Be(0, "first run already attributed all rows; none match Unknown anymore");
        summary2.Written.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DryRun_DoesNotMutateDatabase()
    {
        var factory = InMemoryFactory();
        Guid seedId;
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var e = new MemoryEntity
            {
                Id = Guid.NewGuid(), Type = MemoryType.InnerThought,
                Content = "quiet morning", Provenance = EpistemicTier.Interior,
                OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            };
            seedId = e.Id;
            seed.Memories.Add(e);
            await seed.SaveChangesAsync();
        }

        var summary = await AttributionBackfill.RunAsync(
            factory, isDryRun: true, order: "oldest", limit: 0,
            logProgress: _ => { });

        summary.Loaded.Should().Be(1);
        summary.Processed.Should().Be(1);
        summary.Written.Should().Be(0, "dry-run must not write");

        await using var verify = await factory.CreateDbContextAsync();
        var reloaded = await verify.Memories.FirstAsync(m => m.Id == seedId);
        reloaded.AttributedTo.Should().Be(AttributedTo.Unknown,
            "dry-run must leave attributed_to at default");
        reloaded.AttributionTrust.Should().Be("unverified",
            "dry-run must not change trust field");
    }
}
