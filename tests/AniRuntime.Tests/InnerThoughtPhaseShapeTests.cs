using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 3 (2026-08-18) — verifies
/// <see cref="InnerThoughtPhase"/> wires <see cref="IThoughtShapeClassifier"/>
/// correctly: classifier is called with the generated thought, result is
/// written to <see cref="InnerThoughtResult.Shape"/>, degradation modes
/// (null classifier / flag off / classifier throws) fail open to
/// <see cref="ThoughtShape.Unclassified"/> without blocking the cycle.
/// </summary>
public class InnerThoughtPhaseShapeTests
{
    private readonly Mock<IOllamaClient> _mockOllama = new();

    private const string Thought     = "sitting in the bookstore, windows open like we talked about";
    private const string ValenceJson = "{\"score\": 0.52}";
    private const string ReflectJson = "reflection text";

    private void SetupOllama(string thought)
    {
        _mockOllama.SetupSequence(o => o.InnerMonologueChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(thought)
            .ReturnsAsync(ReflectJson);
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValenceJson);
    }

    private static ContextSnapshot BuildSnapshot() => new()
    {
        CharacterState = new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" },
        DesireState    = new DesireState(),
        EmotionalState = new EmotionalState(),
        BuiltAt        = DateTimeOffset.UtcNow,
    };

    private InnerThoughtPhase BuildPhase(
        IThoughtShapeClassifier? shapeClassifier,
        bool shapeEnabled = true)
    {
        var options = Options.Create(new AniOptions
        {
            ThoughtShapeClassificationEnabled = shapeEnabled,
            UseHybridInnerThoughtCycle        = false,  // exercise the legacy path
        });
        return new InnerThoughtPhase(
            _mockOllama.Object,
            new StubRegisterClassifier(),
            NullLogger<InnerThoughtPhase>.Instance,
            outputGate:        null,
            aniOptions:        options,
            epistemicRenderer: null,
            shapeClassifier:   shapeClassifier);
    }

    [Fact]
    public async Task RunAsync_ClassifierPresentAndEnabled_ShapeFlowsIntoResult()
    {
        SetupOllama(Thought);
        var mockClassifier = new Mock<IThoughtShapeClassifier>();
        mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThoughtShape.CoherentThought);

        var phase = BuildPhase(mockClassifier.Object);
        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Shape.Should().Be(ThoughtShape.CoherentThought);
        mockClassifier.Verify(
            c => c.ClassifyAsync(Thought, It.IsAny<CancellationToken>()),
            Times.Once,
            "classifier must be called with the produced thought");
    }

    [Fact]
    public async Task RunAsync_ClassifierNull_ShapeIsUnclassified()
    {
        SetupOllama(Thought);
        var phase = BuildPhase(shapeClassifier: null);

        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Shape.Should().Be(ThoughtShape.Unclassified);
        result.Thought.Should().Be(Thought, "cycle still proceeds without classifier");
    }

    [Fact]
    public async Task RunAsync_ClassificationDisabledByFlag_ClassifierNotCalled()
    {
        SetupOllama(Thought);
        var mockClassifier = new Mock<IThoughtShapeClassifier>(MockBehavior.Strict);  // must not be called
        var phase = BuildPhase(mockClassifier.Object, shapeEnabled: false);

        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Shape.Should().Be(ThoughtShape.Unclassified);
        mockClassifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_ClassifierThrows_FailsOpenAsUnclassified()
    {
        SetupOllama(Thought);
        var mockClassifier = new Mock<IThoughtShapeClassifier>();
        mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("classifier broken"));

        var phase = BuildPhase(mockClassifier.Object);
        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Shape.Should().Be(ThoughtShape.Unclassified, "classifier failure must NOT block the cycle");
        result.Thought.Should().Be(Thought, "cycle still proceeds with raw thought text");
    }

    [Fact]
    public async Task RunAsync_EmptyThought_ReturnsUnclassifiedWithoutInvokingClassifier()
    {
        SetupOllama(string.Empty);
        var mockClassifier = new Mock<IThoughtShapeClassifier>(MockBehavior.Strict);  // must not be called
        var phase = BuildPhase(mockClassifier.Object);

        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Shape.Should().Be(ThoughtShape.Unclassified);
        result.Thought.Should().BeEmpty();
        mockClassifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_ClassifierCancels_PropagatesCancellation()
    {
        SetupOllama(Thought);
        var mockClassifier = new Mock<IThoughtShapeClassifier>();
        mockClassifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var phase = BuildPhase(mockClassifier.Object);
        var act = () => phase.RunAsync(BuildSnapshot(), new CancellationTokenSource().Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
