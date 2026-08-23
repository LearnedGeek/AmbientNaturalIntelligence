using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using AniRuntime.Memory.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P8 (2026-08-23) — verifies
/// <see cref="EfMemoryAnalyticsService.GetAttributionDistributionAsync"/>
/// returns correct grouped counts over the memories table. Backs the
/// dashboard <c>/api/v1/attribution/distribution</c> endpoint that
/// surfaces substrate attribution health.
/// </summary>
public class AttributionDistributionAnalyticsTests
{
    [Fact]
    public async Task GetAttributionDistribution_GroupsRowsByAttributedToAndTrust()
    {
        using var factory = InMemoryFactory();
        await using (var seed = factory.CreateDbContext())
        {
            // Seed a small mixed table: 3 Ani/verified, 2 Mark/verified,
            // 1 World/verified, 1 Unknown/unverified-historical.
            seed.Memories.Add(NewEntity(AttributedTo.Ani,   "verified"));
            seed.Memories.Add(NewEntity(AttributedTo.Ani,   "verified"));
            seed.Memories.Add(NewEntity(AttributedTo.Ani,   "verified"));
            seed.Memories.Add(NewEntity(AttributedTo.Mark,  "verified"));
            seed.Memories.Add(NewEntity(AttributedTo.Mark,  "verified"));
            seed.Memories.Add(NewEntity(AttributedTo.World, "verified"));
            seed.Memories.Add(NewEntity(AttributedTo.Unknown, "unverified-historical"));
            await seed.SaveChangesAsync();
        }

        var svc = new EfMemoryAnalyticsService(factory,
            NullLogger<EfMemoryAnalyticsService>.Instance);

        var dist = await svc.GetAttributionDistributionAsync();

        dist.TotalRows.Should().Be(7);
        dist.ByAttributedTo[AttributedTo.Ani].Should().Be(3);
        dist.ByAttributedTo[AttributedTo.Mark].Should().Be(2);
        dist.ByAttributedTo[AttributedTo.World].Should().Be(1);
        dist.ByAttributedTo[AttributedTo.Unknown].Should().Be(1);

        dist.ByTrust["verified"].Should().Be(6);
        dist.ByTrust["unverified-historical"].Should().Be(1);
    }

    [Fact]
    public async Task GetAttributionDistribution_EmptyTable_ReturnsZeroCounts()
    {
        using var factory = InMemoryFactory();
        var svc = new EfMemoryAnalyticsService(factory,
            NullLogger<EfMemoryAnalyticsService>.Instance);

        var dist = await svc.GetAttributionDistributionAsync();

        dist.TotalRows.Should().Be(0);
        dist.ByAttributedTo.Should().BeEmpty();
        dist.ByTrust.Should().BeEmpty();
    }

    private static MemoryEntity NewEntity(AttributedTo attr, string trust) => new()
    {
        Id                         = Guid.NewGuid(),
        Type                       = MemoryType.Episodic,
        Content                    = $"seed-{Guid.NewGuid():N}",
        OccurredAt                 = DateTimeOffset.UtcNow,
        CreatedAt                  = DateTimeOffset.UtcNow,
        Provenance                 = EpistemicTier.Episodic,
        AttributedTo               = attr,
        AttributionTrust           = trust,
    };

    private static SharedConnectionFactory InMemoryFactory()
    {
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
        return new SharedConnectionFactory(conn, options);
    }

    private sealed class SharedConnectionFactory : IDbContextFactory<AniDbContext>, IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
        private readonly DbContextOptions<AniDbContext>          _options;
        public SharedConnectionFactory(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            DbContextOptions<AniDbContext>          options)
        {
            _conn    = conn;
            _options = options;
        }
        public AniDbContext CreateDbContext() => new AniDbContext(_options);
        public Task<AniDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(new AniDbContext(_options));
        public void Dispose() => _conn.Dispose();
    }
}
