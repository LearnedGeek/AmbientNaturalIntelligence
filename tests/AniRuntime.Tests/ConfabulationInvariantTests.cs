using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using LearnedGeek.ML.Interfaces;
using LearnedGeek.ML.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5b spec tests for <see cref="ConfabulationInvariant"/>.
/// Pins the type-conditional applicability + confidence-threshold +
/// hard-fail-escalation contract.
/// </summary>
public class ConfabulationInvariantTests
{
    private readonly Mock<ITextClassificationService> _classifier = new(MockBehavior.Strict);

    private ConfabulationInvariant Build(double softThreshold = 0.60)
    {
        var opts = Options.Create(new AniOptions
        {
            ConfabulationClassificationThreshold = (float)softThreshold,
        });
        return new ConfabulationInvariant(
            _classifier.Object, opts,
            NullLogger<ConfabulationInvariant>.Instance);
    }

    private static CognitiveArtifact Artifact(
        string content = "test reply",
        CognitiveProducerKind producer = CognitiveProducerKind.ConversationReply,
        CognitiveOutputSink sink = CognitiveOutputSink.Dispatch) => new()
    {
        Content      = content,
        ProducerKind = producer,
        IntendedSink = sink,
    };

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Outreach,             CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary,  CognitiveOutputSink.PersistedSummary, true)]
    [InlineData(CognitiveProducerKind.Voice,                CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.InnerThought,         CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.WorldExperience,      CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Reflection,           CognitiveOutputSink.PersistedSummary, false)]
    public void AppliesTo_DispatchAndPersistedSummaryOnly_ForContactFacingProducers(
        CognitiveProducerKind producer, CognitiveOutputSink sink, bool expected)
    {
        var inv = Build();
        inv.AppliesTo(Artifact(producer: producer, sink: sink)).Should().Be(expected);
    }

    // ── Evaluate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_NotConfabulated_Passes()
    {
        _classifier.Setup(c => c.DetectConfabulationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfabulationResult(false, 0.20f, "grounded"));

        var result = await Build().EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_ConfabulatedBelowSoftThreshold_Passes()
    {
        _classifier.Setup(c => c.DetectConfabulationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfabulationResult(true, 0.45f, "weak signal"));

        var result = await Build(softThreshold: 0.60).EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeTrue("0.45 < 0.60 soft threshold — Pass.");
    }

    [Fact]
    public async Task Evaluate_ConfabulatedAtSoftThreshold_FailsAsRemediate()
    {
        _classifier.Setup(c => c.DetectConfabulationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfabulationResult(true, 0.65f, "moderate confidence confab"));

        var result = await Build(softThreshold: 0.60).EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeFalse();
        result.IsHardFail.Should().BeFalse(
            "0.65 < hard threshold (0.85) — soft Remediate, not hard Fail. Producer regenerates with hint.");
        result.RemediationHint.Should().Contain("0.65")
            .And.Contain("moderate confidence confab");
    }

    [Fact]
    public async Task Evaluate_ConfabulatedAtHardThreshold_EscalatesToHardFail()
    {
        _classifier.Setup(c => c.DetectConfabulationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfabulationResult(true, 0.92f, "high confidence confab"));

        var result = await Build().EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeFalse();
        result.IsHardFail.Should().BeTrue(
            "0.92 >= 0.85 hard threshold — IsHardFail. Gate substitutes safe acknowledgement; no regen.");
    }

    [Fact]
    public async Task Evaluate_EmptyContent_PassesWithoutCallingClassifier()
    {
        var result = await Build().EvaluateAsync(Artifact(content: ""), CancellationToken.None);
        result.Passed.Should().BeTrue();
        _classifier.Verify(c => c.DetectConfabulationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Evaluate_BuildsContextFromContactRecentAndPriorAniMessages()
    {
        string? capturedContext = null;
        _classifier.Setup(c => c.DetectConfabulationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, ctx, _) => capturedContext = ctx)
            .ReturnsAsync(new ConfabulationResult(false, 0f, null));

        var artifact = new CognitiveArtifact
        {
            Content                 = "the reply",
            ProducerKind            = CognitiveProducerKind.ConversationReply,
            IntendedSink            = CognitiveOutputSink.Dispatch,
            ContactName             = "Mark",
            ContactRecentMessages   = new[] { "what do i teach again?", "i teach at 6pm" },
            PriorAniMessages        = new[] { "earlier ani turn" },
        };

        await Build().EvaluateAsync(artifact, CancellationToken.None);

        capturedContext.Should().Contain("Mark");
        capturedContext.Should().Contain("what do i teach again?");
        capturedContext.Should().Contain("i teach at 6pm");
        capturedContext.Should().Contain("earlier ani turn");
    }
}
