using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #96 (2026-07-15) — spec tests for <see cref="ToolCallInvocation"/>.
/// Pins the classify → dispatch contract:
///
/// <list type="number">
///   <item>Empty action set → null, no classifier call. Zero-cost when
///     nothing is registered.</item>
///   <item>Classifier returns no-call → null, no action invocation.</item>
///   <item>Classifier returns call with known tool → action's InvokeAsync
///     runs with the classifier's arguments, and its string result is
///     returned unchanged.</item>
///   <item>Classifier returns a name not present in the registered action
///     set → null (belt-and-suspenders; parser already coerces, but the
///     helper defends anyway).</item>
///   <item>Classifier throws → null (fail open into untooled path).</item>
///   <item>Action's InvokeAsync throws → returns an attributable error
///     string, not null and not a re-thrown exception. Per Issue #96:
///     "Tool errors surface as attributable errors, not silent fallbacks."</item>
/// </list>
/// </summary>
public class ToolCallInvocationTests
{
    private static readonly ToolDescriptor RecallDescriptor = new(
        Name:            "recall_memory",
        Description:     "Search Ani's memory for anything she may know.",
        ParameterSchema: new Dictionary<string, string> { ["query"] = "string" });

    private static ToolCallInvocation Build(
        Mock<IToolCallClassifier>          classifier,
        params IToolCallableAction[]       actions)
        => new(classifier.Object, actions, NullLogger<ToolCallInvocation>.Instance);

    private static Mock<IToolCallableAction> MockAction(ToolDescriptor descriptor)
    {
        var m = new Mock<IToolCallableAction>(MockBehavior.Strict);
        m.SetupGet(a => a.Descriptor).Returns(descriptor);
        return m;
    }

    // ── Empty registration ─────────────────────────────────────────────────

    [Fact]
    public async Task TryInvokeAsync_NoActionsRegistered_ReturnsNullWithoutClassifierCall()
    {
        // Strict classifier — will fail if any method is called.
        var classifier = new Mock<IToolCallClassifier>(MockBehavior.Strict);
        var helper = Build(classifier);

        var result = await helper.TryInvokeAsync("hello", "", CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Classifier verdicts ────────────────────────────────────────────────

    [Fact]
    public async Task TryInvokeAsync_ClassifierReturnsNoCall_ReturnsNullWithoutInvoke()
    {
        var classifier = new Mock<IToolCallClassifier>(MockBehavior.Strict);
        classifier.Setup(c => c.ClassifyAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ToolDescriptor>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolCallVerdict(false, null, null, 0.9f, "greeting"));

        var action = MockAction(RecallDescriptor);
        // Note: no Setup on InvokeAsync — strict mock guarantees it's not called.
        var helper = Build(classifier, action.Object);

        var result = await helper.TryInvokeAsync("hey babe", "", CancellationToken.None);

        result.Should().BeNull();
        action.Verify(
            a => a.InvokeAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryInvokeAsync_ClassifierPicksKnownTool_InvokesActionAndReturnsResult()
    {
        var classifier = new Mock<IToolCallClassifier>(MockBehavior.Strict);
        var args = new Dictionary<string, string> { ["query"] = "Kevin" };
        classifier.Setup(c => c.ClassifyAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ToolDescriptor>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolCallVerdict(true, "recall_memory", args, 0.85f, "named entity lookup"));

        var action = MockAction(RecallDescriptor);
        action.Setup(a => a.InvokeAsync(args, It.IsAny<CancellationToken>()))
            .ReturnsAsync("recall_memory('Kevin'): 2 result(s)\n1. [Facts] Kevin is a gym friend");

        var helper = Build(classifier, action.Object);

        var result = await helper.TryInvokeAsync("remind me who Kevin is", "", CancellationToken.None);

        result.Should().StartWith("recall_memory('Kevin')");
        result.Should().Contain("Kevin is a gym friend");
    }

    [Fact]
    public async Task TryInvokeAsync_ClassifierPicksUnknownTool_ReturnsNull()
    {
        // Belt-and-suspenders. Parser already coerces unknown names — but if
        // some future classifier bypasses ParseVerdict, this helper still
        // defends the pipeline from a bogus dispatch.
        var classifier = new Mock<IToolCallClassifier>(MockBehavior.Strict);
        classifier.Setup(c => c.ClassifyAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ToolDescriptor>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolCallVerdict(true, "ghost_tool", new Dictionary<string, string>(), 0.7f, "hallucinated"));

        var action = MockAction(RecallDescriptor);
        // Strict — verifies InvokeAsync never fires.
        var helper = Build(classifier, action.Object);

        var result = await helper.TryInvokeAsync("do the thing", "", CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Fail-open discipline ───────────────────────────────────────────────

    [Fact]
    public async Task TryInvokeAsync_ClassifierThrows_ReturnsNull()
    {
        var classifier = new Mock<IToolCallClassifier>(MockBehavior.Strict);
        classifier.Setup(c => c.ClassifyAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ToolDescriptor>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var action = MockAction(RecallDescriptor);
        var helper = Build(classifier, action.Object);

        var result = await helper.TryInvokeAsync("what's up", "", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryInvokeAsync_ActionThrows_ReturnsAttributableErrorString()
    {
        var classifier = new Mock<IToolCallClassifier>(MockBehavior.Strict);
        classifier.Setup(c => c.ClassifyAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ToolDescriptor>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ToolCallVerdict(true, "recall_memory",
                new Dictionary<string, string> { ["query"] = "Peru" }, 0.9f, "reason"));

        var action = MockAction(RecallDescriptor);
        action.Setup(a => a.InvokeAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var helper = Build(classifier, action.Object);

        var result = await helper.TryInvokeAsync("do you remember Peru", "", CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().Contain("recall_memory error");
        result.Should().Contain("InvalidOperationException");
    }

    // ── Descriptor plumbing ────────────────────────────────────────────────

    [Fact]
    public async Task TryInvokeAsync_HandsClassifierDescriptorsFromAllActions()
    {
        IReadOnlyList<ToolDescriptor>? capturedDescriptors = null;
        var classifier = new Mock<IToolCallClassifier>(MockBehavior.Strict);
        classifier.Setup(c => c.ClassifyAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ToolDescriptor>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<ToolDescriptor>, string, CancellationToken>(
                (_, descs, _, _) => capturedDescriptors = descs)
            .ReturnsAsync(new ToolCallVerdict(false, null, null, 0.9f, "n/a"));

        var recallDesc = RecallDescriptor;
        var pingDesc = new ToolDescriptor("ping", "Check runtime.", new Dictionary<string, string>());
        var recall = MockAction(recallDesc);
        var ping = MockAction(pingDesc);
        var helper = Build(classifier, recall.Object, ping.Object);

        await helper.TryInvokeAsync("test", "", CancellationToken.None);

        capturedDescriptors.Should().NotBeNull();
        capturedDescriptors!.Select(d => d.Name).Should().BeEquivalentTo(new[] { "recall_memory", "ping" });
    }
}
