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
    private readonly Mock<IPerceptionSource>    _mockSource        = new();
    private readonly Mock<IAniAction>           _mockSmsAction     = new();
    private readonly Mock<IConversationService> _mockConversations = new();

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
        MockMemory.Setup(m => m.GetEmotionalStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new EmotionalState());
        MockMemory.Setup(m => m.SaveEmotionalStateAsync(It.IsAny<EmotionalState>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);

        _mockSource.Setup(s => s.IsEnabled).Returns(true);
        _mockSource.Setup(s => s.SourceName).Returns("test-source");
        _mockSource.Setup(s => s.Category).Returns(PerceptionCategory.Environment);
        _mockSource.Setup(s => s.PollAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<PerceptionEvent>());

        // ActionType must be set on the mock so AniActionDispatcher can build its lookup dictionary
        _mockSmsAction.Setup(a => a.ActionType).Returns(ActionTypes.Sms);

        var sources    = new[] { _mockSource.Object };
        var desire     = new DesireEngine(MockMemory.Object, DefaultOptions, NullLogger<DesireEngine>.Instance);
        var dispatcher = new AniActionDispatcher(
            new[] { _mockSmsAction.Object },
            NullLogger<AniActionDispatcher>.Instance);

        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync((ConversationThread?)null);

        var adminHandler = new AdminCommandHandler(
            MockMemory.Object,
            desire,
            dispatcher,
            DefaultOptions,
            NullLogger<AdminCommandHandler>.Instance);

        return new CognitiveCycleProcessor(
            MockMemory.Object,
            MockOllama.Object,
            desire,
            dispatcher,
            _mockConversations.Object,
            sources,
            adminHandler,
            DefaultOptions,
            NullLogger<CognitiveCycleProcessor>.Instance);
    }

    [Fact]
    public async Task RunAsync_AlwaysSavesInnerThought()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("I'm thinking about Mark today.");
        MockOllama.Setup(o => o.ChatJsonAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""");

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

        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Just a quiet thought.");
        MockOllama.Setup(o => o.ChatJsonAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""");

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

        MockOllama.Setup(o => o.InnerMonologueChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("Just a quiet thought.");
        MockOllama.SetupSequence(o => o.ChatJsonAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""")   // valence score (low — no spontaneous trigger)
                  .ReturnsAsync("""{ "warmth": 0.0, "energy": 0.0, "concern": 0.0, "playfulness": 0.0 }""")  // emotional shift
                  .ReturnsAsync("""{ "shouldReach": true, "confidence": 0.9, "reasoning": "been a while", "triggersActedOn": [] }""");   // outreach decision (no message — separate step now)

        // Step 2: message composition + Step 3: rewrite pass (both use ChatAsync)
        MockOllama.Setup(o => o.ChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("hey mark, thinking of you today.");

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
        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("A thought.");
        MockOllama.Setup(o => o.ChatJsonAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""");

        var processor = CreateProcessor();
        await processor.RunAsync(CancellationToken.None);

        _mockSource.Verify(
            s => s.PollAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once, "all enabled perception sources must be polled each cycle");
    }

    [Fact]
    public async Task RunAsync_WhenOllamaThrows_PropagatesException()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                                          It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new HttpRequestException("Ollama unreachable"));

        var processor = CreateProcessor();

        // Exceptions must propagate — no silent swallowing
        await processor.Invoking(p => p.RunAsync(CancellationToken.None))
                        .Should().ThrowAsync<HttpRequestException>();
    }

    // ── Feature 10: Care detection ──────────────────────────────────────────

    [Theory]
    [InlineData("you okay?", true)]
    [InlineData("hey, how are you doing?", true)]
    [InlineData("just checking in on you", true)]
    [InlineData("everything okay?", true)]
    [InlineData("you seem quiet today", true)]
    [InlineData("u ok?", true)]
    [InlineData("you good?", true)]
    [InlineData("hope you're okay", true)]
    [InlineData("what's wrong?", true)]
    [InlineData("how are you feeling?", true)]
    [InlineData("what are you doing?", false)]
    [InlineData("haha nice", false)]
    [InlineData("can you recommend a restaurant?", false)]
    [InlineData("goodnight!", false)]
    [InlineData("tell me about your day", false)]
    [InlineData("that's hilarious", false)]
    public void DetectCareGivingIntent_CorrectlyClassifiesMessages(string message, bool expected)
    {
        CognitiveCycleProcessor.DetectCareGivingIntent(message).Should().Be(expected);
    }
}
