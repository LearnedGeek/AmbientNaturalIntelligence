using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Emergence;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme K Phase K.2 (Apr 28, 2026): TDD-style spec tests pinning architectural
/// invariants at the IMemoryService boundary that the loose-mock suite never
/// observed. Each test wires a separate strict <see cref="IMemoryPersistence"/>
/// mock for the cognitive-cycle processor's persistence slot — distinct from
/// the persistence handle injected into <see cref="DesireEngine"/> — so we can
/// verify which collaborator owns which write.
///
/// The canonical invariant pinned here is the one declared in DesireEngine.cs
/// header: *"All DesireState writes go through this class. CognitiveCycleProcessor
/// must never call IMemoryService.SaveDesireStateAsync() directly."* Until this
/// test class shipped that invariant lived only as a comment — strict mocks now
/// pin it as a test.
/// </summary>
public class CognitiveCyclePersistenceContractTests : AniTestBase
{
    private readonly Mock<IPerceptionSource>     _mockSource          = new();
    private readonly Mock<IAniAction>            _mockSmsAction       = new();
    private readonly Mock<IConversationService>  _mockConversations   = new(MockBehavior.Strict);
    private readonly Mock<IIntentExtractor>      _mockIntent          = new();
    private readonly Mock<IReplyChannelResolver> _mockChannelResolver = new();

    /// <summary>
    /// Strict persistence mock that the <see cref="CognitiveCycleProcessor"/>
    /// receives in its <c>persist</c> parameter. <see cref="DesireEngine"/>
    /// receives a *different* persistence handle (<see cref="MockMemory"/>)
    /// in <see cref="BuildProcessor"/> — this isolation is what lets us verify
    /// the contract that the processor never reaches around DesireEngine.
    /// </summary>
    private readonly Mock<IMemoryPersistence> _processorPersist = new(MockBehavior.Strict);

    private CognitiveCycleProcessor BuildProcessor()
    {
        // ── MockMemory setups (same shape as the main test file's CreateProcessor;
        // every call the rest of the cycle makes is declared here).
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
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetActiveContributionsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalContribution>());
        MockMemory.Setup(m => m.SaveEmotionalContributionAsync(It.IsAny<EmotionalContribution>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.CleanupDecayedContributionsAsync(It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());
        MockMemory.Setup(m => m.GetFlaggedContradictionsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryContradiction>());

        // ── Processor's IMemoryPersistence (strict, distinct from MockMemory):
        // declare ONLY the calls the processor is allowed to make on its own
        // persistence handle. SaveDesireStateAsync is deliberately NOT declared
        // because the contract is "the processor must never call it" — strict
        // mode turns that omission into an enforced spec.
        _processorPersist.Setup(p => p.SaveEmotionalStateAsync(
                It.IsAny<EmotionalState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _processorPersist.Setup(p => p.SaveAsync(
                It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _processorPersist.Setup(p => p.SaveEmotionalContributionAsync(
                It.IsAny<EmotionalContribution>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _processorPersist.Setup(p => p.GetRecentAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MemoryRecord>());

        _mockIntent.Setup(i => i.ExtractIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((string msg, CancellationToken _) => msg);

        var mockChannel = new Mock<IReplyChannel>();
        mockChannel.Setup(c => c.ChannelId).Returns("sms");
        mockChannel.Setup(c => c.SendReplyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);
        _mockChannelResolver.Setup(r => r.Resolve(It.IsAny<string>())).Returns(mockChannel.Object);
        _mockChannelResolver.Setup(r => r.Default).Returns(mockChannel.Object);

        _mockSource.Setup(s => s.IsEnabled).Returns(true);
        _mockSource.Setup(s => s.SourceName).Returns("test-source");
        _mockSource.Setup(s => s.Category).Returns(PerceptionCategory.Environment);
        _mockSource.Setup(s => s.PollAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(Array.Empty<PerceptionEvent>());

        _mockSmsAction.Setup(a => a.ActionType).Returns(ActionTypes.Sms);

        // DesireEngine receives MockMemory as its persistence handle — its own
        // SaveDesireStateAsync calls land there, NOT on _processorPersist.
        var desire = new DesireEngine(
            MockMemory.Object, MockMemory.Object, DefaultOptions,
            NullLogger<DesireEngine>.Instance);

        var dispatcher = new AniActionDispatcher(
            new[] { _mockSmsAction.Object },
            NullLogger<AniActionDispatcher>.Instance);

        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync((ConversationThread?)null);

        var mockDiagnostic = new Mock<IDiagnosticService>();
        mockDiagnostic.Setup(d => d.RunDiagnosticAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnosticReport());

        var emotional = new EmotionalProcessor(
            MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, new StubRegisterClassifier(), DefaultOptions,
            NullLogger<EmotionalProcessor>.Instance);
        var contextBuilder = new ContextBuilder(
            NoOpState(), NoOpRetrieval(), NoOpEpistemic(), NoOpConversation(), NoOpEmotional(),
            DefaultOptions, NullLogger<ContextBuilder>.Instance);
        var keywordExtractor = new KeywordExtractor(
            MockMemory.Object, NullLogger<KeywordExtractor>.Instance);
        var gateState = new ConversationGateState();
        var compressor = new ContextCompressor(MockOllama.Object, NullLogger<ContextCompressor>.Instance);
        var claimVerifier = new ClaimVerificationPhase(
            MockMemory.Object, MockOllama.Object, DefaultOptions,
            NullLogger<ClaimVerificationPhase>.Instance);
        var conversationReply = ConversationReplyPhaseTestBuilder.Build(
            MockMemory.Object, MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, _mockConversations.Object,
            _mockChannelResolver.Object, dispatcher, desire, emotional, contextBuilder, keywordExtractor,
            _mockIntent.Object, gateState, compressor, claimVerifier, new WithdrawalStateTracker(), DefaultOptions, DefaultOllamaOptions);
        var outreach = OutreachPhaseTestBuilder.Build(
            MockMemory.Object, MockMemory.Object, MockMemory.Object, MockOllama.Object,
            dispatcher, desire, claimVerifier, _mockConversations.Object, DefaultOptions);
        var perception = new PerceptionPhase(
            new[] { _mockSource.Object }, MockMemory.Object, NullLogger<PerceptionPhase>.Instance);
        var innerThought = new InnerThoughtPhase(
            MockOllama.Object, new StubRegisterClassifier(), NullLogger<InnerThoughtPhase>.Instance);
        var reflection = new ReflectionPhase(
            MockOllama.Object, MockMemory.Object, MockMemory.Object, DefaultOptions,
            NullLogger<ReflectionPhase>.Instance);

        var pipeline = new CognitiveCyclePipeline(
            MockMemory.Object,           // state (IStateStore)
            _processorPersist.Object,    // persist — the slot under test
            MockMemory.Object,           // analytics
            MockMemory.Object,           // maintenance
            desire,
            _mockConversations.Object,
            new NullEmergenceObserver(),
            emotional,
            contextBuilder,
            perception,
            innerThought,
            conversationReply,
            outreach,
            reflection,
            gateState,
            new WorldSeedService(new OptionsWrapper<WorldSeedOptions>(new WorldSeedOptions()), NullLogger<WorldSeedService>.Instance),
            DefaultOptions,
            NullLogger<CognitiveCyclePipeline>.Instance);
        return new CognitiveCycleProcessor(pipeline);
    }

    /// <summary>
    /// SPEC: <see cref="CognitiveCycleProcessor"/> MUST NOT call
    /// <see cref="IMemoryPersistence.SaveDesireStateAsync"/> on its own
    /// persistence handle. All DesireState writes are owned by
    /// <see cref="DesireEngine"/> per the class header invariant.
    ///
    /// The spec lives in source as a comment; this test pins it. Strict mocks
    /// catch a regression the moment the processor reaches around DesireEngine
    /// (the kind of architectural-invariant violation that loose mocks let
    /// through silently because the call would just hit a no-op default).
    /// </summary>
    [Fact]
    public async Task RunAsync_DesireStateWrites_RoutedExclusivelyThroughDesireEngine()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                  .ReturnsAsync("a quiet thought");
        MockOllama.Setup(o => o.ChatJsonAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""");

        var processor = BuildProcessor();

        // If the processor ever calls SaveDesireStateAsync on _processorPersist,
        // strict mode raises immediately. The Verify below is belt-and-suspenders.
        await processor.RunAsync(CancellationToken.None);

        _processorPersist.Verify(
            p => p.SaveDesireStateAsync(It.IsAny<DesireState>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "DesireState writes must flow through DesireEngine — the processor's own " +
            "IMemoryPersistence handle must never receive SaveDesireStateAsync");
    }

    /// <summary>
    /// SPEC: every cycle MUST persist exactly one EmotionalState write via the
    /// processor's IMemoryPersistence (Phase 0). This is the only surface that
    /// owns end-of-phase-0 emotional persistence; if a future refactor moved
    /// the call elsewhere we want the test to flag it.
    /// </summary>
    [Fact]
    public async Task RunAsync_PersistsEmotionalState_ExactlyOncePerCycle()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                  .ReturnsAsync("a quiet thought");
        MockOllama.Setup(o => o.ChatJsonAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.3 }""");

        var processor = BuildProcessor();
        await processor.RunAsync(CancellationToken.None);

        _processorPersist.Verify(
            p => p.SaveEmotionalStateAsync(It.IsAny<EmotionalState>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Phase 0 emotional state persistence must run exactly once per cycle");
    }

    /// <summary>
    /// SPEC: an inner thought MUST be persisted via the processor's
    /// IMemoryPersistence (a SaveAsync of a MemoryRecord whose Type is
    /// InnerThought). This pins the inner-thought-persistence side of the
    /// inner-monologue contract on the persistence channel directly — distinct
    /// from the existing test that asserts the call lands on the composite
    /// MockMemory mock.
    /// </summary>
    [Fact]
    public async Task RunAsync_InnerThought_PersistedThroughProcessorPersistence()
    {
        MockOllama.Setup(o => o.InnerMonologueChatAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                  .ReturnsAsync("I'm thinking about Mark today.");
        MockOllama.Setup(o => o.ChatJsonAsync(
                      It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                      It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync("""{ "score": 0.7 }"""); // valence high enough to persist

        var processor = BuildProcessor();
        await processor.RunAsync(CancellationToken.None);

        _processorPersist.Verify(
            p => p.SaveAsync(
                It.Is<MemoryRecord>(r => r.Type == MemoryType.InnerThought),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "the inner-thought persistence channel must run on the processor's IMemoryPersistence handle");
    }
}
