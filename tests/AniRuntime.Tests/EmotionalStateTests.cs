using AniRuntime.Core.Models;
using AniRuntime.LLM;
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
        // Start at baseline so attenuation doesn't interfere with clamp test
        var state = new EmotionalState
        {
            Warmth = 0.6f, WarmthBaseline = 0.6f,
            Energy = 0.5f, EnergyBaseline = 0.5f,
        };

        // Large deltas from baseline should still clamp to 0.0–1.0
        state.ApplyShift(warmthDelta: 0.5f, energyDelta: -0.6f, concernDelta: 0f, playfulnessDelta: 0f);

        state.Warmth.Should().BeLessOrEqualTo(1.0f);
        state.Energy.Should().BeGreaterOrEqualTo(0.0f);
    }

    [Fact]
    public void ApplyShift_CorrectiveDeltas_FullStrength()
    {
        // Warmth below baseline → positive delta is corrective → full strength
        // Energy above baseline → negative delta is corrective → full strength
        var state = new EmotionalState
        {
            Warmth = 0.4f, WarmthBaseline = 0.6f,
            Energy = 0.7f, EnergyBaseline = 0.5f,
            Concern = 0.2f, ConcernBaseline = 0.2f,
            Playfulness = 0.5f, PlayfulnessBaseline = 0.5f,
        };

        state.ApplyShift(0.1f, -0.1f, 0f, 0f);

        state.Warmth.Should().BeApproximately(0.5f, 0.001f);   // corrective: full +0.1
        state.Energy.Should().BeApproximately(0.6f, 0.001f);   // corrective: full -0.1
    }

    [Fact]
    public void ApplyShift_AwayFromBaseline_Attenuated()
    {
        // Warmth already 0.9 (far above 0.6 baseline) → positive delta attenuated
        // range = 1.0 - 0.6 = 0.4, used = 0.3, scale = 1 - 0.3/0.4 = 0.25
        // attenuated delta = 0.1 * 0.25 = 0.025
        var state = new EmotionalState
        {
            Warmth = 0.9f, WarmthBaseline = 0.6f,
            Energy = 0.5f, EnergyBaseline = 0.5f,
            Concern = 0.2f, ConcernBaseline = 0.2f,
            Playfulness = 0.5f, PlayfulnessBaseline = 0.5f,
        };

        state.ApplyShift(0.1f, 0f, 0f, 0f);

        state.Warmth.Should().BeApproximately(0.925f, 0.001f); // 0.9 + 0.025
    }

    [Fact]
    public void ApplyShift_AtBaseline_FullStrengthInBothDirections()
    {
        var state = new EmotionalState
        {
            Warmth = 0.6f, WarmthBaseline = 0.6f,
            Energy = 0.5f, EnergyBaseline = 0.5f,
            Concern = 0.2f, ConcernBaseline = 0.2f,
            Playfulness = 0.5f, PlayfulnessBaseline = 0.5f,
        };

        state.ApplyShift(0.1f, -0.1f, 0.05f, -0.05f);

        state.Warmth.Should().BeApproximately(0.7f, 0.001f);
        state.Energy.Should().BeApproximately(0.4f, 0.001f);
        state.Concern.Should().BeApproximately(0.25f, 0.001f);
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

    // ── Mood Coloring (BuildMoodInstruction) ─────────────────────────────

    [Fact]
    public void BuildMoodInstruction_ReturnsEmpty_WhenNearBaseline()
    {
        var state = new EmotionalState(); // all at baseline

        PromptBuilder.BuildMoodInstruction(state).Should().BeEmpty();
    }

    [Fact]
    public void BuildMoodInstruction_IncludesWarmthInstruction_WhenHighWarmth()
    {
        var state = new EmotionalState
        {
            Warmth = 0.9f, WarmthBaseline = 0.6f,
        };

        var instruction = PromptBuilder.BuildMoodInstruction(state);
        instruction.Should().Contain("warm");
        instruction.Should().Contain("tenderness");
    }

    [Fact]
    public void BuildMoodInstruction_IncludesLowEnergyInstruction()
    {
        var state = new EmotionalState
        {
            Energy = 0.2f, EnergyBaseline = 0.5f,
        };

        var instruction = PromptBuilder.BuildMoodInstruction(state);
        instruction.Should().Contain("low-energy");
        instruction.Should().Contain("shorter messages");
    }

    [Fact]
    public void BuildMoodInstruction_CombinesMultipleDimensions()
    {
        var state = new EmotionalState
        {
            Warmth = 0.9f, WarmthBaseline = 0.6f,      // high warmth
            Playfulness = 0.8f, PlayfulnessBaseline = 0.5f, // high playfulness
            Energy = 0.5f, EnergyBaseline = 0.5f,       // at baseline — not mentioned
        };

        var instruction = PromptBuilder.BuildMoodInstruction(state);
        instruction.Should().Contain("warm");
        instruction.Should().Contain("playful");
        instruction.Should().NotContain("energy");
    }

    [Fact]
    public void BuildMoodInstruction_HandlesGuardedState()
    {
        var state = new EmotionalState
        {
            Warmth = 0.3f, WarmthBaseline = 0.6f,           // guarded
            Playfulness = 0.2f, PlayfulnessBaseline = 0.5f,  // serious
        };

        var instruction = PromptBuilder.BuildMoodInstruction(state);
        instruction.Should().Contain("guarded");
        instruction.Should().Contain("serious");
    }
}
