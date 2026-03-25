using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

public class EmotionDesireModulationTests
{
    private static EmotionalState MakeState(
        float warmth = 0.6f, float energy = 0.5f, float worry = 0.2f, float playfulness = 0.5f)
        => new()
        {
            Warmth = warmth, WarmthBaseline = 0.6f,
            Energy = energy, EnergyBaseline = 0.5f,
            Worry = worry, WorryBaseline = 0.2f,
            Playfulness = playfulness, PlayfulnessBaseline = 0.5f,
        };

    [Fact]
    public void Modifier_AtBaseline_ReturnsOne()
    {
        var modifier = DesireEngine.ComputeEmotionDesireModifier(MakeState());
        modifier.Should().BeApproximately(1.0f, 0.01f);
    }

    [Fact]
    public void Modifier_HighWorry_AcceleratesDrift()
    {
        var modifier = DesireEngine.ComputeEmotionDesireModifier(MakeState(worry: 0.6f));
        modifier.Should().BeGreaterThan(1.0f);
    }

    [Fact]
    public void Modifier_HighWarmth_AcceleratesDrift()
    {
        var modifier = DesireEngine.ComputeEmotionDesireModifier(MakeState(warmth: 0.9f));
        modifier.Should().BeGreaterThan(1.0f);
    }

    [Fact]
    public void Modifier_LowEnergy_SuppressesDrift()
    {
        var modifier = DesireEngine.ComputeEmotionDesireModifier(MakeState(energy: 0.2f));
        modifier.Should().BeLessThan(1.0f);
    }

    [Fact]
    public void Modifier_CombinedHighWorryLowEnergy_PartiallyCancel()
    {
        // High worry accelerates, low energy brakes — they should partially cancel
        var modifier = DesireEngine.ComputeEmotionDesireModifier(MakeState(worry: 0.6f, energy: 0.2f));
        // Should be close to 1.0 since the effects partially cancel
        modifier.Should().BeGreaterThan(0.8f).And.BeLessThan(1.3f);
    }

    [Fact]
    public void Modifier_ClampedToRange()
    {
        // Extreme high: max worry + max warmth
        var high = DesireEngine.ComputeEmotionDesireModifier(MakeState(worry: 1.0f, warmth: 1.0f));
        high.Should().BeLessOrEqualTo(1.5f);

        // Extreme low: zero energy, no warmth/worry
        var low = DesireEngine.ComputeEmotionDesireModifier(MakeState(energy: 0.0f, warmth: 0.0f, worry: 0.0f));
        low.Should().BeGreaterOrEqualTo(0.5f);
    }

    [Fact]
    public void Modifier_WorryBelowBaseline_NoEffect()
    {
        // Worry below baseline should not affect modifier (no negative excess)
        var modifier = DesireEngine.ComputeEmotionDesireModifier(MakeState(worry: 0.1f));
        modifier.Should().BeApproximately(1.0f, 0.01f);
    }
}
