using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #94 spec tests for <see cref="SubstrateConsistencyInvariant"/>.
/// Pin the AppliesTo contract, the classifier-verdict-to-InvariantResult
/// mapping, the confidence-threshold gating, the fail-open on Unknown,
/// the empty-substrate short-circuit, and the feature-flag rollback lever.
/// </summary>
public class SubstrateConsistencyInvariantTests
{
    private readonly Mock<IContentContradictionClassifier> _classifier = new(MockBehavior.Strict);
    private readonly Mock<IMemorySearch>                    _memory     = new(MockBehavior.Strict);

    private SubstrateConsistencyInvariant Build(
        bool enabled = true, float threshold = 0.60f)
    {
        var opts = Options.Create(new AniOptions
        {
            SubstrateConsistencyInvariantEnabled = enabled,
            SubstrateContradictionThreshold      = threshold,
        });
        return new SubstrateConsistencyInvariant(
            _classifier.Object, _memory.Object, opts,
            NullLogger<SubstrateConsistencyInvariant>.Instance);
    }

    private static CognitiveArtifact Artifact(
        string content = "she was pouring coffee",
        CognitiveProducerKind producer = CognitiveProducerKind.InnerThought,
        CognitiveOutputSink sink = CognitiveOutputSink.PersistedMemory) => new()
    {
        Content      = content,
        ProducerKind = producer,
        IntendedSink = sink,
    };

    private static ScoredMemory ScoredRecord(string content) => new(
        Record: new MemoryRecord
        {
            Id      = Guid.NewGuid(),
            Content = content,
            Type    = MemoryType.Semantic,
        },
        CompositeScore:   0.85f,
        CosineSimilarity: 0.85f);

    private void SetupSubstrate(params string[] contents)
    {
        var scored = contents.Select(ScoredRecord).ToList();
        _memory
            .Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(),
                It.Is<EpistemicTier>(t => t == EpistemicTier.Facts),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(scored);
        _memory
            .Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(),
                It.Is<EpistemicTier>(t => t == EpistemicTier.Episodic),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());
    }

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.InnerThought,        true)]
    [InlineData(CognitiveProducerKind.Reflection,          true)]
    [InlineData(CognitiveProducerKind.WorldExperience,     true)]
    [InlineData(CognitiveProducerKind.ConversationReply,   false)]
    [InlineData(CognitiveProducerKind.Outreach,            false)]
    [InlineData(CognitiveProducerKind.Voice,               false)]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary, false)]
    [InlineData(CognitiveProducerKind.ReactiveShare,       false)]
    public void AppliesTo_SubstrateWritingProducersOnly(
        CognitiveProducerKind producer, bool expected)
    {
        var sink = producer == CognitiveProducerKind.Reflection
            ? CognitiveOutputSink.PersistedSummary
            : CognitiveOutputSink.PersistedMemory;
        Build().AppliesTo(Artifact(producer: producer, sink: sink))
               .Should().Be(expected);
    }

    [Fact]
    public void AppliesTo_FeatureFlagOff_NeverApplies()
    {
        var inv = Build(enabled: false);
        inv.AppliesTo(Artifact(producer: CognitiveProducerKind.InnerThought))
           .Should().BeFalse();
        inv.AppliesTo(Artifact(producer: CognitiveProducerKind.Reflection))
           .Should().BeFalse();
        inv.AppliesTo(Artifact(producer: CognitiveProducerKind.WorldExperience))
           .Should().BeFalse();
    }

    // ── Evaluate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_EmptyContent_Passes_WithoutCallingRetrievalOrClassifier()
    {
        var result = await Build().EvaluateAsync(
            Artifact(content: string.Empty), CancellationToken.None);
        result.Passed.Should().BeTrue();
        _memory.VerifyNoOtherCalls();
        _classifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Evaluate_NoSubstrateNeighbours_Passes_WithoutCallingClassifier()
    {
        _memory
            .Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(),
                It.IsAny<EpistemicTier>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var result = await Build().EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeTrue();
        _classifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Evaluate_ClassifierGrounded_Passes()
    {
        SetupSubstrate("Mark drinks tea in the morning, not coffee.");
        _classifier
            .Setup(c => c.ClassifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentContradictionVerdict(
                ContradictionOutcome.Grounded, 0.90f, null, "consistent"));

        var result = await Build().EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_ClassifierNeutral_Passes()
    {
        SetupSubstrate("Mark drinks tea in the morning.");
        _classifier
            .Setup(c => c.ClassifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentContradictionVerdict(
                ContradictionOutcome.Neutral, 0.75f, null, "orthogonal"));

        var result = await Build().EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_ClassifierUnknown_FailsOpen()
    {
        SetupSubstrate("Mark drinks tea in the morning.");
        _classifier
            .Setup(c => c.ClassifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentContradictionVerdict(
                ContradictionOutcome.Unknown, 0.0f, null, "transport error"));

        var result = await Build().EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_ContradictsBelowThreshold_Passes()
    {
        SetupSubstrate("Mark drinks tea in the morning, not coffee.");
        _classifier
            .Setup(c => c.ClassifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentContradictionVerdict(
                ContradictionOutcome.Contradicts, 0.45f,
                "Mark drinks tea in the morning, not coffee.",
                "artifact says coffee, substrate says tea"));

        var result = await Build(threshold: 0.60f).EvaluateAsync(
            Artifact(), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_ContradictsAtOrAboveThreshold_Fails_WithSubstrateQuoteInHint()
    {
        SetupSubstrate("Mark drinks tea in the morning, not coffee.");
        _classifier
            .Setup(c => c.ClassifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentContradictionVerdict(
                ContradictionOutcome.Contradicts, 0.85f,
                "Mark drinks tea in the morning, not coffee.",
                "artifact says coffee, substrate says tea"));

        var result = await Build(threshold: 0.60f).EvaluateAsync(
            Artifact(), CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("Mark drinks tea in the morning");
    }

    [Fact]
    public async Task Evaluate_SubstrateContextConcatenatesFactsAndEpisodic_AndDedupes()
    {
        var episodicHit = ScoredRecord("Episodic: Mark said 'I'm heading out for a run.'");
        var factsHitDupe = ScoredRecord("Facts: Mark exercises daily.");
        _memory
            .Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(),
                It.Is<EpistemicTier>(t => t == EpistemicTier.Facts),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[] { factsHitDupe });
        _memory
            .Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(),
                It.Is<EpistemicTier>(t => t == EpistemicTier.Episodic),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[] { episodicHit, factsHitDupe });

        string? capturedSubstrate = null;
        _classifier
            .Setup(c => c.ClassifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, substrate, _) => capturedSubstrate = substrate)
            .ReturnsAsync(new ContentContradictionVerdict(
                ContradictionOutcome.Grounded, 0.5f, null, "ok"));

        _ = await Build().EvaluateAsync(Artifact(), CancellationToken.None);

        capturedSubstrate.Should().NotBeNull();
        capturedSubstrate!.Should().Contain("Facts: Mark exercises daily.");
        capturedSubstrate.Should().Contain("Episodic: Mark said 'I'm heading out for a run.'");
        // Facts line appeared in both tiers — should surface exactly once.
        var occurrences = capturedSubstrate!.Split("Facts: Mark exercises daily.").Length - 1;
        occurrences.Should().Be(1);
    }
}
