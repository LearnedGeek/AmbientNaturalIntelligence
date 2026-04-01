using AniRuntime.Core;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

public class WorldSeedServiceTests
{
    private static IOptions<WorldSeedOptions> DefaultOptions => Options.Create(new WorldSeedOptions());

    private static IOptions<WorldSeedOptions> OptionsWith(Action<WorldSeedOptions> configure)
    {
        var opts = new WorldSeedOptions();
        configure(opts);
        return Options.Create(opts);
    }

    private static WorldSeedService CreateService(
        IOptions<WorldSeedOptions>? options = null,
        Random? rng = null) =>
        new(options ?? DefaultOptions, NullLogger<WorldSeedService>.Instance, rng ?? new Random(42));

    // ── ShouldSeedThisCycle ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 4, true)]
    [InlineData(4, 4, true)]
    [InlineData(8, 4, true)]
    [InlineData(1, 4, false)]
    [InlineData(3, 4, false)]
    [InlineData(7, 4, false)]
    public void ShouldSeedThisCycle_RespectsFrequency(int cycleCount, int frequency, bool expected)
    {
        var opts = OptionsWith(o => o.WorldSeedFrequency = frequency);
        var service = CreateService(opts);

        service.ShouldSeedThisCycle(cycleCount).Should().Be(expected);
    }

    [Fact]
    public void ShouldSeedThisCycle_WhenDisabled_ReturnsFalse()
    {
        var opts = OptionsWith(o => o.Enabled = false);
        var service = CreateService(opts);

        service.ShouldSeedThisCycle(0).Should().BeFalse();
        service.ShouldSeedThisCycle(4).Should().BeFalse();
    }

    [Fact]
    public void ShouldSeedThisCycle_ZeroFrequency_ReturnsFalse()
    {
        var opts = OptionsWith(o => o.WorldSeedFrequency = 0);
        var service = CreateService(opts);

        service.ShouldSeedThisCycle(0).Should().BeFalse();
    }

    // ── GetTimeSlot ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(5, 0)]  // MorningRoutine
    [InlineData(7, 0)]  // MorningRoutine
    [InlineData(8, 1)]  // Commute
    [InlineData(9, 2)]  // WorkEarly
    [InlineData(11, 2)] // WorkEarly
    [InlineData(12, 3)] // Lunch
    [InlineData(13, 4)] // WorkAfternoon
    [InlineData(16, 4)] // WorkAfternoon
    [InlineData(17, 5)] // CommuteHome
    [InlineData(18, 6)] // Evening
    [InlineData(21, 6)] // Evening
    [InlineData(22, 7)] // LateNight
    [InlineData(0, 7)]  // LateNight
    [InlineData(3, 7)]  // LateNight
    public void GetTimeSlot_MapsHoursCorrectly(int hour, int expectedSlot)
    {
        var service = CreateService();
        ((int)service.GetTimeSlot(hour)).Should().Be(expectedSlot);
    }

    // ── GenerateSeed — basic structure ───────────────────────────────────────

    [Fact]
    public void GenerateSeed_MorningAtBookstore_ContainsExpectedParts()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 10, 30, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null, "bookstore");

        seed.Should().Contain("bookstore");
        seed.Should().ContainEquivalentOf("mid-morning");
    }

    [Fact]
    public void GenerateSeed_Afternoon_ContainsMidAfternoon()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 14, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null, "bookstore");

        seed.Should().ContainEquivalentOf("mid-afternoon");
        seed.Should().Contain("bookstore");
    }

    [Fact]
    public void GenerateSeed_Evening_ContainsWindingDown()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 20, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null, "bookstore");

        seed.Should().ContainEquivalentOf("evening");
        seed.Should().Contain("winding down");
    }

    [Fact]
    public void GenerateSeed_WithWeather_IncludesWeatherContext()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 14, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, "rainy", "bookstore");

        seed.Should().ContainEquivalentOf("rainy");
        seed.Should().Contain("bookstore");
    }

    [Fact]
    public void GenerateSeed_StartsWithCapitalLetter()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 14, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null, "bookstore");

        seed[0].Should().Be(char.ToUpper(seed[0]));
    }

    // ── Calendar awareness ───────────────────────────────────────────────────

    [Fact]
    public void GetHoliday_ChristmasDay_ReturnsChristmas()
    {
        var service = CreateService();
        service.GetHoliday(12, 25).Should().Be("Christmas Day");
    }

    [Fact]
    public void GetHoliday_RegularDay_ReturnsNull()
    {
        var service = CreateService();
        service.GetHoliday(3, 31).Should().BeNull();
    }

    [Fact]
    public void GenerateSeed_OnHoliday_IncludesHolidayName()
    {
        var service = CreateService();
        // July 4th at 2 PM
        var now = new DateTimeOffset(2026, 7, 4, 14, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null, "bookstore");

        seed.Should().Contain("Independence Day");
    }

    // ── Special events ───────────────────────────────────────────────────────

    [Fact]
    public void TryGetSpecialEvent_WithZeroProbability_ReturnsNull()
    {
        var opts = OptionsWith(o => o.SpecialEventProbability = 0f);
        var service = CreateService(opts);

        // Run many times — should always be null
        var results = Enumerable.Range(0, 100).Select(_ => service.TryGetSpecialEvent()).ToList();
        results.Should().AllSatisfy(r => r.Should().BeNull());
    }

    [Fact]
    public void TryGetSpecialEvent_WithFullProbability_AlwaysReturns()
    {
        var opts = OptionsWith(o => o.SpecialEventProbability = 1.0f);
        var service = CreateService(opts);

        var result = service.TryGetSpecialEvent();

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void TryGetSpecialEvent_ReturnsFromPool()
    {
        var opts = OptionsWith(o => o.SpecialEventProbability = 1.0f);
        var service = CreateService(opts);

        // Collect several events — they should all be non-empty strings
        var events = Enumerable.Range(0, 20).Select(_ => service.TryGetSpecialEvent()).ToList();
        events.Should().AllSatisfy(e =>
        {
            e.Should().NotBeNull();
            e!.Length.Should().BeGreaterThan(5);
        });
    }

    // ── SourceName constant ──────────────────────────────────────────────────

    [Fact]
    public void SourceNames_WorldExperience_Exists()
    {
        SourceNames.WorldExperience.Should().Be("world-experience");
    }
}
