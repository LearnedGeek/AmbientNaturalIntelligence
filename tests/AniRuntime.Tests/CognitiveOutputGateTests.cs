using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Loops.Pipeline;
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

    /// <summary>
    /// Theme O Phase O.2 (May 10, 2026) — gate constructor changed from
    /// IEnumerable&lt;ICognitiveOutputInvariant&gt; to a CognitivePipeline.
    /// This helper preserves the existing "build a gate from invariants"
    /// shape that all gate tests use by wrapping each invariant in an
    /// <see cref="InvariantToHandlerAdapter"/> and feeding the resulting
    /// handlers into a <see cref="CognitivePipeline"/>. The pipeline runs
    /// Post-only via the gate's pass-through; tests still assert the same
    /// verdict + fired-name contract.
    /// </summary>
    private static CognitiveOutputGate Build(params ICognitiveOutputInvariant[] invariants)
    {
        var handlers = invariants.Select(i => (ICognitivePipelineHandler)new InvariantToHandlerAdapter(i)).ToList();
        var pipeline = new CognitivePipeline(handlers, NullLogger<CognitivePipeline>.Instance);
        return new CognitiveOutputGate(pipeline, NullLogger<CognitiveOutputGate>.Instance);
    }

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
    public async Task Evaluate_MultipleFails_FirstFailureShortCircuits()
    {
        // Theme O Phase O.2 (May 10, 2026) behaviour shift: the pipeline
        // short-circuits on the FIRST failing handler rather than aggregating
        // every fired invariant. Documented in the Theme O plan §6 — each
        // failure is surfaced individually with telemetry pinpointing the
        // exact handler; producers re-evaluate after remediation regen so a
        // second invariant that would have fired re-fires on the regen if
        // it still applies. Pre-O.2 this test asserted aggregation.
        var failA = InvariantMock("a", true, InvariantResult.Fail("alpha-hint"));
        var failB = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        failB.SetupGet(i => i.Name).Returns("b");
        failB.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);

        var result = await Build(failA.Object, failB.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Remediate);
        result.FiredInvariants.Should().Equal("a");
        result.RemediationHint.Should().Be("alpha-hint");
        failB.Verify(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()), Times.Never,
            "post-O.2 the pipeline short-circuits on the first failing handler");
    }

    [Fact]
    public async Task Evaluate_HardFail_EscalatesToFailVerdict()
    {
        // Theme O Phase O.2 (May 10, 2026): hard-fail still escalates the
        // verdict to Fail; in the new short-circuit model the hard-fail
        // invariant simply has to be the FIRST failing invariant (or no
        // soft-fail invariant precedes it). Ordering is deterministic so
        // tests can rely on it.
        var hard = InvariantMock("hard", true, InvariantResult.Fail("hard-hint", hard: true));

        var result = await Build(hard.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Fail,
            "hard-fail invariant escalates the verdict from Remediate to Fail");
        result.FiredInvariants.Should().Equal("hard");
        result.RemediationHint.Should().Be("hard-hint");
    }

    // ── Exception containment ─────────────────────────────────────────

    [Fact]
    public async Task Evaluate_InvariantThrows_ShortCircuitsFail()
    {
        // Theme O Phase O.2 (May 10, 2026) behaviour shift: the pipeline's
        // orchestrator catches handler exceptions and short-circuits with
        // verdict=Fail. The pre-O.2 gate logged + treated as Pass. The new
        // behaviour is intentional: an invariant that throws is producing
        // an unknown result; treating that as "Pass" was masking observability
        // bugs. The Fail short-circuit surfaces the failure in
        // O_HANDLER_END warning logs and prevents dispatch.
        var throwing = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        throwing.SetupGet(i => i.Name).Returns("throws");
        throwing.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);
        throwing.Setup(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("invariant bug"));

        var clean = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        clean.SetupGet(i => i.Name).Returns("clean");
        clean.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);

        var result = await Build(throwing.Object, clean.Object).EvaluateAsync(ArtifactFor());

        result.Verdict.Should().Be(OutputGateVerdict.Fail);
        result.FiredInvariants.Should().Equal("throws");
        clean.Verify(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Cancellation ──────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_CancellationRequested_ShortCircuitsRemainingInvariants()
    {
        // First invariant cancels mid-evaluation; subsequent invariants must not run.
        // Post-O.2 the pipeline's orchestrator catches the resulting
        // OperationCanceledException and short-circuits with Fail; the
        // legacy gate treated it as Pass. Both behaviours stop downstream
        // invariants — the test verifies that contract.
        using var cts = new CancellationTokenSource();
        var first = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        first.SetupGet(i => i.Name).Returns("first");
        first.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);
        first.Setup(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
             .Callback<CognitiveArtifact, CancellationToken>((_, _) => cts.Cancel())
             .ThrowsAsync(new OperationCanceledException());

        var second = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        second.SetupGet(i => i.Name).Returns("second");
        second.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);

        var result = await Build(first.Object, second.Object).EvaluateAsync(ArtifactFor(), cts.Token);

        // Pipeline short-circuits on cancellation with Fail verdict.
        result.Verdict.Should().Be(OutputGateVerdict.Fail);
        second.Verify(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
