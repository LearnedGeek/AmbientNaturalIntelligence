using AniRuntime.Core.Interfaces;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Outage Perception Source support tests (Apr 27, 2026; backlog 15.19).
/// Verifies the health tracker correctly classifies "failing for ≥ N
/// minutes" and "recovered within N minutes" — the two queries the
/// OutagePerceptionSource consumes.
/// </summary>
public class PerceptionSourceHealthTrackerTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 04, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsTracked_FalseForUnknownSource()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.IsTracked("rss").Should().BeFalse();
    }

    [Fact]
    public void IsTracked_TrueAfterFirstSuccess()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordSuccess("rss", T0);
        tracker.IsTracked("rss").Should().BeTrue();
    }

    [Fact]
    public void GetSourcesFailingFor_ReturnsEmpty_WhenNothingFailing()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordSuccess("rss", T0);
        tracker.RecordSuccess("weather", T0);

        tracker.GetSourcesFailingFor(TimeSpan.FromMinutes(15), T0.AddMinutes(20))
               .Should().BeEmpty();
    }

    [Fact]
    public void GetSourcesFailingFor_ReportsSourceFailingLongerThanMinimum()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss", T0);

        // 20 minutes later, still failing — should appear in failing-for-15 list.
        tracker.GetSourcesFailingFor(TimeSpan.FromMinutes(15), T0.AddMinutes(20))
               .Should().Contain("rss");
    }

    [Fact]
    public void GetSourcesFailingFor_DoesNotReport_BelowMinimumDuration()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss", T0);

        tracker.GetSourcesFailingFor(TimeSpan.FromMinutes(15), T0.AddMinutes(5))
               .Should().BeEmpty();
    }

    [Fact]
    public void RecordSuccess_ClearsFailureStretch()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss", T0);
        tracker.RecordFailure("rss", T0.AddMinutes(10));
        tracker.RecordSuccess("rss", T0.AddMinutes(15));

        // Far enough out that a still-failing source would be reported.
        tracker.GetSourcesFailingFor(TimeSpan.FromMinutes(1), T0.AddMinutes(30))
               .Should().NotContain("rss",
                   "a success between failure stretches must reset the failing-since clock.");
    }

    [Fact]
    public void NewFailureAfterSuccess_StartsFreshStretch()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss", T0);
        tracker.RecordSuccess("rss", T0.AddMinutes(5));
        tracker.RecordFailure("rss", T0.AddMinutes(10));

        // 12 minutes later: only 2 minutes into the second failure stretch.
        tracker.GetSourcesFailingFor(TimeSpan.FromMinutes(15), T0.AddMinutes(12))
               .Should().BeEmpty();

        // 30 minutes later: 20 minutes into the second failure stretch.
        tracker.GetSourcesFailingFor(TimeSpan.FromMinutes(15), T0.AddMinutes(30))
               .Should().Contain("rss");
    }

    [Fact]
    public void GetSourcesRecoveredWithin_ReportsRecentSuccessAfterFailure()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss", T0);
        tracker.RecordFailure("rss", T0.AddMinutes(20));
        tracker.RecordSuccess("rss", T0.AddMinutes(25));

        // Within the recovery window of 15 min.
        tracker.GetSourcesRecoveredWithin(TimeSpan.FromMinutes(15), T0.AddMinutes(30))
               .Should().Contain("rss");
    }

    [Fact]
    public void GetSourcesRecoveredWithin_DoesNotReport_StaleRecovery()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordFailure("rss", T0);
        tracker.RecordSuccess("rss", T0.AddMinutes(5));

        // 60 minutes later — recovery happened long ago, outside the window.
        tracker.GetSourcesRecoveredWithin(TimeSpan.FromMinutes(15), T0.AddMinutes(60))
               .Should().BeEmpty();
    }

    [Fact]
    public void GetSourcesRecoveredWithin_DoesNotReport_NeverFailedSource()
    {
        var tracker = new PerceptionSourceHealthTracker();
        tracker.RecordSuccess("rss", T0);
        tracker.RecordSuccess("rss", T0.AddMinutes(5));

        // Source has only ever succeeded — that's not a "recovery" event.
        tracker.GetSourcesRecoveredWithin(TimeSpan.FromMinutes(15), T0.AddMinutes(10))
               .Should().BeEmpty();
    }
}
