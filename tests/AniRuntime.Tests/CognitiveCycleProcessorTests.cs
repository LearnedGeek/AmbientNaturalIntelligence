using AniRuntime.Actions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

public class CognitiveCycleProcessorTests : AniTestBase
{
    private readonly Mock<IPerceptionSource> _mockSource    = new();
    private readonly Mock<IAniAction>        _mockSmsAction = new();

    private CognitiveCycleProcessor CreateProcessor()
    {
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(FreshDesireState());
        MockMemory.Setup(m => m.SaveDesireStateAsync(It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc());
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetOpenLoopsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<OpenLoop>());
        MockMemory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);

        _mockSource.Setup(s => s.IsEnabled).Returns(true);
        _mockSource.Setup(s => s.SourceName).Returns("test-source");
        _mockSource.Setup(s => s.Category).Returns(PerceptionCategory.Environment);
        _mockSource.Setup(s => s.PollAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<PerceptionEvent>());

        // ActionType must be set on the mock so AniActionDispatcher can build its lookup dictionary
        _mockSmsAction.Setup(a => a.ActionType).Returns(ActionTypes.Sms);

        var sources    = new[] { _mockSource.Object };
        var desire     = new DesireEngine(MockMemory.Object, DefaultOptions);
        var dispatcher = new AniActionDispatcher(
            new[] { _mockSmsAction.Object },
            NullLogger<AniActionDispatcher>.Instance);

        return new CognitiveCycleProcessor(
            MockMemory.Object,
            MockOllama.Object,
            desire,
            dispatcher,
            sources,
            NullLogger<CognitiveCycleProcessor>.Instance);
    }

    [Fact]
    public async Task RunAsync_AlwaysSavesInnerThought()
    {
        MockOllama.Setup(o => o.ChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("I'm thinking about Mark today.");

        var processor = CreateProcessor();
        await processor.RunAsync(CancellationToken.None);

        MockMemory.Verify(m => m.SaveAsync(
            It.Is<MemoryRecord>(r => r.Type == MemoryType.InnerThought),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_WhenDesireBelowThreshold_DoesNotDispatch()
    {
        // Fresh state with no desire — outreach should not fire
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(FreshDesireState() with { DesireToConnect = 0.0f });

        MockOllama.Setup(o => o.ChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Just a quiet thought.");

        _mockSmsAction.Setup(a => a.ActionType).Returns(ActionTypes.Sms);
        _mockSmsAction.Setup(a => a.ExecuteAsync(It.IsAny<OutreachDecision>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(true);

        var processor = CreateProcessor();
        await processor.RunAsync(CancellationToken.None);

        _mockSmsAction.Verify(
            a => a.ExecuteAsync(It.IsAny<OutreachDecision>(), It.IsAny<CancellationToken>()),
            Times.Never, "should not dispatch when desire is below threshold");
    }

    [Fact]
    public async Task RunAsync_WhenOutreachDecisionSaysReach_Dispatches()
    {
        // Create processor first — then apply test-specific mock overrides
        // (CreateProcessor sets up defaults; overrides below take precedence as last-registered setup wins)
        var processor = CreateProcessor();

        // Override desire state: max desire, no cooldown — threshold always crossed
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(HighDesireState() with { DesireToConnect = 1.0f, CooldownActive = false });

        MockOllama.SetupSequence(o => o.ChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Just a quiet thought.")   // inner thought
                  .ReturnsAsync("""{ "score": 0.3 }""")   // valence score (low — no spontaneous trigger)
                  .ReturnsAsync("""{ "shouldReach": true, "message": "Hey Mark, thinking of you!", "actionType": "sms", "confidence": 0.9, "reasoning": "been a while", "triggersActedOn": [] }""");

        _mockSmsAction.Setup(a => a.ExecuteAsync(It.IsAny<OutreachDecision>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(true);

        await processor.RunAsync(CancellationToken.None);

        _mockSmsAction.Verify(
            a => a.ExecuteAsync(
                It.Is<OutreachDecision>(d => d.ShouldReach && d.ActionType == ActionTypes.Sms),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_PollsAllRegisteredPerceptionSources()
    {
        MockOllama.Setup(o => o.ChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("A thought.");

        var processor = CreateProcessor();
        await processor.RunAsync(CancellationToken.None);

        _mockSource.Verify(
            s => s.PollAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once, "all enabled perception sources must be polled each cycle");
    }

    [Fact]
    public async Task RunAsync_WhenOllamaThrows_PropagatesException()
    {
        MockOllama.Setup(o => o.ChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new HttpRequestException("Ollama unreachable"));

        var processor = CreateProcessor();

        // Exceptions must propagate — no silent swallowing
        await processor.Invoking(p => p.RunAsync(CancellationToken.None))
                        .Should().ThrowAsync<HttpRequestException>();
    }
}
