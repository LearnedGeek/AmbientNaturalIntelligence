using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Options;
using Moq;
using AniRuntime.Core;

namespace AniRuntime.Tests.Infrastructure;

/// <summary>
/// Base class for all ANI Runtime tests.
/// Provides pre-built mocks and default options matching appsettings.json defaults.
///
/// Theme K Phase K.2 (Apr 28, 2026): <see cref="MockMemory"/> is now
/// <see cref="MockBehavior.Strict"/> per the strict-mock migration policy
/// (`~/.claude/TESTING-STRATEGY.md` §20). Every interaction with the memory
/// surface must be explicitly declared by the test (or the test's factory
/// helper). Loose-mock defaults silently returned null/empty values that
/// allowed contract regressions through; strict mode forces each call site
/// to be a documented spec interaction.
/// </summary>
public abstract class AniTestBase
{
    protected readonly Mock<IMemoryService>  MockMemory  = new(MockBehavior.Strict);
    protected readonly Mock<IOllamaClient>   MockOllama  = new();
    protected readonly Mock<IAniAction>      MockAction  = new();

    protected AniTestBase()
    {
        // Default emotional state mock — many tests need this since DesireEngine reads it.
        // This is the only universal default; everything else belongs to the test class's
        // factory helper so each class controls which calls it expects.
        MockMemory.Setup(m => m.GetEmotionalStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new EmotionalState());
    }

    protected IOptions<AniOptions> DefaultOptions => Options.Create(new AniOptions
    {
        DesireLambdaMinutes    = 8.0,
        ThinkTargetProbability = 0.70,
        MinWakeMinutes         = 2.0,
        MaxWakeMinutes         = 45.0,
        CooldownMinutes        = 20.0,
        MinOutreachGapMinutes  = 60.0,
        MaxOutreachPerDay      = 4,
        // Disable night/morning gates in unit tests — these are time-dependent
        // and cause false failures when tests run after 10pm.
        MaxNightOutreach       = 100,
    });

    protected IOptions<OllamaOptions> DefaultOllamaOptions => Options.Create(new OllamaOptions());

    protected static DesireState FreshDesireState() => new()
    {
        DesireToConnect  = 0.0f,
        CooldownActive   = false,
        LastContactInbound  = DateTimeOffset.UtcNow,
        LastInnerThought = DateTimeOffset.UtcNow,
        CircadianModifier = 1.0f,
    };

    protected static DesireState HighDesireState() => new()
    {
        DesireToConnect   = 0.9f,
        CooldownActive    = false,
        LastContactInbound   = DateTimeOffset.UtcNow.AddHours(-8),
        LastInnerThought  = DateTimeOffset.UtcNow.AddMinutes(-30),
        CircadianModifier = 1.0f,
    };

    /// <summary>
    /// 2026-05-19 — emotional context is now its own sub-builder. Tests that
    /// construct <c>ContextBuilder</c> but aren't exercising emotional
    /// concerns inject this no-op mock so they don't need to set up the four
    /// underlying retrieval mocks. Emotional concerns have dedicated tests
    /// in <c>EmotionalContextBuilderTests</c>.
    /// </summary>
    protected static IEmotionalContextBuilder NoOpEmotional()
    {
        var mock = new Mock<IEmotionalContextBuilder>(MockBehavior.Strict);
        // F-1 Phase 8f: BuildAsync now returns IEmotionalContextEnvelope;
        // wrap the no-op result for the mock. ContextBuilder unwraps
        // via .Content immediately, so downstream test behavior is
        // unchanged.
        mock.Setup(b => b.BuildAsync(
                It.IsAny<string>(), It.IsAny<EmotionalState>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmotionalContextEnvelope
            {
                Result = new EmotionalContextResult(
                    RelationshipHealth: null,
                    EmotionalDrift:     null,
                    PatternAwareness:   null,
                    ProcessedThemes:    Array.Empty<string>()),
            });
        return mock.Object;
    }

    /// <summary>
    /// 2026-05-19 — retrieval context is now its own sub-builder. Tests that
    /// construct <c>ContextBuilder</c> but aren't exercising retrieval
    /// concerns inject this no-op mock returning empty lists. Retrieval
    /// concerns have dedicated tests in <c>RetrievalContextBuilderTests</c>.
    /// </summary>
    protected static IRetrievalContextBuilder NoOpRetrieval()
    {
        var mock = new Mock<IRetrievalContextBuilder>(MockBehavior.Strict);
        mock.Setup(b => b.BuildAsync(
                It.IsAny<IReadOnlyList<PerceptionEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetrievalContextResult(
                RecentMemory:           new List<MemoryRecord>(),
                AnchoredMemories:       new List<MemoryRecord>(),
                RelevantMemory:         new List<MemoryRecord>(),
                SimilarRecentThoughts:  new List<MemoryRecord>()));
        return mock.Object;
    }

    /// <summary>
    /// 2026-05-19 — conversation context is now its own sub-builder. Tests that
    /// construct <c>ContextBuilder</c> but aren't exercising conversation-
    /// history concerns inject this no-op mock returning all-null fields.
    /// Conversation concerns have dedicated tests in
    /// <c>ConversationContextBuilderTests</c>.
    /// </summary>
    protected static IConversationContextBuilder NoOpConversation()
    {
        var mock = new Mock<IConversationContextBuilder>(MockBehavior.Strict);
        mock.Setup(b => b.BuildAsync(
                It.IsAny<CharacterStateDoc>(),
                It.IsAny<IReadOnlyList<MemoryRecord>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationContextResult(
                RecentConversationSummary:     null,
                StructuredConversationSummary: null,
                RecentClosedConversation:      null));
        return mock.Object;
    }

    /// <summary>
    /// 2026-05-19 — epistemic context is now its own sub-builder. Tests that
    /// construct <c>ContextBuilder</c> but aren't exercising tier-scoped
    /// retrieval inject this no-op mock returning empty pools. Epistemic
    /// concerns have dedicated tests in <c>EpistemicContextBuilderTests</c>.
    /// </summary>
    protected static IEpistemicContextBuilder NoOpEpistemic()
    {
        var mock = new Mock<IEpistemicContextBuilder>(MockBehavior.Strict);
        mock.Setup(b => b.BuildAsync(
                It.IsAny<IReadOnlyList<PerceptionEvent>>(),
                It.IsAny<IReadOnlyList<MemoryRecord>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpistemicContextResult(
                GroundedFacts:   new List<MemoryRecord>(),
                InteriorContext: new List<MemoryRecord>()));
        return mock.Object;
    }

    /// <summary>
    /// 2026-05-19 — state context is now its own sub-builder. Tests that
    /// construct <c>ContextBuilder</c> but aren't exercising state pulls
    /// inject this no-op mock returning a minimal default state. State
    /// concerns have dedicated tests in <c>StateContextBuilderTests</c>.
    /// </summary>
    protected static IStateContextBuilder NoOpState(
        CharacterStateDoc? characterState = null)
    {
        var mock = new Mock<IStateContextBuilder>(MockBehavior.Strict);
        mock.Setup(b => b.BuildAsync(
                It.IsAny<IReadOnlyList<MemoryRecord>>(),
                It.IsAny<EmotionalState?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StateContextResult(
                CharacterState:         characterState ?? new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" },
                DesireState:            FreshDesireState(),
                EmotionalState:         new EmotionalState(),
                OpenLoops:              new List<OpenLoop>(),
                OutreachContext:        new RecentOutreachContext(),
                ThoughtDiversityNudge:  null));
        return mock.Object;
    }
}
