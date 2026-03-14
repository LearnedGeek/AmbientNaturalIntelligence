using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

public class EmotionalStateTests
{
    [Fact]
    public void ComputeFromContributions_BaselineWhenEmpty()
    {
        var state = new EmotionalState();
        state.ComputeFromContributions(Array.Empty<EmotionalContribution>());

        state.Warmth.Should().Be(state.WarmthBaseline);
        state.Energy.Should().Be(state.EnergyBaseline);
        state.Concern.Should().Be(state.ConcernBaseline);
        state.Playfulness.Should().Be(state.PlayfulnessBaseline);
    }

    [Fact]
    public void ComputeFromContributions_AppliesFreshDeltas()
    {
        var state = new EmotionalState();
        var now = DateTimeOffset.UtcNow;
        var contributions = new List<EmotionalContribution>
        {
            new() { WarmthDelta = 0.1f, EnergyDelta = -0.2f, CreatedAt = now, HalfLifeHours = 1f }
        };

        state.ComputeFromContributions(contributions, now);

        state.Warmth.Should().BeApproximately(0.7f, 0.01f);  // 0.6 + 0.1
        state.Energy.Should().BeApproximately(0.3f, 0.01f);  // 0.5 - 0.2
    }

    [Fact]
    public void ComputeFromContributions_DecaysOverTime()
    {
        var state = new EmotionalState();
        var created = DateTimeOffset.UtcNow.AddHours(-1); // 1 half-life ago
        var now = DateTimeOffset.UtcNow;
        var contributions = new List<EmotionalContribution>
        {
            new() { WarmthDelta = 0.2f, CreatedAt = created, HalfLifeHours = 1f }
        };

        state.ComputeFromContributions(contributions, now);

        // After 1 half-life, delta is halved: 0.2 * 0.5 = 0.1
        state.Warmth.Should().BeApproximately(0.7f, 0.01f); // 0.6 + 0.1
    }

    [Fact]
    public void ComputeFromContributions_MultipleSources_Sum()
    {
        var state = new EmotionalState();
        var now = DateTimeOffset.UtcNow;
        var contributions = new List<EmotionalContribution>
        {
            new() { WarmthDelta = 0.1f, CreatedAt = now, HalfLifeHours = 1f },
            new() { WarmthDelta = -0.15f, CreatedAt = now, HalfLifeHours = 3f },
            new() { EnergyDelta = 0.2f, CreatedAt = now, HalfLifeHours = 1f },
        };

        state.ComputeFromContributions(contributions, now);

        state.Warmth.Should().BeApproximately(0.55f, 0.01f); // 0.6 + 0.1 - 0.15
        state.Energy.Should().BeApproximately(0.7f, 0.01f);  // 0.5 + 0.2
    }

    [Fact]
    public void ComputeFromContributions_ClampsToValidRange()
    {
        var state = new EmotionalState();
        var now = DateTimeOffset.UtcNow;
        var contributions = new List<EmotionalContribution>
        {
            new() { WarmthDelta = 0.5f, EnergyDelta = -0.6f, CreatedAt = now, HalfLifeHours = 1f }
        };

        state.ComputeFromContributions(contributions, now);

        state.Warmth.Should().BeLessOrEqualTo(1.0f);
        state.Energy.Should().BeGreaterOrEqualTo(0.0f);
    }

    [Fact]
    public void ComputeFromContributions_FullDecay_ReturnsToBaseline()
    {
        var state = new EmotionalState();
        var created = DateTimeOffset.UtcNow.AddHours(-10); // 10 half-lives ago
        var now = DateTimeOffset.UtcNow;
        var contributions = new List<EmotionalContribution>
        {
            new() { WarmthDelta = 0.2f, CreatedAt = created, HalfLifeHours = 1f }
        };

        state.ComputeFromContributions(contributions, now);

        // After 10 half-lives, delta ~ 0.0002 — effectively zero
        state.Warmth.Should().BeApproximately(0.6f, 0.01f);
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

    // ── Feature 17: Contact-Gap Tension ────────────────────────────────

    [Fact]
    public void ContactGapTension_DefaultsToZero()
    {
        var state = new EmotionalState();
        state.ContactGapTension.Should().Be(0f);
    }

    [Fact]
    public void AccumulateContactGapTension_NoEffect_BeforeOnset()
    {
        var state = new EmotionalState();
        state.AccumulateContactGapTension(hoursSinceContact: 12.0, onsetHours: 18.0, rate: 0.004, max: 0.4);
        state.ContactGapTension.Should().Be(0f);
    }

    [Fact]
    public void AccumulateContactGapTension_Accumulates_AfterOnset()
    {
        var state = new EmotionalState();
        // 36 hours since contact, onset at 18 → 18 excess hours × 0.004 = 0.072
        state.AccumulateContactGapTension(hoursSinceContact: 36.0, onsetHours: 18.0, rate: 0.004, max: 0.4);
        state.ContactGapTension.Should().BeApproximately(0.072f, 0.001f);
    }

    [Fact]
    public void AccumulateContactGapTension_CapsAtMax()
    {
        var state = new EmotionalState();
        // 200 hours excess → would be 0.8 but capped at 0.4
        state.AccumulateContactGapTension(hoursSinceContact: 218.0, onsetHours: 18.0, rate: 0.004, max: 0.4);
        state.ContactGapTension.Should().BeApproximately(0.4f, 0.001f);
    }

    [Fact]
    public void DissipateContactGapTension_ReducesTension()
    {
        var state = new EmotionalState { ContactGapTension = 0.3f };
        // 5 min × 0.004 × 3.0 / 60 = 0.001 per call
        state.DissipateContactGapTension(elapsedMinutes: 5.0, rate: 0.004, dissipationMultiplier: 3.0);
        state.ContactGapTension.Should().BeLessThan(0.3f);
        state.ContactGapTension.Should().BeGreaterOrEqualTo(0f);
    }

    [Fact]
    public void DissipateContactGapTension_NeverGoesBelowZero()
    {
        var state = new EmotionalState { ContactGapTension = 0.001f };
        state.DissipateContactGapTension(elapsedMinutes: 60.0, rate: 0.004, dissipationMultiplier: 3.0);
        state.ContactGapTension.Should().Be(0f);
    }

    [Fact]
    public void EffectiveWarmth_SuppressedByTension()
    {
        var state = new EmotionalState { Warmth = 0.8f, ContactGapTension = 0.3f };
        // effectiveWarmth = 0.8 - 0.3*0.3 = 0.71
        state.EffectiveWarmth.Should().BeApproximately(0.71f, 0.01f);
    }

    [Fact]
    public void EffectiveWarmth_EqualsWarmth_WhenNoTension()
    {
        var state = new EmotionalState { Warmth = 0.8f, ContactGapTension = 0f };
        state.EffectiveWarmth.Should().Be(state.Warmth);
    }

    [Fact]
    public void EffectiveWarmth_NeverNegative()
    {
        var state = new EmotionalState { Warmth = 0.05f, ContactGapTension = 0.4f };
        state.EffectiveWarmth.Should().BeGreaterOrEqualTo(0f);
    }

    [Fact]
    public void Describe_MentionsTension_WhenAboveThreshold()
    {
        var state = new EmotionalState { ContactGapTension = 0.2f };
        state.Describe().Should().Contain("silence");
    }

    [Fact]
    public void Describe_OmitsTension_WhenBelowThreshold()
    {
        var state = new EmotionalState { ContactGapTension = 0.1f };
        state.Describe().Should().NotContain("silence");
    }

    [Fact]
    public void GetSelfAwarenessPrompt_IncludesTension_WhenAboveThreshold()
    {
        var state = new EmotionalState { ContactGapTension = 0.25f };
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().NotBeNull();
        prompt.Should().Contain("quiet");
    }

    [Fact]
    public void BuildMoodInstruction_IncludesTension_WhenAboveThreshold()
    {
        var state = new EmotionalState { ContactGapTension = 0.2f };
        var instruction = PromptBuilder.BuildMoodInstruction(state);
        instruction.Should().Contain("undercurrent");
    }

    // ── Feature 4: Relationship Health Model ─────────────────────────────

    [Theory]
    [InlineData(0.8, "steady",   "connected")]
    [InlineData(0.5, "steady",   "steady")]
    [InlineData(0.3, "steady",   "quiet")]
    [InlineData(0.1, "steady",   "distant")]
    [InlineData(0.5, "quiet",    "reconnecting")]
    [InlineData(0.5, "distant",  "reconnecting")]
    [InlineData(0.8, "quiet",    "connected")]    // high score overrides reconnecting
    [InlineData(0.1, "quiet",    "distant")]       // low score stays low
    public void DeterminePhase_ReturnsCorrectPhase(double score, string previous, string expected)
    {
        RelationshipHealth.DeterminePhase(score, previous).Should().Be(expected);
    }

    [Fact]
    public void RelationshipHealth_Describe_ReturnsNonEmpty_ForAllPhases()
    {
        foreach (var phase in new[] { "connected", "steady", "quiet", "reconnecting", "distant" })
        {
            var health = new RelationshipHealth { Phase = phase };
            health.Describe().Should().NotBeEmpty($"phase '{phase}' should have a description");
        }
    }

    [Fact]
    public void BuildMoodInstruction_UsesEffectiveWarmth_WhenTensionPresent()
    {
        // Warmth is high (0.85) but tension suppresses effective warmth below baseline
        var state = new EmotionalState
        {
            Warmth = 0.75f, WarmthBaseline = 0.6f,
            ContactGapTension = 0.4f,
            // EffectiveWarmth = 0.75 - 0.4*0.3 = 0.63 → warmthDiff = 0.03, below threshold
        };
        var instruction = PromptBuilder.BuildMoodInstruction(state);
        // Should NOT say "warm" because effective warmth is near baseline
        instruction.Should().NotContain("tenderness");
    }

    // ── Feature 8: Emotional Drift Detection ──────────────────────────────

    [Fact]
    public void EmotionalDrift_IdenticalVectors_HighSimilarity()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = Enumerable.Range(0, 10)
            .Select(i => new EmotionalStateSnapshot(0.6f, 0.5f, 0.2f, 0.5f, 0f, now.AddHours(-i)))
            .ToList();

        var older = snapshots.Take(5).ToList();
        var recent = snapshots.Skip(5).ToList();
        var drift = EmotionalDrift.Compute(recent, older);

        drift.Similarity.Should().BeGreaterThan(0.99f);
        drift.IsSignificant.Should().BeFalse();
        drift.Describe().Should().BeNull();
    }

    [Fact]
    public void EmotionalDrift_ShiftedWarmth_DetectsDrift()
    {
        var now = DateTimeOffset.UtcNow;
        var older = Enumerable.Range(0, 5)
            .Select(i => new EmotionalStateSnapshot(0.6f, 0.5f, 0.2f, 0.5f, 0f, now.AddHours(-10 - i)))
            .ToList();
        // Recent: warmth dropped to 0.2, concern spiked to 0.8
        var recent = Enumerable.Range(0, 5)
            .Select(i => new EmotionalStateSnapshot(0.2f, 0.5f, 0.8f, 0.5f, 0f, now.AddHours(-i)))
            .ToList();

        var drift = EmotionalDrift.Compute(recent, older);

        drift.IsSignificant.Should().BeTrue();
        drift.WarmthDrift.Should().BeLessThan(-0.1f);
        drift.ConcernDrift.Should().BeGreaterThan(0.1f);
        drift.Describe().Should().Contain("warmth").And.Contain("worry");
    }

    [Fact]
    public void EmotionalDrift_EmptyInput_ReturnsDefault()
    {
        var drift = EmotionalDrift.Compute(Array.Empty<EmotionalStateSnapshot>(),
            Array.Empty<EmotionalStateSnapshot>());
        drift.Similarity.Should().Be(1.0f);
        drift.IsSignificant.Should().BeFalse();
    }

    // ── EmotionalContribution Model Tests ─────────────────────────────────

    [Fact]
    public void DecayFactor_AtCreation_ReturnsOne()
    {
        var c = new EmotionalContribution { CreatedAt = DateTimeOffset.UtcNow, HalfLifeHours = 1f };
        c.DecayFactor(c.CreatedAt).Should().Be(1.0f);
    }

    [Fact]
    public void DecayFactor_AfterOneHalfLife_ReturnsHalf()
    {
        var created = DateTimeOffset.UtcNow;
        var c = new EmotionalContribution { CreatedAt = created, HalfLifeHours = 2f };
        c.DecayFactor(created.AddHours(2)).Should().BeApproximately(0.5f, 0.001f);
    }

    [Fact]
    public void DecayFactor_AfterTwoHalfLives_ReturnsQuarter()
    {
        var created = DateTimeOffset.UtcNow;
        var c = new EmotionalContribution { CreatedAt = created, HalfLifeHours = 3f };
        c.DecayFactor(created.AddHours(6)).Should().BeApproximately(0.25f, 0.001f);
    }

    [Fact]
    public void DecayFactor_BeforeCreation_ReturnsOne()
    {
        var created = DateTimeOffset.UtcNow;
        var c = new EmotionalContribution { CreatedAt = created, HalfLifeHours = 1f };
        c.DecayFactor(created.AddHours(-1)).Should().Be(1.0f);
    }

    [Fact]
    public void DecayFactor_ZeroHalfLife_ReturnsZero()
    {
        var created = DateTimeOffset.UtcNow;
        var c = new EmotionalContribution { CreatedAt = created, HalfLifeHours = 0f };
        c.DecayFactor(created.AddHours(1)).Should().Be(0f);
    }

    [Fact]
    public void CurrentDeltas_ScalesByDecayFactor()
    {
        var created = DateTimeOffset.UtcNow;
        var c = new EmotionalContribution
        {
            WarmthDelta = 0.2f, EnergyDelta = -0.1f,
            ConcernDelta = 0.15f, PlayfulnessDelta = 0.05f,
            CreatedAt = created, HalfLifeHours = 1f
        };

        // At creation — full strength
        var (w, e, co, p) = c.CurrentDeltas(created);
        w.Should().BeApproximately(0.2f, 0.001f);
        e.Should().BeApproximately(-0.1f, 0.001f);
        co.Should().BeApproximately(0.15f, 0.001f);
        p.Should().BeApproximately(0.05f, 0.001f);

        // After 1 half-life — half strength
        var (w2, e2, co2, p2) = c.CurrentDeltas(created.AddHours(1));
        w2.Should().BeApproximately(0.1f, 0.001f);
        e2.Should().BeApproximately(-0.05f, 0.001f);
        co2.Should().BeApproximately(0.075f, 0.001f);
        p2.Should().BeApproximately(0.025f, 0.001f);
    }

    [Fact]
    public void IsEffectivelyZero_FreshContribution_ReturnsFalse()
    {
        var c = new EmotionalContribution
        {
            WarmthDelta = 0.1f, CreatedAt = DateTimeOffset.UtcNow, HalfLifeHours = 1f
        };
        c.IsEffectivelyZero(c.CreatedAt).Should().BeFalse();
    }

    [Fact]
    public void IsEffectivelyZero_AfterManyHalfLives_ReturnsTrue()
    {
        var created = DateTimeOffset.UtcNow;
        var c = new EmotionalContribution
        {
            WarmthDelta = 0.2f, EnergyDelta = -0.15f,
            ConcernDelta = 0.1f, PlayfulnessDelta = 0.05f,
            CreatedAt = created, HalfLifeHours = 1f
        };
        // After 10 half-lives: 0.2 * 2^(-10) ≈ 0.0002 — well below epsilon
        c.IsEffectivelyZero(created.AddHours(10)).Should().BeTrue();
    }

    [Fact]
    public void IsEffectivelyZero_ZeroDeltas_ReturnsTrue()
    {
        var c = new EmotionalContribution { CreatedAt = DateTimeOffset.UtcNow, HalfLifeHours = 1f };
        c.IsEffectivelyZero(c.CreatedAt).Should().BeTrue();
    }

    [Theory]
    [InlineData(ImpactCategory.Ambient, 0.15f, 1.0f)]
    [InlineData(ImpactCategory.Conversation, 0.25f, 3.0f)]
    [InlineData(ImpactCategory.Global, 0.20f, 6.0f)]
    public void ImpactCategoryDefaults_ReturnsCorrectValues(ImpactCategory category, float maxDelta, float halfLife)
    {
        var (actualMax, actualHalfLife) = ImpactCategoryDefaults.GetDefaults(category);
        actualMax.Should().Be(maxDelta);
        actualHalfLife.Should().Be(halfLife);
    }
}
