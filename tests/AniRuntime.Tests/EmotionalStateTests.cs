using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

public class EmotionalStateTests
{
    [Fact]
    public void DriftTowardBaseline_MovesValuesTowardBaseline()
    {
        var state = new EmotionalState
        {
            Warmth = 0.9f, WarmthBaseline = 0.6f,
            Energy = 0.2f, EnergyBaseline = 0.5f,
            Concern = 0.8f, ConcernBaseline = 0.2f,
            Playfulness = 0.1f, PlayfulnessBaseline = 0.5f,
            DriftRate = 0.5f, // 50% of gap per hour
        };

        state.DriftTowardBaseline(TimeSpan.FromHours(1));

        // Each should move halfway toward baseline
        state.Warmth.Should().BeApproximately(0.75f, 0.01f);      // 0.9 - (0.3 * 0.5) = 0.75
        state.Energy.Should().BeApproximately(0.35f, 0.01f);      // 0.2 + (0.3 * 0.5) = 0.35
        state.Concern.Should().BeApproximately(0.50f, 0.01f);     // 0.8 - (0.6 * 0.5) = 0.50
        state.Playfulness.Should().BeApproximately(0.30f, 0.01f); // 0.1 + (0.4 * 0.5) = 0.30
    }

    [Fact]
    public void DriftTowardBaseline_CapsFactorAtOne()
    {
        var state = new EmotionalState
        {
            Warmth = 0.9f, WarmthBaseline = 0.6f,
            DriftRate = 0.5f,
        };

        // After 10 hours, factor = min(1.0, 0.5 * 10) = 1.0 → full drift
        state.DriftTowardBaseline(TimeSpan.FromHours(10));

        state.Warmth.Should().BeApproximately(0.6f, 0.01f);
    }

    [Fact]
    public void DriftTowardBaseline_NoChangeWhenAtBaseline()
    {
        var state = new EmotionalState(); // all values at baseline by default

        state.DriftTowardBaseline(TimeSpan.FromHours(1));

        state.Warmth.Should().Be(state.WarmthBaseline);
        state.Energy.Should().Be(state.EnergyBaseline);
    }

    [Fact]
    public void ApplyShift_ClampsToValidRange()
    {
        var state = new EmotionalState { Warmth = 0.9f, Energy = 0.1f };

        state.ApplyShift(warmthDelta: 0.2f, energyDelta: -0.2f, concernDelta: 0f, playfulnessDelta: 0f);

        state.Warmth.Should().Be(1.0f);  // clamped to max
        state.Energy.Should().Be(0.0f);  // clamped to min (0.1 - 0.2 = -0.1 → 0)
    }

    [Fact]
    public void ApplyShift_AppliesAllDimensions()
    {
        var state = new EmotionalState
        {
            Warmth = 0.5f, Energy = 0.5f, Concern = 0.5f, Playfulness = 0.5f,
        };

        state.ApplyShift(0.1f, -0.1f, 0.05f, -0.05f);

        state.Warmth.Should().BeApproximately(0.6f, 0.001f);
        state.Energy.Should().BeApproximately(0.4f, 0.001f);
        state.Concern.Should().BeApproximately(0.55f, 0.001f);
        state.Playfulness.Should().BeApproximately(0.45f, 0.001f);
    }

    [Fact]
    public void Describe_ReturnsEmpty_WhenNearBaseline()
    {
        var state = new EmotionalState(); // all at baseline

        state.Describe().Should().BeEmpty();
    }

    [Fact]
    public void Describe_DescribesNotableDeviations()
    {
        var state = new EmotionalState
        {
            Warmth = 0.9f, WarmthBaseline = 0.6f,        // notably warm
            Energy = 0.5f, EnergyBaseline = 0.5f,        // at baseline — not mentioned
            Concern = 0.5f, ConcernBaseline = 0.2f,       // notably concerned
            Playfulness = 0.5f, PlayfulnessBaseline = 0.5f, // at baseline
        };

        var desc = state.Describe();
        desc.Should().Contain("warm");
        desc.Should().Contain("worried");
        desc.Should().NotContain("energy");
        desc.Should().NotContain("playful");
    }

    [Fact]
    public void Describe_DescribesBelowBaseline()
    {
        var state = new EmotionalState
        {
            Warmth = 0.3f, WarmthBaseline = 0.6f,         // below — distant
            Playfulness = 0.2f, PlayfulnessBaseline = 0.5f, // below — serious
        };

        var desc = state.Describe();
        desc.Should().Contain("distant");
        desc.Should().Contain("serious");
    }
}
