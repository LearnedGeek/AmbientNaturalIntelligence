using AniRuntime.Core;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

public class MotivationScorerTests
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
    public void Score_HighValence_HighSeverity_ReturnsHigh()
    {
        // High relational valence (close to 1.0) + high severity = very motivated
        var score = MotivationScorer.Score(0.9f, 0.8f, MakeState(warmth: 0.9f, playfulness: 0.8f));
        score.Should().BeGreaterThan(1.2f);
    }

    [Fact]
    public void Score_NeutralValence_LowSeverity_ReturnsLow()
    {
        // Neutral valence (0.5) + low severity = routine thought
        var score = MotivationScorer.Score(0.5f, 0.1f, MakeState());
        score.Should().BeLessThan(0.6f);
    }

    [Fact]
    public void Score_AlwaysWithinBounds()
    {
        // Min case
        var min = MotivationScorer.Score(0.5f, 0f, MakeState());
        min.Should().BeGreaterOrEqualTo(0.3f);

        // Max case
        var max = MotivationScorer.Score(1.0f, 1.0f, MakeState(warmth: 1f, playfulness: 1f));
        max.Should().BeLessOrEqualTo(1.5f);
    }

    [Fact]
    public void Score_HighValence_IncreasesMotivation()
    {
        var baseline = MakeState();
        var low = MotivationScorer.Score(0.5f, 0.5f, baseline);
        var high = MotivationScorer.Score(0.95f, 0.5f, baseline);
        high.Should().BeGreaterThan(low);
    }

    [Fact]
    public void Score_HighSeverity_IncreasesMotivation()
    {
        var baseline = MakeState();
        var low = MotivationScorer.Score(0.7f, 0.1f, baseline);
        var high = MotivationScorer.Score(0.7f, 0.9f, baseline);
        high.Should().BeGreaterThan(low);
    }

    [Fact]
    public void Score_WarmthAboveBaseline_IncreasesImpact()
    {
        var cold = MakeState(warmth: 0.6f); // at baseline
        var warm = MakeState(warmth: 0.9f); // above baseline
        var scoreCold = MotivationScorer.Score(0.7f, 0.5f, cold);
        var scoreWarm = MotivationScorer.Score(0.7f, 0.5f, warm);
        scoreWarm.Should().BeGreaterThan(scoreCold);
    }
}
