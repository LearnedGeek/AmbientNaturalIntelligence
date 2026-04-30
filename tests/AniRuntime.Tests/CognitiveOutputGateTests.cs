using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.4 spec tests for <see cref="CognitiveOutputGate"/>.
/// Pins the orchestration contract: applicability dispatch, ordered
/// evaluation, aggregated verdict, hard-fail escalation, exception
/// containment.
///
/// All tests use synthetic invariants (Moq) so the orchestration logic
/// is tested in isolation from any specific invariant's behaviour.
/// Real-invariant integration is covered by
/// <see cref="AntiParrotInvariantTests"/> and
/// <see cref="PromptTemplateLeakInvariantTests"/>.
/// </summary>
public class CognitiveOutputGateTests
{
    private static CognitiveArtifact ArtifactFor(string content = "test content")
        => new()
        {
            Content      = content,
            ProducerKind = CognitiveProducerKind.ConversationReply,
            IntendedSink = CognitiveOutputSink.Dispatch,
        };

    private static Mock<ICognitiveOutputInvariant> InvariantMock(
        string name, bool applies, InvariantResult result)
    {
        var mock = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        mock.SetupGet(i => i.Name).Returns(name);
        mock.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(applies);
        if (applies)
            mock.Setup(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
        return mock;
    }

    private static CognitiveOutputGate Build(params ICognitiveOutputInvariant[] invariants)
        => new(invariants, NullLogger<CognitiveOutputGate>.Instance);

    // ── Empty / trivial cases ──────────────────────────────────────────

    [Fact]
    public async Task Evaluate_NoInvariants_ReturnsPass()
    {
        var gate = Build();
        var result = await gate.EvaluateAsync(ArtifactFor());
        result.Verdict.Should().Be(OutputGateVerdict.Pass);
    }

    [Fact]
    public async Task Evaluate_EmptyContent_ReturnsPassWithoutCallingInvariants()
    {
        var inv = InvariantMock("never-runs", applies: false, InvariantResult.Pass());
        var gate = Build(inv.Object);

        var artifact = new CognitiveArtifact
        {
            Content      = "",
            ProducerKind = CognitiveProducerKind.ConversationReply,
            IntendedSink = CognitiveOutputSink.Dispatch,
        };
        var result = await gate.EvaluateAsync(artifact);

        result.Verdict.Should().Be(OutputGateVerdict.Pass);
        inv.Verify(i => i.AppliesTo(It.IsAny<CognitiveArtifact>()), Times.Never);
    }

    [Fact]
    public async Task Evaluate_NullArtifact_Throws()
    {
        var gate = Build();
        var act = () => gate.EvaluateAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── AppliesTo dispatch ─────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_InapplicableInvariants_AreSkipped()
    {
        var skipped = InvariantMock("skipped", applies: false, InvariantResult.Pass());
        var applied = InvariantMock("applied", applies: true, InvariantResult.Pass());

        var result = await Build(skipped.Object, applied.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Pass);
        skipped.Verify(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()), Times.Never);
        applied.Verify(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Aggregation ────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_AllPass_VerdictIsPass()
    {
        var a = InvariantMock("a", true, InvariantResult.Pass());
        var b = InvariantMock("b", true, InvariantResult.Pass());

        var result = await Build(a.Object, b.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Pass);
        result.FiredInvariants.Should().BeEmpty();
        result.RemediationHint.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_OneSoftFail_VerdictIsRemediate()
    {
        var pass = InvariantMock("pass-inv", true, InvariantResult.Pass());
        var fail = InvariantMock("fail-inv", true, InvariantResult.Fail("hint-x"));

        var result = await Build(pass.Object, fail.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Remediate);
        result.FiredInvariants.Should().Equal("fail-inv");
        result.RemediationHint.Should().Be("hint-x");
    }

    [Fact]
    public async Task Evaluate_MultipleFails_AggregatesHintsAndNames()
    {
        var failA = InvariantMock("a", true, InvariantResult.Fail("alpha-hint"));
        var failB = InvariantMock("b", true, InvariantResult.Fail("beta-hint"));

        var result = await Build(failA.Object, failB.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Remediate);
        result.FiredInvariants.Should().Equal("a", "b");
        result.RemediationHint.Should().Contain("alpha-hint").And.Contain("beta-hint");
    }

    [Fact]
    public async Task Evaluate_HardFail_EscalatesToFailVerdict()
    {
        var soft = InvariantMock("soft", true, InvariantResult.Fail("soft-hint"));
        var hard = InvariantMock("hard", true, InvariantResult.Fail("hard-hint", hard: true));

        var result = await Build(soft.Object, hard.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Fail,
            "any hard-fail invariant escalates the verdict regardless of other soft-fails");
        result.FiredInvariants.Should().Equal("soft", "hard");
    }

    // ── Exception containment ─────────────────────────────────────────

    [Fact]
    public async Task Evaluate_InvariantThrows_TreatedAsPassForThatArtifact()
    {
        var throwing = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        throwing.SetupGet(i => i.Name).Returns("throws");
        throwing.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);
        throwing.Setup(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("invariant bug"));

        var clean = InvariantMock("clean", true, InvariantResult.Pass());

        var result = await Build(throwing.Object, clean.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Pass,
            "exception in one invariant must NOT block the artifact — observability bugs in invariants stay non-fatal");
        result.FiredInvariants.Should().BeEmpty();
    }

    // ── Cancellation ──────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_CancellationRequested_ShortCircuitsRemainingInvariants()
    {
        // First invariant cancels mid-evaluation; subsequent invariants must not run.
        using var cts = new CancellationTokenSource();
        var first = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        first.SetupGet(i => i.Name).Returns("first");
        first.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);
        first.Setup(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
             .Callback(() => cts.Cancel())
             .ReturnsAsync(InvariantResult.Pass());

        var second = InvariantMock("second", true, InvariantResult.Pass());

        var result = await Build(first.Object, second.Object).EvaluateAsync(ArtifactFor(), cts.Token);

        result.Verdict.Should().Be(OutputGateVerdict.Pass);
        second.Verify(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
