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

    // 2026-05-16 Posture S: TimeSlot enum renamed to drop work-schedule
    // assumptions (MorningRoutine → EarlyMorning, Commute → MorningTransition,
    // WorkEarly → MidMorning, Lunch → Midday, WorkAfternoon → Afternoon,
    // CommuteHome → LateAfternoon, Evening/LateNight unchanged). Integer
    // ordinals are unchanged. GenerateSeed signature dropped the occupation
    // parameter. See docs/spec/ANI-Substrate-Led-Character-Plan.md §3.1 #4.
    [Theory]
    [InlineData(5, 0)]  // EarlyMorning
    [InlineData(7, 0)]  // EarlyMorning
    [InlineData(8, 1)]  // MorningTransition
    [InlineData(9, 2)]  // MidMorning
    [InlineData(11, 2)] // MidMorning
    [InlineData(12, 3)] // Midday
    [InlineData(13, 4)] // Afternoon
    [InlineData(16, 4)] // Afternoon
    [InlineData(17, 5)] // LateAfternoon
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
    public void GenerateSeed_MidMorning_ContainsTimeOfDayMarker()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 10, 30, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null);

        seed.Should().ContainEquivalentOf("mid-morning");
    }

    [Fact]
    public void GenerateSeed_Afternoon_ContainsMidAfternoon()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 14, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null);

        seed.Should().ContainEquivalentOf("mid-afternoon");
    }

    [Fact]
    public void GenerateSeed_Evening_ContainsWindingDown()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 20, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null);

        seed.Should().ContainEquivalentOf("evening");
        seed.Should().Contain("winding down");
    }

    [Fact]
    public void GenerateSeed_WithWeather_IncludesWeatherContext()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 14, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, "rainy");

        seed.Should().ContainEquivalentOf("rainy");
    }

    [Fact]
    public void GenerateSeed_StartsWithCapitalLetter()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 3, 31, 14, 0, 0, TimeSpan.FromHours(-5));

        var seed = service.GenerateSeed(now, null);

        seed[0].Should().Be(char.ToUpper(seed[0]));
    }

    [Fact]
    public void GenerateSeed_HasNoOccupationOrWorkScheduleReferences()
    {
        // Posture S contract: the seed must not assert occupation or
        // workday-schedule terminology. Substrate carries the actual
        // "what's happening" content.
        var service = CreateService();
        var hours = new[] { 6, 8, 10, 12, 14, 17, 19, 23 };
        foreach (var hour in hours)
        {
            var now = new DateTimeOffset(2026, 3, 31, hour, 0, 0, TimeSpan.FromHours(-5));
            var seed = service.GenerateSeed(now, null).ToLowerInvariant();

            seed.Should().NotContain("bookstore");
            seed.Should().NotContain("workday");
            seed.Should().NotContain("on the way to work");
            seed.Should().NotContain("wrapping up the workday");
        }
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

        var seed = service.GenerateSeed(now, null);

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
