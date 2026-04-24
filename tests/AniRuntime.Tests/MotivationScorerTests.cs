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

    // ── Feature 42 Phase 2a — ScoreVector three-axis output ─────────────────
    // Ryan & Deci SDT: relatedness / autonomy / competence axes computed from
    // the same per-cycle signals as the scalar. Phase 2a is measurement-only;
    // these tests verify each axis's shape and the delegation contract with
    // the existing scalar API.

    [Fact]
    public void ScoreVector_DelegationContract_ScalarEqualsRelatedness()
    {
        // Contract: MotivationScorer.Score(...) must return the exact same
        // float as MotivationScorer.ScoreVector(..., 0f).Relatedness. The
        // scalar API is preserved verbatim as a thin delegator.
        var state = MakeState(warmth: 0.85f, playfulness: 0.7f);
        var scalar = MotivationScorer.Score(0.8f, 0.6f, state);
        var vector = MotivationScorer.ScoreVector(0.8f, 0.6f, state, worldOriginFraction: 0f);
        vector.Relatedness.Should().Be(scalar);
    }

    [Fact]
    public void ScoreVector_Relatedness_RespectsExistingBounds()
    {
        // Relatedness preserves the original Score() [0.3, 1.5] bounds.
        var min = MotivationScorer.ScoreVector(0.5f, 0f, MakeState(), 0f);
        min.Relatedness.Should().BeGreaterOrEqualTo(0.3f);
        min.Relatedness.Should().BeLessOrEqualTo(1.5f);

        var max = MotivationScorer.ScoreVector(1.0f, 1.0f,
                      MakeState(warmth: 1f, playfulness: 1f), 0f);
        max.Relatedness.Should().BeLessOrEqualTo(1.5f);
    }

    [Fact]
    public void ScoreVector_Autonomy_HighWhenNeutralValenceAndHighSeverity()
    {
        // Autonomy rises with emotional intensity when the thought is NOT
        // about the contact. Neutral valence (0.5) + high severity = peak
        // autonomy signal.
        var vec = MotivationScorer.ScoreVector(
            relationalValence: 0.5f, severity: 0.9f, MakeState(), worldOriginFraction: 0f);
        vec.Autonomy.Should().BeGreaterThan(1.0f);
    }

    [Fact]
    public void ScoreVector_Autonomy_ZeroWhenSeverityZero()
    {
        // No emotional weight → no autonomy pressure regardless of valence.
        var vec = MotivationScorer.ScoreVector(0.5f, 0f, MakeState(), 0f);
        vec.Autonomy.Should().Be(0f);
    }

    [Fact]
    public void ScoreVector_Autonomy_LowWhenCaregiverDirected()
    {
        // Extreme valence (clearly about the contact) → autonomy near zero
        // even with high severity, because the thought is caregiver-directed.
        var vec = MotivationScorer.ScoreVector(
            relationalValence: 0.95f, severity: 0.9f, MakeState(), 0f);
        vec.Autonomy.Should().BeLessThan(0.2f);
    }

    [Fact]
    public void ScoreVector_Autonomy_WithinBounds()
    {
        var min = MotivationScorer.ScoreVector(0f, 0f, MakeState(), 0f);
        min.Autonomy.Should().BeGreaterOrEqualTo(0f);

        var max = MotivationScorer.ScoreVector(0.5f, 1.0f, MakeState(), 0f);
        max.Autonomy.Should().BeLessOrEqualTo(1.5f);
    }

    [Fact]
    public void ScoreVector_Competence_ZeroWhenNoWorldSubstrate()
    {
        // No World-origin memories in retrieval pool → no competence pressure.
        var vec = MotivationScorer.ScoreVector(0.7f, 0.5f, MakeState(), worldOriginFraction: 0f);
        vec.Competence.Should().Be(0f);
    }

    [Fact]
    public void ScoreVector_Competence_ProportionalToWorldFraction()
    {
        var quarter = MotivationScorer.ScoreVector(0.5f, 0.5f, MakeState(), 0.25f);
        var half    = MotivationScorer.ScoreVector(0.5f, 0.5f, MakeState(), 0.50f);
        var full    = MotivationScorer.ScoreVector(0.5f, 0.5f, MakeState(), 1.00f);

        half.Competence.Should().BeGreaterThan(quarter.Competence);
        full.Competence.Should().BeGreaterThan(half.Competence);
        full.Competence.Should().BeLessOrEqualTo(1.5f);
    }

    [Fact]
    public void ScoreVector_Competence_ClampsOutOfRangeWorldFraction()
    {
        // Caller mistakes (fraction > 1 or negative) must not propagate as
        // out-of-range axis values. Clamped internally.
        var over  = MotivationScorer.ScoreVector(0.5f, 0.5f, MakeState(), 2.5f);
        var under = MotivationScorer.ScoreVector(0.5f, 0.5f, MakeState(), -0.3f);
        over.Competence.Should().BeLessOrEqualTo(1.5f);
        under.Competence.Should().BeGreaterOrEqualTo(0f);
    }

    [Fact]
    public void ScoreVector_AxesAreIndependent()
    {
        // Relatedness and autonomy should both respond to their own signals
        // without forcing the other to zero. Competence is independent of
        // both valence and severity.
        var caregiver = MotivationScorer.ScoreVector(0.9f, 0.8f, MakeState(warmth: 0.9f), 0.5f);
        caregiver.Relatedness.Should().BeGreaterThan(1.0f);
        caregiver.Competence.Should().BeGreaterThan(0.5f);
        // Autonomy still has a signal here because severity is nonzero even
        // though valence is extreme — it's just small.
        caregiver.Autonomy.Should().BeGreaterOrEqualTo(0f);
    }
}
