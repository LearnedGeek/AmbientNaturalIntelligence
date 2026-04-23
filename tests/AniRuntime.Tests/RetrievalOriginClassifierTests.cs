using AniRuntime.Core;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Agentic Lens Layer 1 Phase 1a: classifier unit tests.
///
/// The classifier is pure: given a MemoryRecord's DecayTier, SourceName, and
/// Type, return the correct RetrievalOrigin. These tests pin the classification
/// precedence and the mapping from MemoryType to origin, including the
/// World Layer discriminator (InnerThought with SourceName="world-experience").
/// </summary>
public class RetrievalOriginClassifierTests
{
    // ── Precedence: Anchored wins regardless of type or source ─────────────

    [Fact]
    public void Classify_AnchoredDecayTier_ReturnsAnchored_EvenForInnerThought()
    {
        var record = new MemoryRecord
        {
            Type      = MemoryType.InnerThought,
            DecayTier = DecayTier.Anchored,
        };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.Anchored);
    }

    [Fact]
    public void Classify_AnchoredDecayTier_ReturnsAnchored_EvenForWorldExperience()
    {
        // Defensive: if a World Layer memory were also marked Anchored, Anchored
        // wins because it's the more specific class.
        var record = new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            SourceName = SourceNames.WorldExperience,
            DecayTier  = DecayTier.Anchored,
        };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.Anchored);
    }

    // ── World Layer discriminator: SourceName routes InnerThought to World ──

    [Fact]
    public void Classify_InnerThoughtWithWorldExperienceSource_ReturnsWorld()
    {
        var record = new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            SourceName = SourceNames.WorldExperience,
        };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.World);
    }

    [Fact]
    public void Classify_InnerThoughtWithoutSourceName_ReturnsOwnOutput()
    {
        var record = new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            SourceName = null,
        };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.OwnOutput);
    }

    [Fact]
    public void Classify_InnerThoughtWithUnrelatedSourceName_ReturnsOwnOutput()
    {
        var record = new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            SourceName = "some-other-source",
        };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.OwnOutput);
    }

    // ── MemoryType mapping for non-InnerThought types ──────────────────────

    [Fact]
    public void Classify_Perception_ReturnsExternal()
    {
        var record = new MemoryRecord { Type = MemoryType.Perception };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.External);
    }

    [Fact]
    public void Classify_Episodic_ReturnsCaregiver()
    {
        var record = new MemoryRecord { Type = MemoryType.Episodic };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.Caregiver);
    }

    [Fact]
    public void Classify_Commitment_ReturnsCaregiver()
    {
        var record = new MemoryRecord { Type = MemoryType.Commitment };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.Caregiver);
    }

    [Fact]
    public void Classify_OpenLoop_ReturnsCaregiver()
    {
        var record = new MemoryRecord { Type = MemoryType.OpenLoop };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.Caregiver);
    }

    [Fact]
    public void Classify_Semantic_ReturnsCaregiver_ByDefault()
    {
        // Most Semantic content in the ANI corpus is relationship-relevant.
        // If dashboard data reveals a non-caregiver Semantic class (e.g.
        // character-seed facts about Ani's world), this default can be
        // refined with a SourceName discriminator.
        var record = new MemoryRecord { Type = MemoryType.Semantic };
        RetrievalOriginClassifier.Classify(record).Should().Be(RetrievalOrigin.Caregiver);
    }

    // ── Helper predicates ──────────────────────────────────────────────────

    [Fact]
    public void IsCaregiverOriented_OnlyTrueForCaregiver()
    {
        RetrievalOriginClassifier.IsCaregiverOriented(RetrievalOrigin.Caregiver).Should().BeTrue();
        RetrievalOriginClassifier.IsCaregiverOriented(RetrievalOrigin.OwnOutput).Should().BeFalse();
        RetrievalOriginClassifier.IsCaregiverOriented(RetrievalOrigin.World).Should().BeFalse();
        RetrievalOriginClassifier.IsCaregiverOriented(RetrievalOrigin.External).Should().BeFalse();
        RetrievalOriginClassifier.IsCaregiverOriented(RetrievalOrigin.Anchored).Should().BeFalse();
        RetrievalOriginClassifier.IsCaregiverOriented(RetrievalOrigin.Unknown).Should().BeFalse();
    }

    [Fact]
    public void IsOwnOutput_OnlyTrueForOwnOutput()
    {
        RetrievalOriginClassifier.IsOwnOutput(RetrievalOrigin.OwnOutput).Should().BeTrue();
        RetrievalOriginClassifier.IsOwnOutput(RetrievalOrigin.Caregiver).Should().BeFalse();
        RetrievalOriginClassifier.IsOwnOutput(RetrievalOrigin.World).Should().BeFalse();
        RetrievalOriginClassifier.IsOwnOutput(RetrievalOrigin.External).Should().BeFalse();
        RetrievalOriginClassifier.IsOwnOutput(RetrievalOrigin.Anchored).Should().BeFalse();
        RetrievalOriginClassifier.IsOwnOutput(RetrievalOrigin.Unknown).Should().BeFalse();
    }

    // ── ScoredMemory OriginTier integration ────────────────────────────────

    [Fact]
    public void ScoredMemory_DefaultsToUnknown_WhenOriginTierNotSet()
    {
        var record = new MemoryRecord { Type = MemoryType.Episodic };
        var scored = new ScoredMemory(record, CompositeScore: 0.8f, CosineSimilarity: 0.7f);
        scored.OriginTier.Should().Be(RetrievalOrigin.Unknown);
    }

    [Fact]
    public void ScoredMemory_AcceptsExplicitOriginTier_ViaInitializer()
    {
        var record = new MemoryRecord { Type = MemoryType.Episodic };
        var scored = new ScoredMemory(record, 0.8f, 0.7f)
        {
            OriginTier = RetrievalOriginClassifier.Classify(record),
        };
        scored.OriginTier.Should().Be(RetrievalOrigin.Caregiver);
    }
}
