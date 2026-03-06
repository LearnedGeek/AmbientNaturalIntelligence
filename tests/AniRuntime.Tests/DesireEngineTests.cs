using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Moq;

namespace AniRuntime.Tests;

public class DesireEngineTests : AniTestBase
{
    private DesireEngine CreateEngine() => new(MockMemory.Object, DefaultOptions);

    // ── ComputeNextWakeTime ────────────────────────────────────────────────────

    [Fact]
    public void ComputeNextWakeTime_FreshState_ReturnsWithinConfiguredBounds()
    {
        var engine = CreateEngine();
        var state  = FreshDesireState();

        var delay = engine.ComputeNextWakeTime(state);

        delay.TotalMinutes.Should().BeGreaterThanOrEqualTo(2.0);
        delay.TotalMinutes.Should().BeLessThanOrEqualTo(45.0);
    }

    [Fact]
    public void ComputeNextWakeTime_HighDesire_ReturnsShorterDelayThanLowDesire()
    {
        var engine    = CreateEngine();
        var lowDesire = FreshDesireState();  // DesireToConnect = 0.0
        var hiDesire  = HighDesireState();   // DesireToConnect = 0.9

        // Run many trials to account for jitter — high desire should average shorter
        var lowDelays = Enumerable.Range(0, 100).Select(_ => engine.ComputeNextWakeTime(lowDesire).TotalMinutes).Average();
        var hiDelays  = Enumerable.Range(0, 100).Select(_ => engine.ComputeNextWakeTime(hiDesire).TotalMinutes).Average();

        hiDelays.Should().BeLessThan(lowDelays);
    }

    [Fact]
    public void ComputeNextWakeTime_NightCircadian_ReturnsLongerDelayThanMorning()
    {
        var engine  = CreateEngine();
        var morning = FreshDesireState() with { CircadianModifier = 1.2f };
        var night   = FreshDesireState() with { CircadianModifier = 0.4f };

        var morningAvg = Enumerable.Range(0, 100).Select(_ => engine.ComputeNextWakeTime(morning).TotalMinutes).Average();
        var nightAvg   = Enumerable.Range(0, 100).Select(_ => engine.ComputeNextWakeTime(night).TotalMinutes).Average();

        nightAvg.Should().BeGreaterThan(morningAvg);
    }

    [Fact]
    public void ComputeNextWakeTime_AlwaysRespectsMinBound()
    {
        var engine   = CreateEngine();
        var maxDesire = FreshDesireState() with { DesireToConnect = 1.0f, CircadianModifier = 1.2f };

        for (var i = 0; i < 200; i++)
        {
            var delay = engine.ComputeNextWakeTime(maxDesire);
            delay.TotalMinutes.Should().BeGreaterThanOrEqualTo(2.0, "min wake bound must never be violated");
        }
    }

    [Fact]
    public void ComputeNextWakeTime_AlwaysRespectsMaxBound()
    {
        var engine   = CreateEngine();
        var minDesire = FreshDesireState() with { DesireToConnect = 0.0f, CircadianModifier = 0.4f };

        for (var i = 0; i < 200; i++)
        {
            var delay = engine.ComputeNextWakeTime(minDesire);
            delay.TotalMinutes.Should().BeLessThanOrEqualTo(45.0, "max wake bound must never be violated");
        }
    }

    [Fact]
    public void ComputeNextWakeTime_IsPure_DoesNotMutateState()
    {
        var engine        = CreateEngine();
        var state         = FreshDesireState();
        var desireBefore  = state.DesireToConnect;
        var cirBefore     = state.CircadianModifier;

        engine.ComputeNextWakeTime(state);

        state.DesireToConnect.Should().Be(desireBefore,  "ComputeNextWakeTime must have no side effects");
        state.CircadianModifier.Should().Be(cirBefore,   "ComputeNextWakeTime must have no side effects");
    }

    // ── ShouldReachOutAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ShouldReachOutAsync_WhenCooldownActive_ReturnsFalse()
    {
        var state = HighDesireState() with { CooldownActive = true };
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(state);

        var engine = CreateEngine();
        var result = await engine.ShouldReachOutAsync();

        result.Should().BeFalse("cooldown must gate outreach regardless of desire level");
    }

    [Fact]
    public async Task ShouldReachOutAsync_WithZeroDesire_ReturnsFalse()
    {
        var state = FreshDesireState() with { DesireToConnect = 0.0f };
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(state);

        var engine = CreateEngine();
        // Run many trials — should essentially never return true at desire=0
        var trueCount = 0;
        for (var i = 0; i < 100; i++)
            if (await engine.ShouldReachOutAsync()) trueCount++;

        trueCount.Should().Be(0, "desire of 0.0 can never cross threshold (min threshold is 0.55)");
    }

    [Fact]
    public async Task ShouldReachOutAsync_WithMaxDesire_ReturnsTrueFrequently()
    {
        var state = HighDesireState() with { DesireToConnect = 1.0f };
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(state);

        var engine = CreateEngine();
        var trueCount = 0;
        for (var i = 0; i < 100; i++)
            if (await engine.ShouldReachOutAsync()) trueCount++;

        trueCount.Should().BeGreaterThan(0, "desire of 1.0 should regularly cross threshold");
    }

    // ── ApplyDriftAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDriftAsync_IncreasesDesireToConnect()
    {
        var state = FreshDesireState() with
        {
            DesireToConnect = 0.2f,
            LastMarkContact = DateTimeOffset.UtcNow.AddHours(-4),
        };
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(state);

        DesireState? saved = null;
        MockMemory.Setup(m => m.SaveDesireStateAsync(It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
                  .Callback<DesireState, CancellationToken>((s, _) => saved = s)
                  .Returns(Task.CompletedTask);

        await CreateEngine().ApplyDriftAsync();

        saved.Should().NotBeNull();
        saved!.DesireToConnect.Should().BeGreaterThan(0.2f, "drift should increase desire after elapsed time");
    }

    [Fact]
    public async Task ApplyDriftAsync_NeverExceedsOne()
    {
        var state = FreshDesireState() with
        {
            DesireToConnect = 0.95f,
            LastMarkContact = DateTimeOffset.UtcNow.AddHours(-48),
        };
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(state);

        DesireState? saved = null;
        MockMemory.Setup(m => m.SaveDesireStateAsync(It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
                  .Callback<DesireState, CancellationToken>((s, _) => saved = s)
                  .Returns(Task.CompletedTask);

        await CreateEngine().ApplyDriftAsync();

        saved!.DesireToConnect.Should().BeLessThanOrEqualTo(1.0f, "desire must be capped at 1.0");
    }

    // ── ResetAfterOutreachAsync ───────────────────────────────────────────────

    [Fact]
    public async Task ResetAfterOutreachAsync_ClearsDesireAndTriggers()
    {
        var state = HighDesireState();
        state.ActiveTriggers.Add(new DesireTrigger { Type = TriggerType.TemporalDrift, Weight = 0.5f, Description = "test" });

        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(state);

        DesireState? saved = null;
        MockMemory.Setup(m => m.SaveDesireStateAsync(It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
                  .Callback<DesireState, CancellationToken>((s, _) => saved = s)
                  .Returns(Task.CompletedTask);

        await CreateEngine().ResetAfterOutreachAsync();

        saved!.DesireToConnect.Should().Be(0.0f);
        saved.CooldownActive.Should().BeFalse();
        saved.ActiveTriggers.Should().BeEmpty();
        saved.LastOutreach.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── AddTriggerAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddTriggerAsync_ElevatesDesireAndRecordsTrigger()
    {
        var state = FreshDesireState() with { DesireToConnect = 0.3f };
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(state);

        DesireState? saved = null;
        MockMemory.Setup(m => m.SaveDesireStateAsync(It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
                  .Callback<DesireState, CancellationToken>((s, _) => saved = s)
                  .Returns(Task.CompletedTask);

        await CreateEngine().AddTriggerAsync(TriggerType.SpontaneousThought, 0.8f, "thought of Mark");

        saved!.DesireToConnect.Should().BeGreaterThan(0.3f);
        saved.ActiveTriggers.Should().ContainSingle(t => t.Type == TriggerType.SpontaneousThought);
    }
}
