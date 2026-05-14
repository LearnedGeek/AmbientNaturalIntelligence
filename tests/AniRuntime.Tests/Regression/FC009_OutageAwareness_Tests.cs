using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Perception;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-009 — Outage-awareness fails during outage</b>.
///
/// THE SPEC has two distinct sub-shapes that need separate scenarios:
///
///   (1) <b>The detector fires correctly when invoked with stale-thought state.</b>
///       When TemporalGapPerceptionSource.PollAsync is called and the last
///       persisted InnerThought is older than MinGapHours, the source MUST
///       emit a gap-acknowledgement perception event. This is the unit-level
///       SPEC of the detector itself.
///
///   (2) <b>The detector surfaces awareness even when the outage was caused
///       by the cycle itself failing.</b> The May 13 reboot case: AniRuntime
///       came up automatically; Ollama wasn't yet running; cycles failed for
///       5.5 hours; when Ollama recovered, the next cycle ran but did NOT
///       acknowledge the gap. The detector did emit a perception at 01:11 —
///       but the cycle that perceived it immediately failed, so the perception
///       never surfaced into substrate. Catch-22: gap-awareness depends on
///       a working cycle to surface; if the cycle is what's failing, awareness
///       can't fire.
///
/// THIS SCENARIO covers (1) as a clean SPEC test. Coverage of (2) requires a
/// FIT-class harness scaffolding (mock Ollama unreachable, run cycles, restore,
/// assert post-recovery awareness) — to be authored in the H.2 phase of the
/// Test Harness Plan. The pin documents (2) as a known structural failure
/// pattern requiring FIT.
///
/// TEST CATEGORY: SPEC.
/// </summary>
public class FC009_OutageAwareness_Tests
{
    /// <summary>
    /// FC-009a — SPEC: when the last persisted InnerThought is older than
    /// MinGapHours, the next PollAsync MUST emit a gap-acknowledgement
    /// perception. This is the unit-level SPEC of the detector.
    /// </summary>
    [Fact]
    public async Task FC009a_GapDetector_EmitsPerception_WhenLastThoughtIsStale_Spec()
    {
        var lastThought = new MemoryRecord
        {
            Id          = Guid.NewGuid(),
            Type        = MemoryType.InnerThought,
            Content     = "FC009-FIXTURE: synthetic inner thought from before the gap",
            Importance  = 0.5f,
            OccurredAt  = DateTimeOffset.UtcNow.AddHours(-5),   // 5h gap (> default 2h MinGapHours)
            CreatedAt   = DateTimeOffset.UtcNow.AddHours(-5),
        };

        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.GetByTypeAsync(MemoryType.InnerThought, 1, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { lastThought });

        var source = new TemporalGapPerceptionSource(
            search.Object,
            NullLogger<TemporalGapPerceptionSource>.Instance);

        var events = await source.PollAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        var list = events.ToList();
        list.Should().ContainSingle(
            "the last InnerThought is 5h old; MinGapHours default is 2h; the source MUST " +
            "emit exactly one gap-acknowledgement perception event");
        list[0].SourceName.Should().Be("temporal-gap");
        list[0].Summary.Should().NotBeNullOrWhiteSpace(
            "the perception must carry a narration string for downstream consumers");
        list[0].Summary.ToLowerInvariant().Should().Contain("hours",
            "the narration mentions the gap magnitude (5h falls into 4-12h band — " +
            "'more than N hours have passed' narration)");
    }

    /// <summary>
    /// FC-009b — SPEC: when the last persisted InnerThought is well within
    /// the normal cycle cadence (e.g., 10 minutes ago), the detector MUST
    /// NOT emit (control — verifies the threshold logic).
    /// </summary>
    [Fact]
    public async Task FC009b_GapDetector_DoesNotEmit_WhenGapIsWithinNormalCadence_Spec()
    {
        var recent = new MemoryRecord
        {
            Id          = Guid.NewGuid(),
            Type        = MemoryType.InnerThought,
            Content     = "FC009-FIXTURE: recent inner thought within cycle cadence",
            Importance  = 0.5f,
            OccurredAt  = DateTimeOffset.UtcNow.AddMinutes(-10),
            CreatedAt   = DateTimeOffset.UtcNow.AddMinutes(-10),
        };

        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.GetByTypeAsync(MemoryType.InnerThought, 1, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new[] { recent });

        var source = new TemporalGapPerceptionSource(
            search.Object,
            NullLogger<TemporalGapPerceptionSource>.Instance);

        var events = await source.PollAsync(DateTimeOffset.UtcNow.AddMinutes(-1), CancellationToken.None);

        events.Should().BeEmpty(
            "the last InnerThought is 10 min ago — well within normal cycle cadence. " +
            "The detector must not emit on normal cadence (otherwise every cycle would " +
            "fire a gap-perception and substrate would be dominated by it).");
    }

    /// <summary>
    /// FC-009c — FIT-PENDING: the structural catch-22 documented at the
    /// registry level. The detector fires INSIDE a cognitive cycle; if the
    /// cycle is what's failing (Ollama-down case May 13), the detector can't
    /// fire even though the gap is real. This SPEC is pending the FIT layer
    /// (H.2) which can mock the Ollama-down condition and exercise the
    /// post-recovery cycle.
    ///
    /// No assertion. Documents the FIT scope.
    /// </summary>
    [Fact]
    public void FC009c_CatchTwo_TheCycleMustRunForTheDetectorToFire_FitPending()
    {
        // No assertion. Pending H.2 FIT scaffolding. The catch-22 is:
        //   1. Service reboot at midnight
        //   2. Cycles fail because Ollama not up
        //   3. Detector lives in a cycle's PerceptionPhase
        //   4. Cycle failure prevents detector from running
        //   5. When Ollama comes up, first successful cycle runs but the
        //      detector's emitted-flag isn't aware of the prior failed cycles
        //   6. Net: gap detected, but the perception event from 01:11 (in the
        //      failed cycle) was lost; the next successful cycle at 05:58
        //      may or may not re-fire depending on _hasEmittedForCurrentGap
        //      state (per TemporalGapPerceptionSource:47-48 — `_hasEmittedForCurrentGap`)
        //
        // The proper SPEC test requires FIT to exercise this end-to-end. Filed
        // for H.2 authoring.
        true.Should().BeTrue();
    }
}
