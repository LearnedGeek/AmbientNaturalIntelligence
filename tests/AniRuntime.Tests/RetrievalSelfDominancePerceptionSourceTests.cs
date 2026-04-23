using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using AniRuntime.Perception;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// Agentic Lens Layer 1 Phase 1d: perception source tests.
///
/// Pins the emission rules:
///   - Emits empty when disabled.
///   - Emits empty when tracker has fewer than MinCycles recorded.
///   - Emits empty when own-output share is below threshold.
///   - Emits a single perception when share is above threshold.
///   - Cooldown suppresses re-emission during the cooldown window.
///   - After cooldown expires, can emit again if condition still holds.
/// </summary>
public class RetrievalSelfDominancePerceptionSourceTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset seed) { _now = seed; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static (RetrievalSelfDominancePerceptionSource src, RetrievalOriginTracker tracker, FakeTimeProvider time, AniOptions opts)
        Build(Action<AniOptions>? configure = null)
    {
        var opts = new AniOptions
        {
            RetrievalDominancePerceptionEnabled = true,
            OwnOutputDominanceThreshold         = 0.70,
            RetrievalDominanceWindowCycles      = 5,
            RetrievalDominanceMinCycles         = 2,
            RetrievalDominanceCooldownMinutes   = 30.0,
        };
        configure?.Invoke(opts);

        var tracker = new RetrievalOriginTracker(capacity: 30);
        var time    = new FakeTimeProvider(new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero));
        var src     = new RetrievalSelfDominancePerceptionSource(
            tracker,
            Options.Create(opts),
            time,
            NullLogger<RetrievalSelfDominancePerceptionSource>.Instance);
        return (src, tracker, time, opts);
    }

    private static void RecordCaregiverHeavyCycles(RetrievalOriginTracker tracker, int count)
    {
        for (int i = 0; i < count; i++)
            tracker.RecordCycle(new Dictionary<RetrievalOrigin, int> { [RetrievalOrigin.Caregiver] = 10 });
    }

    private static void RecordOwnOutputHeavyCycles(RetrievalOriginTracker tracker, int count, float share = 0.80f)
    {
        int caregiver = (int)Math.Round(10 * (1 - share));
        int ownOutput = 10 - caregiver;
        for (int i = 0; i < count; i++)
        {
            tracker.RecordCycle(new Dictionary<RetrievalOrigin, int>
            {
                [RetrievalOrigin.Caregiver] = caregiver,
                [RetrievalOrigin.OwnOutput] = ownOutput,
            });
        }
    }

    // ── Gate conditions ──────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_Disabled_ReturnsEmpty()
    {
        var (src, tracker, _, _) = Build(o => o.RetrievalDominancePerceptionEnabled = false);
        RecordOwnOutputHeavyCycles(tracker, count: 5, share: 0.90f);  // well above threshold
        var result = await src.PollAsync(DateTimeOffset.MinValue);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_InsufficientCycles_ReturnsEmpty()
    {
        var (src, tracker, _, _) = Build(o => o.RetrievalDominanceMinCycles = 5);
        RecordOwnOutputHeavyCycles(tracker, count: 3, share: 0.90f);
        var result = await src.PollAsync(DateTimeOffset.MinValue);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_ShareBelowThreshold_ReturnsEmpty()
    {
        var (src, tracker, _, _) = Build();
        RecordOwnOutputHeavyCycles(tracker, count: 5, share: 0.40f);  // below 0.70
        var result = await src.PollAsync(DateTimeOffset.MinValue);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_NoCyclesAtAll_ReturnsEmpty()
    {
        var (src, _, _, _) = Build();
        var result = await src.PollAsync(DateTimeOffset.MinValue);
        result.Should().BeEmpty();
    }

    // ── Emission ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_ShareAboveThreshold_EmitsPerception()
    {
        var (src, tracker, _, _) = Build();
        RecordOwnOutputHeavyCycles(tracker, count: 5, share: 0.80f);
        var result = (await src.PollAsync(DateTimeOffset.MinValue)).ToList();
        result.Should().HaveCount(1);
        result[0].SourceName.Should().Be("retrieval-self-dominance");
        result[0].Category.Should().Be(PerceptionCategory.Internal);
        result[0].Summary.Should().Contain("listening to my own thoughts");
    }

    [Fact]
    public async Task PollAsync_SourceName_IsStable()
    {
        var (src, _, _, _) = Build();
        src.SourceName.Should().Be("retrieval-self-dominance");
    }

    [Fact]
    public async Task PollAsync_IsEnabled_ReflectsOptionFlag()
    {
        var (src1, _, _, _) = Build(o => o.RetrievalDominancePerceptionEnabled = true);
        var (src2, _, _, _) = Build(o => o.RetrievalDominancePerceptionEnabled = false);
        src1.IsEnabled.Should().BeTrue();
        src2.IsEnabled.Should().BeFalse();
    }

    // ── Cooldown ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_WithinCooldown_DoesNotRepeatEmission()
    {
        var (src, tracker, time, _) = Build();
        RecordOwnOutputHeavyCycles(tracker, count: 5, share: 0.80f);

        // First emission fires.
        var first = await src.PollAsync(DateTimeOffset.MinValue);
        first.Should().HaveCount(1);

        // Second poll 5 minutes later — still within 30-min cooldown — emits nothing.
        time.Advance(TimeSpan.FromMinutes(5));
        var second = await src.PollAsync(DateTimeOffset.MinValue);
        second.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_AfterCooldown_CanEmitAgain()
    {
        var (src, tracker, time, _) = Build();
        RecordOwnOutputHeavyCycles(tracker, count: 5, share: 0.80f);

        var first = await src.PollAsync(DateTimeOffset.MinValue);
        first.Should().HaveCount(1);

        // Advance past the 30-min cooldown. Fresh cycles on the tracker still
        // show dominance — condition persists — and the cooldown no longer
        // suppresses emission.
        time.Advance(TimeSpan.FromMinutes(31));
        RecordOwnOutputHeavyCycles(tracker, count: 5, share: 0.80f);

        var second = await src.PollAsync(DateTimeOffset.MinValue);
        second.Should().HaveCount(1);
    }
}
