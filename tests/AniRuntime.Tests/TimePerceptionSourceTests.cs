using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Perception;
using FluentAssertions;
using Moq;

namespace AniRuntime.Tests;

public class TimePerceptionSourceTests
{
    // Fake TimeProvider — fixed UTC time, local = UTC for timezone independence
    private sealed class FakeTime(DateTimeOffset fixedUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => fixedUtc.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    // Theme K Phase K.2 (Apr 28, 2026): IStateStore (a narrowed slice of
    // IMemoryService per ISP) is strict. TimePerceptionSource only reads
    // GetEmotionalStateAsync — declare it once and any unexpected interaction
    // fails the test.
    private static readonly Mock<IStateStore> DefaultStateStore = CreateMockStateStore();

    private static Mock<IStateStore> CreateMockStateStore()
    {
        var mock = new Mock<IStateStore>(MockBehavior.Strict);
        mock.Setup(s => s.GetEmotionalStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmotionalState());
        return mock;
    }

    private static TimePerceptionSource At(DateTimeOffset t) =>
        new(new FakeTime(t), DefaultStateStore.Object);

    private static DateTimeOffset Since(DateTimeOffset now, int hoursAgo) =>
        now.AddHours(-hoursAgo);

    // ── Base context ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_AlwaysEmitsBaseContext()
    {
        var now    = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero); // Wednesday morning
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().Contain(e => e.Summary.Contains("Wednesday") && e.Summary.Contains("March"));
    }

    [Fact]
    public async Task PollAsync_BaseEvent_HasEnvironmentCategoryAndCorrectSource()
    {
        var now    = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero);
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().AllSatisfy(e =>
        {
            e.Category.Should().Be(PerceptionCategory.Environment);
            e.SourceName.Should().Be("time");
        });
    }

    // ── Day-of-week conditions ────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_MondayMorning_EmitsNewWeekPerception()
    {
        var now    = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero); // Monday 9am
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().Contain(e => e.Summary.Contains("new week"));
    }

    [Fact]
    public async Task PollAsync_MondayAfternoon_DoesNotEmitNewWeek()
    {
        var now    = new DateTimeOffset(2026, 3, 2, 14, 0, 0, TimeSpan.Zero); // Monday 2pm
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().NotContain(e => e.Summary.Contains("new week"));
    }

    [Fact]
    public async Task PollAsync_FridayAfternoon_EmitsWeekAlmostOverPerception()
    {
        var now    = new DateTimeOffset(2026, 3, 6, 15, 0, 0, TimeSpan.Zero); // Friday 3pm
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().Contain(e => e.Summary.Contains("almost over"));
    }

    [Fact]
    public async Task PollAsync_Saturday_EmitsWeekendPerception()
    {
        var now    = new DateTimeOffset(2026, 3, 7, 11, 0, 0, TimeSpan.Zero); // Saturday
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().Contain(e => e.Summary.Contains("weekend"));
    }

    // ── Month boundary ────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_FirstOfMonth_EmitsNewMonthPerception()
    {
        var now    = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero); // April 1st
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().Contain(e => e.Summary.Contains("new month") && e.Summary.Contains("April"));
    }

    // ── Elapsed time ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_ElapsedOver2Hours_EmitsElapsedPerception()
    {
        var now    = new DateTimeOffset(2026, 3, 4, 14, 0, 0, TimeSpan.Zero);
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 3))).ToList();

        events.Should().Contain(e => e.Summary.Contains("hours since"));
    }

    [Fact]
    public async Task PollAsync_ElapsedUnder2Hours_DoesNotEmitElapsedPerception()
    {
        var now    = new DateTimeOffset(2026, 3, 4, 14, 0, 0, TimeSpan.Zero);
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().NotContain(e => e.Summary.Contains("hours since"));
    }

    // ── Holiday proximity ─────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_ChristmasEveEve_EmitsHolidayPerception()
    {
        var now    = new DateTimeOffset(2026, 12, 20, 10, 0, 0, TimeSpan.Zero); // 4 days before Christmas Eve
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().Contain(e => e.Summary.Contains("Christmas"));
    }

    [Fact]
    public async Task PollAsync_ChristmasDay_EmitsTodayIsPerception()
    {
        var now    = new DateTimeOffset(2026, 12, 25, 10, 0, 0, TimeSpan.Zero);
        var source = At(now);

        var events = (await source.PollAsync(Since(now, 1))).ToList();

        events.Should().Contain(e => e.Summary.Contains("Today is Christmas Day"));
    }

    // ── IsEnabled / metadata ──────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_ReturnsTrue()
    {
        var source = At(DateTimeOffset.UtcNow);
        source.IsEnabled.Should().BeTrue();
    }
}
