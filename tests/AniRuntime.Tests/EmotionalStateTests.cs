using AniRuntime.Core;
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
        state.Worry.Should().Be(state.WorryBaseline);
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

        // With diminishing returns (tanh compression), deltas are attenuated
        // near boundaries. Small deltas near baseline are nearly linear.
        state.Warmth.Should().BeGreaterThan(0.6f).And.BeLessThan(0.75f);  // baseline + compressed(+0.1)
        state.Energy.Should().BeGreaterThan(0.25f).And.BeLessThan(0.5f);  // baseline + compressed(-0.2)
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

        // After 1 half-life, delta is halved: 0.2 * 0.5 = 0.1, then tanh-compressed
        state.Warmth.Should().BeGreaterThan(0.6f).And.BeLessThan(0.75f); // baseline + compressed(+0.1)
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

        // Net warmth delta = -0.05 (slightly negative), energy delta = +0.2
        state.Warmth.Should().BeLessThan(0.6f);  // below baseline (net negative delta)
        state.Energy.Should().BeGreaterThan(0.5f).And.BeLessThan(0.75f);  // above baseline + compressed(+0.2)
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
    public void ComputeFromContributions_DiminishingReturns_PreventsSaturation()
    {
        // Simulate the real-world problem: 50 contributions all pushing warmth up
        // and energy down (the 8B model's dominant bias). Without diminishing returns,
        // warmth=1.0 and energy=0.0 (pegged). With it, they approach but don't reach.
        var state = new EmotionalState();
        var now = DateTimeOffset.UtcNow;
        var contributions = Enumerable.Range(0, 50)
            .Select(i => new EmotionalContribution
            {
                WarmthDelta = 0.08f,
                EnergyDelta = -0.10f,
                CreatedAt = now.AddMinutes(-i * 5), // one every 5 minutes
                HalfLifeHours = 1f,
            })
            .ToList();

        state.ComputeFromContributions(contributions, now);

        // Key assertion: even with 50 biased contributions, dimensions don't peg
        state.Warmth.Should().BeGreaterThan(0.85f, "many positive contributions should push warmth high");
        state.Warmth.Should().BeLessThan(1.0f, "diminishing returns should prevent saturation at 1.0");
        state.Energy.Should().BeGreaterThan(0.0f, "diminishing returns should prevent saturation at 0.0");
        state.Energy.Should().BeLessThan(0.2f, "many negative contributions should push energy low");
    }

    [Fact]
    public void ApplyDiminishingReturns_SmallDelta_NearlyLinear()
    {
        // Small deltas near baseline should behave approximately linearly
        var result = EmotionalState.ApplyDiminishingReturns(0.5f, 0.05f);
        var linear = 0.5f + 0.05f;
        (result - linear).Should().BeLessThan(0.02f, "small deltas should be nearly linear");
    }

    [Fact]
    public void ApplyDiminishingReturns_LargeDelta_Compressed()
    {
        // Large deltas should be compressed vs linear. Scale=1.5 means moderate
        // deltas stay responsive while extreme accumulation saturates gradually.
        // tanh(0.5/1.5) ≈ 0.32, so 0.5 + 0.5*0.32 ≈ 0.66
        var result = EmotionalState.ApplyDiminishingReturns(0.5f, 0.5f);
        result.Should().BeLessThan(0.85f, "large positive delta should be compressed below linear (1.0)");
        result.Should().BeGreaterThan(0.6f, "large positive delta should still push above baseline");
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
            Warmth = 0.9f, WarmthBaseline = 0.6f,        // W ≥ 0.75, E ≥ 0.65 → "bright and warm"
            Energy = 0.7f, EnergyBaseline = 0.5f,
            Worry = 0.5f, WorryBaseline = 0.2f,
            Playfulness = 0.5f, PlayfulnessBaseline = 0.5f,
        };

        var desc = state.Describe();
        desc.Should().Contain("bright and warm");
    }

    [Fact]
    public void Describe_DescribesBelowBaseline()
    {
        var state = new EmotionalState
        {
            Warmth = 0.25f, WarmthBaseline = 0.6f,         // W < 0.30, E < 0.35 → "dim today"
            Energy = 0.30f, EnergyBaseline = 0.5f,
            Playfulness = 0.2f, PlayfulnessBaseline = 0.5f,
        };

        var desc = state.Describe();
        desc.Should().Contain("dim");
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
        // Recent: warmth dropped to 0.2, worry spiked to 0.8
        var recent = Enumerable.Range(0, 5)
            .Select(i => new EmotionalStateSnapshot(0.2f, 0.5f, 0.8f, 0.5f, 0f, now.AddHours(-i)))
            .ToList();

        var drift = EmotionalDrift.Compute(recent, older);

        drift.IsSignificant.Should().BeTrue();
        drift.WarmthDrift.Should().BeLessThan(-0.1f);
        drift.WorryDrift.Should().BeGreaterThan(0.1f);
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
            WorryDelta = 0.15f, PlayfulnessDelta = 0.05f,
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
            WorryDelta = 0.1f, PlayfulnessDelta = 0.05f,
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

    // May 2, 2026: half-lives halved (Ambient 1.0 → 0.5, Conversation 3.0 → 1.5,
    // Global 12.0 → 6.0) after May 1 evening saturation observation
    // (warmth=0.99 / worry=0.93 / playfulness=0.95 simultaneously). Pre-calibration
    // half-lives let model's uniform-positive delta bias accumulate to steady-state
    // deltaSum that landed in the flat region of tanh, pinning state near
    // saturation. See ImpactCategoryDefaults docstring for rationale.
    [Theory]
    [InlineData(ImpactCategory.Ambient, 0.15f, 0.5f)]
    [InlineData(ImpactCategory.Conversation, 0.25f, 1.5f)]
    [InlineData(ImpactCategory.Global, 0.35f, 6.0f)]
    public void ImpactCategoryDefaults_ReturnsCorrectValues(ImpactCategory category, float maxDelta, float halfLife)
    {
        var (actualMax, actualHalfLife) = ImpactCategoryDefaults.GetDefaults(category);
        actualMax.Should().Be(maxDelta);
        actualHalfLife.Should().Be(halfLife);
    }

    [Fact]
    public void CurrentDeltas_AppliesSeverityMultiplier()
    {
        var created = DateTimeOffset.UtcNow;
        var c = new EmotionalContribution
        {
            WarmthDelta = 0.2f, EnergyDelta = -0.1f,
            WorryDelta = 0.15f, PlayfulnessDelta = 0.05f,
            CreatedAt = created, HalfLifeHours = 1f,
            Severity = 0.5f
        };

        var (w, e, co, p) = c.CurrentDeltas(created);
        w.Should().BeApproximately(0.1f, 0.001f);   // 0.2 * 1.0 * 0.5
        e.Should().BeApproximately(-0.05f, 0.001f);  // -0.1 * 1.0 * 0.5
        co.Should().BeApproximately(0.075f, 0.001f);
        p.Should().BeApproximately(0.025f, 0.001f);
    }

    [Fact]
    public void CurrentDeltas_SeverityAndDecayCombine()
    {
        var created = DateTimeOffset.UtcNow;
        var c = new EmotionalContribution
        {
            WarmthDelta = 0.2f,
            CreatedAt = created, HalfLifeHours = 1f,
            Severity = 0.5f
        };

        // After 1 half-life: 0.2 * 0.5(decay) * 0.5(severity) = 0.05
        var (w, _, _, _) = c.CurrentDeltas(created.AddHours(1));
        w.Should().BeApproximately(0.05f, 0.001f);
    }

    [Fact]
    public void Describe_TenderAndQuiet_WhenHighWarmthLowEnergy()
    {
        var state = new EmotionalState { Warmth = 0.80f, Energy = 0.35f };
        state.Describe().Should().Contain("tender and quiet");
    }

    [Fact]
    public void Describe_SharpAndAlive_WhenModerateWarmthHighEnergy()
    {
        var state = new EmotionalState { Warmth = 0.60f, Energy = 0.70f };
        state.Describe().Should().Contain("sharp and alive");
    }

    [Fact]
    public void Describe_Unresolved_WhenLowWarmthHighWorry()
    {
        var state = new EmotionalState { Warmth = 0.40f, Worry = 0.40f };
        state.Describe().Should().Contain("unresolved");
    }

    [Fact]
    public void Describe_ClosedOff_WhenVeryLowWarmthAndWorry()
    {
        var state = new EmotionalState { Warmth = 0.20f, Worry = 0.05f, Energy = 0.50f };
        state.Describe().Should().Contain("closed off");
    }

    [Fact]
    public void Describe_PlayfulOverlay_HighPlayfulness()
    {
        var state = new EmotionalState { Playfulness = 0.80f };
        state.Describe().Should().Contain("funny");
    }

    [Fact]
    public void Describe_CuriousAndQuick_HighEnergyAndPlayfulness()
    {
        var state = new EmotionalState { Warmth = 0.80f, Energy = 0.70f, Playfulness = 0.70f };
        var desc = state.Describe();
        desc.Should().Contain("curious and quick");
    }

    [Fact]
    public void GetSelfAwarenessPrompt_ReturnsNull_WhenNearBaseline()
    {
        var state = new EmotionalState();
        state.GetSelfAwarenessPrompt().Should().BeNull();
    }

    [Fact]
    public void GetSelfAwarenessPrompt_BrightAndWarm_WhenHighWarmthEnergy()
    {
        var state = new EmotionalState { Warmth = 0.80f, Energy = 0.70f };
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().NotBeNull();
        prompt.Should().Contain("bright");
    }

    [Fact]
    public void GetSelfAwarenessPrompt_ClosedOff_WhenVeryLowWarmthWorry()
    {
        var state = new EmotionalState { Warmth = 0.20f, Worry = 0.05f, Energy = 0.50f };
        var prompt = state.GetSelfAwarenessPrompt();
        prompt.Should().NotBeNull();
        prompt.Should().Contain("closed off");
    }

    // ── Tier Promotion Tests ─────────────────────────────────────────────

    [Fact]
    public void DetermineEffectiveTier_BelowThresholds_ReturnsBaseTier()
    {
        var options = new AniOptions();
        ImpactCategoryDefaults.DetermineEffectiveTier(
            ImpactCategory.Ambient, 0.5f, options)
            .Should().Be(ImpactCategory.Ambient);
    }

    [Fact]
    public void DetermineEffectiveTier_AboveConversationThreshold_PromotesAmbient()
    {
        var options = new AniOptions();
        ImpactCategoryDefaults.DetermineEffectiveTier(
            ImpactCategory.Ambient, 0.75f, options)
            .Should().Be(ImpactCategory.Conversation);
    }

    [Fact]
    public void DetermineEffectiveTier_ConversationTier_NotPromotedAtModerateSeverity()
    {
        var options = new AniOptions();
        ImpactCategoryDefaults.DetermineEffectiveTier(
            ImpactCategory.Conversation, 0.75f, options)
            .Should().Be(ImpactCategory.Conversation);
    }

    [Fact]
    public void DetermineEffectiveTier_AboveGlobalThreshold_PromotesToGlobal()
    {
        var options = new AniOptions();
        // Global threshold is 0.98 — only true events (conversations, hurt) reach this
        ImpactCategoryDefaults.DetermineEffectiveTier(
            ImpactCategory.Ambient, 0.99f, options)
            .Should().Be(ImpactCategory.Global);
    }

    [Fact]
    public void DetermineEffectiveTier_ConversationToGlobal_AtHighSeverity()
    {
        var options = new AniOptions();
        ImpactCategoryDefaults.DetermineEffectiveTier(
            ImpactCategory.Conversation, 0.99f, options)
            .Should().Be(ImpactCategory.Global);
    }

    [Fact]
    public void DetermineEffectiveTier_RespectsCustomThresholds()
    {
        var options = new AniOptions
        {
            ConversationPromotionThreshold = 0.50f,
            GlobalPromotionThreshold = 0.60f,
        };
        ImpactCategoryDefaults.DetermineEffectiveTier(
            ImpactCategory.Ambient, 0.55f, options)
            .Should().Be(ImpactCategory.Conversation);

        ImpactCategoryDefaults.DetermineEffectiveTier(
            ImpactCategory.Ambient, 0.65f, options)
            .Should().Be(ImpactCategory.Global);
    }

    [Fact]
    public void GlobalTier_HasExtendedHalfLife()
    {
        var (maxDelta, halfLife) = ImpactCategoryDefaults.GetDefaults(ImpactCategory.Global);
        maxDelta.Should().Be(0.35f);
        // May 2, 2026: was 12.0f; halved to 6.0f as part of saturation calibration.
        halfLife.Should().Be(6.0f);
    }

    // ── May 2, 2026 — saturation calibration regression fixtures ──────────
    //
    // Pin the new contract: with the model's typical uniform-positive delta
    // bias on W/W/P and ambient cycle frequency, steady-state should land
    // ABOVE baseline (real signal) but BELOW saturation (responsive).
    // Pre-calibration produced state pegged at 0.99 / 0.93 / 0.95 simultaneously
    // and was unable to register new events. Post-calibration these tests
    // pin the responsive band.

    [Fact]
    public void May2Calibration_AmbientSteadyState_LandsBelowSaturationAndAboveBaseline()
    {
        // Simulate 2 hours of ambient cycle deposits at 10/hour with the model's
        // uniform +0.15 warmth bias. Pre-calibration: state would peg at ~0.96.
        // Post-calibration: state should land in responsive range (~0.80-0.90).
        var now = DateTimeOffset.UtcNow;
        var (_, ambientHalfLife) = ImpactCategoryDefaults.GetDefaults(ImpactCategory.Ambient);
        var contributions = new List<EmotionalContribution>();
        for (var i = 0; i < 20; i++)
        {
            // 6-minute spacing → 10 cycles/hour, over 2 hours
            var minutesAgo = i * 6;
            contributions.Add(new EmotionalContribution
            {
                WarmthDelta   = 0.15f,
                CreatedAt     = now.AddMinutes(-minutesAgo),
                HalfLifeHours = ambientHalfLife,
            });
        }

        var state = new EmotionalState();
        state.ComputeFromContributions(contributions, now);

        state.Warmth.Should().BeGreaterThan(state.WarmthBaseline,
            "real positive bias should still elevate state above baseline");
        state.Warmth.Should().BeLessThan(0.95f,
            "post-May-2 calibration: 2h of continuous +0.15 ambient warmth must NOT pin state at saturation. Pre-calibration this hit ~0.96+.");
    }

    [Fact]
    public void May2Calibration_AmbientContribution_DropsFromActivePoolFasterWithTighterEpsilon()
    {
        // Pre-May-2 epsilon=0.005 meant a 0.15-magnitude Ambient contribution
        // stayed "active" until decayed to <0.005 — about 4.9 half-lives.
        // Post-May-2 epsilon=0.02 (default) drops it at ~2.9 half-lives, when
        // it's still ~13% of original strength but not load-bearing on state.
        var now = DateTimeOffset.UtcNow;
        var contribution = new EmotionalContribution
        {
            WarmthDelta   = 0.15f,
            CreatedAt     = now,
            HalfLifeHours = 0.5f,  // post-May-2 ambient default
        };

        // At 1.5h (3 half-lives), original 0.15 → 0.0188. Below new 0.02 epsilon.
        contribution.IsEffectivelyZero(now.AddHours(1.5)).Should().BeTrue(
            "post-May-2 epsilon drops the contribution at ~3 half-lives, capping the active-pool size");

        // At 1.0h (2 half-lives), still 0.0375 — above 0.02 epsilon, still active.
        contribution.IsEffectivelyZero(now.AddHours(1.0)).Should().BeFalse(
            "still active at 2 half-lives — short of the new drop point");
    }

    [Fact]
    public void May2Calibration_NewEventStillRegistersAfterSteadyState()
    {
        // Critical responsiveness test: at saturated steady-state, a new
        // contribution should still produce a visible state shift. Pre-calibration,
        // tanh was so flat at deltaSum~2.16 that adding +0.15 barely moved state.
        // Post-calibration the responsive slope should still produce visible movement.
        var now = DateTimeOffset.UtcNow;
        var contributions = new List<EmotionalContribution>();
        for (var i = 0; i < 20; i++)
        {
            contributions.Add(new EmotionalContribution
            {
                WarmthDelta   = 0.15f,
                CreatedAt     = now.AddMinutes(-i * 6),
                HalfLifeHours = 0.5f,
            });
        }

        var stateBefore = new EmotionalState();
        stateBefore.ComputeFromContributions(contributions, now);

        contributions.Add(new EmotionalContribution
        {
            WarmthDelta   = 0.15f,
            CreatedAt     = now,
            HalfLifeHours = 0.5f,
        });

        var stateAfter = new EmotionalState();
        stateAfter.ComputeFromContributions(contributions, now);

        var delta = stateAfter.Warmth - stateBefore.Warmth;
        delta.Should().BeGreaterThan(0.005f,
            "new event must produce visible state movement — that's the responsiveness contract");
    }
}
