using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using AniRuntime.Perception;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// Outage Perception Source tests (Apr 27, 2026; backlog 15.19, motivating
/// case Apr 14-15 power outage). Verifies the outage / recovery semantics
/// against a real PerceptionSourceHealthTracker — testing the integration
/// rather than mocking the tracker, since the source's behaviour is mostly
/// in the queries it runs against the tracker.
/// </summary>
public class OutagePerceptionSourceTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 04, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AniOptions OptionsWith(
        bool enabled = true,
        int minSources = 3,
        double minMinutes = 15.0,
        double cooldownMinutes = 60.0)
        => new()
        {
            OutagePerceptionEnabled       = enabled,
            OutageMinFailingSources       = minSources,
            OutageMinFailingMinutes       = minMinutes,
            OutageReemitCooldownMinutes   = cooldownMinutes,
        };

    private static OutagePerceptionSource Build(
        IPerceptionSourceHealthTracker tracker, AniOptions options, FakeTimeProvider time)
        => new(tracker, Options.Create(options), time,
               NullLogger<OutagePerceptionSource>.Instance);

    [Fact]
    public async Task PollAsync_DoesNotEmit_WhenDisabled()
    {
        var tracker = new PerceptionSourceHealthTracker();
        // Set up an actual outage condition.
        tracker.RecordFailure("rss",     T0);
        tracker.RecordFailure("weather", T0);
        tracker.RecordFailure("twilio",  T0);
        var time = new FakeTimeProvider { Now = T0.AddMinutes(20) };

        var src = Build(tracker, OptionsWith(enabled: false), time);
        var events = await src.PollAsync(time.Now);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_DoesNotEmit_BelowMinSourcesThreshold()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss",     T0);
        tracker.RecordFailure("weather", T0);
        // Only 2 failing — threshold is 3.
        var time = new FakeTimeProvider { Now = T0.AddMinutes(20) };

        var src = Build(tracker, OptionsWith(minSources: 3), time);
        var events = await src.PollAsync(time.Now);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_DoesNotEmit_BelowMinDurationThreshold()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss",     T0);
        tracker.RecordFailure("weather", T0);
        tracker.RecordFailure("twilio",  T0);
        // Only 5 minutes elapsed — threshold is 15.
        var time = new FakeTimeProvider { Now = T0.AddMinutes(5) };

        var src = Build(tracker, OptionsWith(minMinutes: 15), time);
        var events = await src.PollAsync(time.Now);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_Emits_WhenThreeSourcesFailingForFifteenPlusMinutes()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss",     T0);
        tracker.RecordFailure("weather", T0);
        tracker.RecordFailure("twilio",  T0);
        var time = new FakeTimeProvider { Now = T0.AddMinutes(20) };

        var src = Build(tracker, OptionsWith(), time);
        var events = (await src.PollAsync(time.Now)).ToList();

        events.Should().HaveCount(1);
        events[0].SourceName.Should().Be("outage");
        events[0].Category.Should().Be(PerceptionCategory.Internal);
        events[0].Summary.Should().Contain("rss");
        events[0].Summary.Should().Contain("weather");
        events[0].Summary.Should().Contain("twilio");
        events[0].Summary.Should().Contain("isn't just a hiccup",
            "first-time outage emission uses the 'something's wrong' framing");
    }

    [Fact]
    public async Task PollAsync_RespectsCooldown_DuringSustainedOutage()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss",     T0);
        tracker.RecordFailure("weather", T0);
        tracker.RecordFailure("twilio",  T0);
        var time = new FakeTimeProvider { Now = T0.AddMinutes(20) };

        var src = Build(tracker, OptionsWith(cooldownMinutes: 60), time);

        // First poll — emits.
        (await src.PollAsync(time.Now)).Should().HaveCount(1);

        // 30 minutes later, still in outage — should NOT re-emit (within cooldown).
        time.Now = T0.AddMinutes(50);
        (await src.PollAsync(time.Now)).Should().BeEmpty();

        // 90 minutes later (past 60 min cooldown) — re-emits with sustained framing.
        time.Now = T0.AddMinutes(110);
        var reemit = (await src.PollAsync(time.Now)).ToList();
        reemit.Should().HaveCount(1);
        reemit[0].Summary.Should().Contain("quiet for a while now",
            "sustained-outage re-emission uses the 'still no signal' framing");
    }

    [Fact]
    public async Task PollAsync_EmitsRecovery_WhenFailingSourcesDropBelowThreshold()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss",     T0);
        tracker.RecordFailure("weather", T0);
        tracker.RecordFailure("twilio",  T0);
        var time = new FakeTimeProvider { Now = T0.AddMinutes(20) };
        var src = Build(tracker, OptionsWith(), time);

        // First poll fires the outage.
        (await src.PollAsync(time.Now)).Should().HaveCount(1);

        // Sources recover.
        time.Now = T0.AddMinutes(25);
        tracker.RecordSuccess("rss",     time.Now);
        tracker.RecordSuccess("weather", time.Now);
        tracker.RecordSuccess("twilio",  time.Now);

        var recovery = (await src.PollAsync(time.Now)).ToList();
        recovery.Should().HaveCount(1);
        recovery[0].Summary.Should().Contain("world came back");
        recovery[0].Summary.Should().Contain("rss");
    }

    [Fact]
    public async Task PollAsync_DoesNotEmitRecovery_IfNeverEnteredOutage()
    {
        // A momentary blip that didn't reach the outage threshold should not
        // produce a "world came back" event when it clears.
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss", T0);
        tracker.RecordSuccess("rss", T0.AddMinutes(5));
        var time = new FakeTimeProvider { Now = T0.AddMinutes(10) };
        var src = Build(tracker, OptionsWith(), time);

        var events = (await src.PollAsync(time.Now)).ToList();
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_DoesNotReEmitOutage_AfterRecovery()
    {
        // After a recovery emission, the outage state is cleared. A
        // subsequent poll with no new outage condition should NOT fire
        // either an outage or a recovery event.
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss",     T0);
        tracker.RecordFailure("weather", T0);
        tracker.RecordFailure("twilio",  T0);
        var time = new FakeTimeProvider { Now = T0.AddMinutes(20) };
        var src = Build(tracker, OptionsWith(), time);

        await src.PollAsync(time.Now); // outage

        time.Now = T0.AddMinutes(25);
        tracker.RecordSuccess("rss",     time.Now);
        tracker.RecordSuccess("weather", time.Now);
        tracker.RecordSuccess("twilio",  time.Now);
        await src.PollAsync(time.Now); // recovery

        // Steady state: nothing new.
        time.Now = T0.AddMinutes(30);
        (await src.PollAsync(time.Now)).Should().BeEmpty();
    }
}
