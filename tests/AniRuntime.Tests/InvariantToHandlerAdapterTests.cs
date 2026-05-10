using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Pipeline;
using FluentAssertions;
using Moq;
using DispatchVerdict = AniRuntime.Core.Models.OutputGateVerdict;

namespace AniRuntime.Tests;

/// <summary>
/// Theme O Phase O.2 (May 10, 2026) — spec tests for
/// <see cref="InvariantToHandlerAdapter"/>. The adapter is the thin
/// shim that wraps an existing
/// <see cref="ICognitiveOutputInvariant"/> as an
/// <see cref="ICognitivePipelineHandler"/>; it MUST preserve the
/// invariant's name + applicability + result semantics so the
/// pipeline-based gate behaves identically to the legacy gate at
/// the boundary.
/// </summary>
public class InvariantToHandlerAdapterTests
{
    private static CognitiveArtifact ArtifactFor(string content = "test")
        => new()
        {
            Content      = content,
            ProducerKind = CognitiveProducerKind.ConversationReply,
            IntendedSink = CognitiveOutputSink.Dispatch,
        };

    private static CognitivePipelineContext CtxFor(CognitiveArtifact? artifact = null)
        => new()
        {
            Artifact = artifact ?? ArtifactFor(),
            Snapshot = new ContextSnapshot(),
        };

    private static Mock<ICognitiveOutputInvariant> InvariantMock(
        string name = "mock-inv",
        bool   applies = true,
        InvariantResult? result = null)
    {
        var mock = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        mock.SetupGet(i => i.Name).Returns(name);
        mock.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(applies);
        if (applies)
            mock.Setup(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result ?? InvariantResult.Pass());
        return mock;
    }

    // ── 1. Stage is always Post ────────────────────────────────────────

    [Fact]
    public void Stage_IsAlwaysPost()
    {
        var adapter = new InvariantToHandlerAdapter(InvariantMock().Object);
        adapter.Stage.Should().Be(PipelineStage.Post,
            "invariants are post-composition output checks; the adapter's stage is locked to Post");
    }

    // ── 2. Name delegates to wrapped invariant ─────────────────────────

    [Fact]
    public void Name_DelegatesToWrappedInvariant()
    {
        var adapter = new InvariantToHandlerAdapter(InvariantMock("self-echo").Object);
        adapter.Name.Should().Be("self-echo");
    }

    // ── 3. AppliesTo delegates to wrapped invariant ────────────────────

    [Fact]
    public void AppliesTo_DelegatesToWrappedInvariant()
    {
        var inv = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        inv.SetupGet(i => i.Name).Returns("any");
        inv.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(false);

        var adapter = new InvariantToHandlerAdapter(inv.Object);
        var artifact = ArtifactFor();
        adapter.AppliesTo(artifact).Should().BeFalse();
        inv.Verify(i => i.AppliesTo(artifact), Times.Once);
    }

    // ── 4. Passed result → Continue ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_PassedResult_ReturnsContinue()
    {
        var inv = InvariantMock(result: InvariantResult.Pass());
        var adapter = new InvariantToHandlerAdapter(inv.Object);

        var result = await adapter.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeFalse();
        result.Verdict.Should().BeNull();
    }

    // ── 5. Soft fail → ShortCircuit Remediate ──────────────────────────

    [Fact]
    public async Task HandleAsync_SoftFail_ReturnsShortCircuitRemediate()
    {
        var inv = InvariantMock(result: InvariantResult.Fail("soft hint", hard: false));
        var adapter = new InvariantToHandlerAdapter(inv.Object);

        var result = await adapter.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeTrue();
        result.Verdict.Should().Be(DispatchVerdict.Remediate,
            "soft-fail invariants map to Remediate so the producer regenerates");
        result.Reason.Should().Be("soft hint");
    }

    // ── 6. Hard fail → ShortCircuit Fail ───────────────────────────────

    [Fact]
    public async Task HandleAsync_HardFail_ReturnsShortCircuitFail()
    {
        var inv = InvariantMock(result: InvariantResult.Fail("hard hint", hard: true));
        var adapter = new InvariantToHandlerAdapter(inv.Object);

        var result = await adapter.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeTrue();
        result.Verdict.Should().Be(DispatchVerdict.Fail,
            "hard-fail invariants map to Fail so the producer abandons instead of regenerating");
        result.Reason.Should().Be("hard hint");
    }

    // ── 7. Null hint becomes empty string ──────────────────────────────

    [Fact]
    public async Task HandleAsync_FailWithNullHint_ReasonIsEmptyString()
    {
        // Defensive: InvariantResult.Fail factory requires a hint, but
        // construction via init syntax can produce a null hint. The adapter
        // converts null → "" so HandlerResult.Reason is never null on a
        // short-circuit.
        var inv = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        inv.SetupGet(i => i.Name).Returns("nullhint");
        inv.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);
        inv.Setup(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(new InvariantResult { Passed = false, RemediationHint = null });

        var adapter = new InvariantToHandlerAdapter(inv.Object);
        var result = await adapter.HandleAsync(CtxFor(), CancellationToken.None);

        result.ShortCircuit.Should().BeTrue();
        result.Reason.Should().Be(string.Empty);
    }

    // ── 8. Exception propagates (the pipeline catches it) ──────────────

    [Fact]
    public async Task HandleAsync_InvariantThrows_PropagatesException()
    {
        // The adapter intentionally does NOT catch invariant exceptions.
        // The pipeline's InvokeHandlerAsync already catches every handler
        // exception, logs an O_HANDLER_END warning, and short-circuits
        // with Fail. Catching here would either duplicate the log or
        // silently swallow the bug; both are worse than the single
        // canonical catch in the orchestrator.
        var inv = new Mock<ICognitiveOutputInvariant>(MockBehavior.Strict);
        inv.SetupGet(i => i.Name).Returns("throws");
        inv.Setup(i => i.AppliesTo(It.IsAny<CognitiveArtifact>())).Returns(true);
        inv.Setup(i => i.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("invariant bug"));

        var adapter = new InvariantToHandlerAdapter(inv.Object);
        var act = () => adapter.HandleAsync(CtxFor(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("invariant bug");
    }

    // ── 9. Constructor null-guards ─────────────────────────────────────

    [Fact]
    public void Constructor_NullInvariant_Throws()
    {
        var act = () => new InvariantToHandlerAdapter(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
