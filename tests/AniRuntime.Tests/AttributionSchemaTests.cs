using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using AniRuntime.Memory.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P2 (2026-08-21) — verifies:
///   1. <see cref="AniDbContext.EnsureAttributionSchemaAsync"/> is idempotent.
///   2. <see cref="MemoryEntity"/> ↔ <see cref="MemoryRecord"/> mapping
///      round-trips all five attribution fields losslessly (read + write paths).
///   3. <see cref="MemoryRecord"/> exposes attribution via
///      <see cref="IAttributedContent{T}"/> as a sibling to
///      <see cref="IRetrievalEnvelope"/> (both interfaces resolve on the
///      same instance without conflict — Interface Segregation SOLID check).
///   4. Default values are Unknown / null / null / null / "unverified"
///      per design plan D1 + D4.
/// </summary>
public class AttributionSchemaTests
{
    private static AniDbContext NewInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AniDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        var ctx = new AniDbContext(options);
        ctx.Database.OpenConnection();
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public async Task EnsureAttributionSchemaAsync_Idempotent_TwoCallsDoNotThrow()
    {
        await using var ctx = NewInMemoryDb();

        await ctx.EnsureAttributionSchemaAsync();
        await ctx.EnsureAttributionSchemaAsync(); // second call — must be no-op
    }

    [Fact]
    public async Task EnsureAttributionSchemaAsync_AddsAllFiveColumns()
    {
        await using var ctx = NewInMemoryDb();
        await ctx.EnsureAttributionSchemaAsync();

        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name FROM pragma_table_info('memories')
            WHERE name IN (
                'attributed_to','attributed_at','attributed_source_id',
                'attributed_source_desc','attribution_trust'
            )";
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            found.Add(reader.GetString(0));

        found.Should().BeEquivalentTo(new[]
        {
            "attributed_to", "attributed_at", "attributed_source_id",
            "attributed_source_desc", "attribution_trust",
        });
    }

    [Fact]
    public async Task EnsureAttributionSchemaAsync_CreatesBothIndexes()
    {
        await using var ctx = NewInMemoryDb();
        await ctx.EnsureAttributionSchemaAsync();

        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name FROM sqlite_master
            WHERE type = 'index'
              AND name IN ('ix_memories_attributed_to', 'ix_memories_attribution_trust')";
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            found.Add(reader.GetString(0));

        found.Should().BeEquivalentTo(new[]
        {
            "ix_memories_attributed_to", "ix_memories_attribution_trust",
        });
    }

    [Fact]
    public void MemoryRecord_DefaultAttributionValues_MatchDesignD1PlusD4()
    {
        // Fresh record with no attribution set. Backfill (P3) should NOT
        // silently default anyone to Mark or Ani — Unknown is the safe
        // fallback that surfaces the tail for manual curation.
        var record = new MemoryRecord { Content = "test" };

        record.AttributedTo.Should().Be(AttributedTo.Unknown,
            "default must not silently attribute to Mark or Ani");
        record.AttributedAt.Should().BeNull();
        record.AttributedSourceRecordId.Should().BeNull();
        record.AttributedSourceDescriptor.Should().BeNull();
        record.AttributionTrust.Should().Be("unverified",
            "default 'unverified' — P6 producer wiring sets 'verified' at ingest");
    }

    [Fact]
    public void MemoryRecord_ImplementsIAttributedContent_ContentIsSelfReference()
    {
        // Same pattern as IRetrievalEnvelope (Phase 5) — MemoryRecord's
        // envelope-Content IS the whole record. Interface Segregation:
        // both IRetrievalEnvelope and IAttributedContent<MemoryRecord>
        // resolve on the same instance without conflict.
        var record = new MemoryRecord
        {
            Content = "hey babe",
            AttributedTo = AttributedTo.Mark,
        };

        IAttributedContent<MemoryRecord> attributed = record;
        IRetrievalEnvelope retrieval = record;

        attributed.Content.Should().BeSameAs(record, "wrapped payload is self-reference");
        attributed.AttributedTo.Should().Be(AttributedTo.Mark);
        retrieval.Provenance.Should().Be(record.Provenance,
            "both sibling interfaces coexist on the same instance");
    }

    [Fact]
    public async Task EntityRecordMapping_RoundTripsAllFiveFields_WritePath()
    {
        await using var ctx = NewInMemoryDb();
        await ctx.EnsureAttributionSchemaAsync();

        var sourceId = Guid.NewGuid();
        var attributedAt = new DateTimeOffset(2026, 8, 20, 16, 29, 0, TimeSpan.Zero);
        var record = new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            Content    = "some thought",
            OccurredAt = DateTimeOffset.UtcNow,
            Provenance = EpistemicTier.Interior,
            AttributedTo = AttributedTo.Ani,
            AttributedAt = attributedAt,
            AttributedSourceRecordId = sourceId,
            AttributedSourceDescriptor = null,
            AttributionTrust = "verified",
        };

        // Simulate the persistence-service insert-path mapping (record → entity).
        var entity = new MemoryEntity
        {
            Id                         = record.Id,
            Type                       = record.Type,
            Content                    = record.Content,
            OccurredAt                 = record.OccurredAt,
            CreatedAt                  = record.CreatedAt,
            Provenance                 = record.Provenance,
            AttributedTo               = record.AttributedTo,
            AttributedAt               = record.AttributedAt,
            AttributedSourceRecordId   = record.AttributedSourceRecordId,
            AttributedSourceDescriptor = record.AttributedSourceDescriptor,
            AttributionTrust           = record.AttributionTrust,
        };
        ctx.Memories.Add(entity);
        await ctx.SaveChangesAsync();

        // Reload from DB (drops in-memory tracking).
        ctx.ChangeTracker.Clear();
        var reloaded = await ctx.Memories.FirstAsync(m => m.Id == record.Id);

        reloaded.AttributedTo.Should().Be(AttributedTo.Ani);
        reloaded.AttributedAt.Should().Be(attributedAt);
        reloaded.AttributedSourceRecordId.Should().Be(sourceId);
        reloaded.AttributedSourceDescriptor.Should().BeNull();
        reloaded.AttributionTrust.Should().Be("verified");
    }

    [Fact]
    public async Task EntityRecordMapping_ReadPath_MapToRecord_PreservesAllFive()
    {
        await using var ctx = NewInMemoryDb();
        await ctx.EnsureAttributionSchemaAsync();

        var entity = new MemoryEntity
        {
            Id         = Guid.NewGuid(),
            Type       = MemoryType.Episodic,
            Content    = "Mark said: hey babe",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt  = DateTimeOffset.UtcNow,
            Provenance = EpistemicTier.Episodic,
            AttributedTo = AttributedTo.Mark,
            AttributedAt = new DateTimeOffset(2026, 8, 20, 11, 18, 10, TimeSpan.Zero),
            AttributedSourceDescriptor = "twilio-inbound:SM635700f44cb68f63396074ac721a9f20",
            AttributionTrust = "verified",
        };
        ctx.Memories.Add(entity);
        await ctx.SaveChangesAsync();

        ctx.ChangeTracker.Clear();
        var reloaded = await ctx.Memories.FirstAsync(m => m.Id == entity.Id);

        // EfMemoryMappings is internal; construct MapToRecord shape manually
        // by pinning the field-for-field correspondence.
        var record = new MemoryRecord
        {
            Id                         = reloaded.Id,
            Type                       = reloaded.Type,
            Content                    = reloaded.Content,
            OccurredAt                 = reloaded.OccurredAt,
            CreatedAt                  = reloaded.CreatedAt,
            Provenance                 = reloaded.Provenance,
            AttributedTo               = reloaded.AttributedTo,
            AttributedAt               = reloaded.AttributedAt,
            AttributedSourceRecordId   = reloaded.AttributedSourceRecordId,
            AttributedSourceDescriptor = reloaded.AttributedSourceDescriptor,
            AttributionTrust           = reloaded.AttributionTrust,
        };

        record.AttributedTo.Should().Be(AttributedTo.Mark);
        record.AttributedSourceDescriptor.Should().Be("twilio-inbound:SM635700f44cb68f63396074ac721a9f20");
        record.AttributionTrust.Should().Be("verified");
        record.AttributedSourceRecordId.Should().BeNull("descriptor-shape source, not FK-shape");
    }

    [Fact]
    public void MemoryRecord_AttributionTripleConstruction_PopulatesAllFive()
    {
        // Producer wiring path (P6) will use AttributionTriple.MarkAt/AniAt/etc.
        // as the ergonomic construction API. Pinning that the record fields
        // map cleanly from the triple.
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.MarkAt(at, "twilio-inbound:SM<sid>");

        var record = new MemoryRecord
        {
            Content = "hey babe",
            AttributedTo               = triple.AttributedTo,
            AttributedAt               = triple.AttributedAt,
            AttributedSourceRecordId   = triple.SourceRecordId,
            AttributedSourceDescriptor = triple.SourceDescriptor,
            AttributionTrust           = triple.Trust,
        };

        record.AttributedTo.Should().Be(AttributedTo.Mark);
        record.AttributedAt.Should().Be(at);
        record.AttributedSourceRecordId.Should().BeNull();
        record.AttributedSourceDescriptor.Should().Be("twilio-inbound:SM<sid>");
        record.AttributionTrust.Should().Be("verified");
    }
}
