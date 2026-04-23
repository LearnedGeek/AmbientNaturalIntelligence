using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Agentic Lens Layer 1 Phase 1d: rolling-window tracker tests.
/// Pins ring-buffer semantics, aggregation windows, defensive copying.
/// </summary>
public class RetrievalOriginTrackerTests
{
    [Fact]
    public void RecordedCycleCount_StartsAtZero()
    {
        var tracker = new RetrievalOriginTracker(capacity: 5);
        tracker.RecordedCycleCount.Should().Be(0);
    }

    [Fact]
    public void RecordCycle_IncrementsRecordedCount()
    {
        var tracker = new RetrievalOriginTracker(capacity: 5);
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.Caregiver] = 3 });
        tracker.RecordedCycleCount.Should().Be(1);

        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.Caregiver] = 2 });
        tracker.RecordedCycleCount.Should().Be(2);
    }

    [Fact]
    public void RecordCycle_EvictsOldestBeyondCapacity()
    {
        var tracker = new RetrievalOriginTracker(capacity: 2);
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.Caregiver] = 10 });
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.World]     = 20 });
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.OwnOutput] = 30 });

        tracker.RecordedCycleCount.Should().Be(2);

        // Only the two newest should aggregate — the 10 Caregiver was evicted.
        var agg = tracker.GetRecentDistribution(10);
        agg.GetValueOrDefault(RetrievalOrigin.Caregiver).Should().Be(0);
        agg.GetValueOrDefault(RetrievalOrigin.World).Should().Be(20);
        agg.GetValueOrDefault(RetrievalOrigin.OwnOutput).Should().Be(30);
    }

    [Fact]
    public void GetRecentDistribution_AggregatesAcrossWindow()
    {
        var tracker = new RetrievalOriginTracker(capacity: 10);
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int>
        {
            [RetrievalOrigin.Caregiver] = 4,
            [RetrievalOrigin.OwnOutput] = 1,
        });
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int>
        {
            [RetrievalOrigin.Caregiver] = 3,
            [RetrievalOrigin.OwnOutput] = 2,
        });
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int>
        {
            [RetrievalOrigin.Caregiver] = 2,
            [RetrievalOrigin.World]     = 3,
        });

        var agg = tracker.GetRecentDistribution(cycleWindow: 3);
        agg[RetrievalOrigin.Caregiver].Should().Be(9);
        agg[RetrievalOrigin.OwnOutput].Should().Be(3);
        agg[RetrievalOrigin.World].Should().Be(3);
    }

    [Fact]
    public void GetRecentDistribution_RespectsNarrowerWindow()
    {
        var tracker = new RetrievalOriginTracker(capacity: 10);
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.Caregiver] = 5 });
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.World]     = 5 });
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.OwnOutput] = 5 });

        // Only the newest cycle should be aggregated.
        var agg = tracker.GetRecentDistribution(cycleWindow: 1);
        agg.GetValueOrDefault(RetrievalOrigin.Caregiver).Should().Be(0);
        agg.GetValueOrDefault(RetrievalOrigin.World).Should().Be(0);
        agg.GetValueOrDefault(RetrievalOrigin.OwnOutput).Should().Be(5);
    }

    [Fact]
    public void GetRecentDistribution_WindowLargerThanBuffer_AggregatesAvailable()
    {
        var tracker = new RetrievalOriginTracker(capacity: 10);
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.Caregiver] = 2 });
        tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.World]     = 3 });

        var agg = tracker.GetRecentDistribution(cycleWindow: 100);
        agg[RetrievalOrigin.Caregiver].Should().Be(2);
        agg[RetrievalOrigin.World].Should().Be(3);
    }

    [Fact]
    public void RecordCycle_DefensiveCopy_CallerMutationsDoNotLeakIn()
    {
        var tracker = new RetrievalOriginTracker(capacity: 5);
        var mutable = new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.Caregiver] = 5 };
        tracker.RecordCycle(mutable);

        // Mutate the caller's dictionary post-record.
        mutable[RetrievalOrigin.Caregiver] = 9999;
        mutable[RetrievalOrigin.OwnOutput] = 9999;

        var agg = tracker.GetRecentDistribution(10);
        agg[RetrievalOrigin.Caregiver].Should().Be(5);
        agg.GetValueOrDefault(RetrievalOrigin.OwnOutput).Should().Be(0);
    }

    [Fact]
    public void RecordCycle_NullDistribution_Ignored()
    {
        var tracker = new RetrievalOriginTracker(capacity: 5);
        tracker.RecordCycle(null!);  // null passes the guard; must not throw
        tracker.RecordedCycleCount.Should().Be(0);
    }

    [Fact]
    public void Constructor_ZeroOrNegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetrievalOriginTracker(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetrievalOriginTracker(capacity: -1));
    }
}
