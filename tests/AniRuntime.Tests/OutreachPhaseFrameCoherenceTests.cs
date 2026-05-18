using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Loops.Coreference;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme N Phase N.5 (May 10, 2026) — spec tests for the
/// <see cref="IFrameCoherenceChecker"/> wiring inside
/// <see cref="OutreachPhase"/>.
///
/// These tests cover the integration boundary: when the checker
/// returns a coherence violation, OutreachPhase must (a) not dispatch,
/// (b) decay desire 0.30 + apply a 10-minute cooldown — the same
/// suppression shape as the universal output-gate-fail path. When the
/// checker returns Coherent, OutreachPhase must proceed past the
/// coherence step into the post-coherence pipeline (echo guard,
/// rewrite, claim verification, gate).
///
/// Strict-mock policy per Theme K K.0 (see <see cref="AniTestBase"/>).
/// </summary>
public class OutreachPhaseFrameCoherenceTests : AniTestBase
{
    private readonly Mock<IConversationService>     _mockConversations  = new(MockBehavior.Strict);
    private readonly Mock<IOutreachFrameSelector>   _mockFrameSelector  = new(MockBehavior.Strict);
    private readonly Mock<IFrameCoherenceChecker>   _mockFrameChecker   = new(MockBehavior.Strict);

    private const string DecisionShouldReachJson =
        "{\"shouldReach\": true, \"confidence\": 0.9, \"reasoning\": \"high desire to connect\"}";

    /// <summary>
    /// Composition output the test mocks the LLM to return. Non-empty
    /// so we get past the line 298 empty-message guard and reach the
    /// N.5 coherence step. Whether the checker treats this as
    /// coherent or violating is controlled by the
    /// <see cref="_mockFrameChecker"/> setup.
    /// </summary>
    private const string ComposedMessage = "hey, just thinking about you tonight.";

    private OutreachPhase BuildPhase(IOptions<AniOptions>? options = null)
    {
        var dispatcher = new AniActionDispatcher(
            Array.Empty<IAniAction>(),
            NullLogger<AniActionDispatcher>.Instance);

        var desire = new DesireEngine(
            MockMemory.Object, MockMemory.Object,
            options ?? DefaultOptions,
            NullLogger<DesireEngine>.Instance);

        var claimVerifier = new ClaimVerificationPhase(
            MockMemory.Object, MockOllama.Object,
            options ?? DefaultOptions,
            NullLogger<ClaimVerificationPhase>.Instance);

        return OutreachPhaseTestBuilder.Build(
            MockMemory.Object,
            MockMemory.Object,
            MockMemory.Object,
            MockOllama.Object,
            dispatcher,
            desire,
            claimVerifier,
            _mockConversations.Object,
            options ?? DefaultOptions,
            mlClassifier:   null,
            personaCache:   null,
            em9Detector:    null,
            outputGate:     null,
            vibeBias:       null,
            frameSelector:  _mockFrameSelector.Object,
            frameChecker:   _mockFrameChecker.Object);
    }

    private static ContextSnapshot BuildSnapshot() => new()
    {
        BuiltAt        = DateTimeOffset.UtcNow,
        CharacterState = new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" },
        EmotionalState = new EmotionalState(),
        DesireState    = new DesireState { DesireToConnect = 0.8f },
    };

    /// <summary>
    /// Setup the Ollama mock so the decision step parses to ShouldReach=true
    /// and the composition step returns <see cref="ComposedMessage"/>.
    /// </summary>
    private void SetupOllamaForDecisionAndCompose()
    {
        // 2026-05-15: outreach composition migrated from ChatAsync to
        // ChatJsonAsync. First call = decision JSON, second call =
        // composition JSON with .message=ComposedMessage.
        var compositionJson = $"{{\"message\": \"{ComposedMessage}\", \"notes\": null}}";
        MockOllama
            .SetupSequence(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecisionShouldReachJson)
            .ReturnsAsync(compositionJson);

        // ChatAsync may still be reached for downstream rewrite passes;
        // return the same message so post-composition flow works.
        MockOllama
            .Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync(ComposedMessage);
    }

    /// <summary>
    /// Setup the strict memory mock with the minimum write/read calls the
    /// suppression path actually invokes.
    /// </summary>
    private void SetupMemoryForSuppressionPath()
    {
        MockMemory.Setup(m => m.SearchWithScoresAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScoredMemory>());

        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesireState { DesireToConnect = 0.8f });

        MockMemory.Setup(m => m.SaveDesireStateAsync(
                It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// SPEC: when the selector returns AniInterior and the checker
    /// returns IsCoherent=false, the dispatch is suppressed and the
    /// desire engine is decayed + cooled down. No further LLM calls
    /// (echo embed, claim verifier) are made.
    /// </summary>
    [Fact]
    public async Task RunOutreach_FlagOn_CheckerReturnsViolation_SuppressesAndDecaysDesire()
    {
        var options = Options.Create(new AniOptions
        {
            MaxNightOutreach             = 100,
            OutreachFrameSelectorEnabled = true,
        });

        SetupOllamaForDecisionAndCompose();
        SetupMemoryForSuppressionPath();

        var aniInteriorFrame = new OutreachFrame(
            OutreachFrameType.AniInterior, Anchor: "i was thinking", Confidence: 0.8f);

        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(aniInteriorFrame);

        _mockFrameChecker.Setup(c => c.CheckCoherence(
                It.IsAny<string>(), It.IsAny<OutreachFrame>()))
            .Returns(new FrameCoherenceResult(
                IsCoherent: false,
                Violations: new[] { "Mia told us: \"Mia told us\"" },
                Reason:     "test violation"));

        var phase = BuildPhase(options);

        await phase.RunOutreachAsync(BuildSnapshot(), "i was thinking about him", CancellationToken.None);

        _mockFrameChecker.Verify(c => c.CheckCoherence(
            ComposedMessage, aniInteriorFrame),
            Times.Once,
            "the checker must run once on the composed message + selected frame");

        // The suppression path goes through DesireEngine.DecayDesireAsync
        // and ApplyCooldownAsync, both of which call SaveDesireStateAsync.
        // Two saves = decay + cooldown.
        MockMemory.Verify(m => m.SaveDesireStateAsync(
            It.IsAny<DesireState>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "DecayDesireAsync + ApplyCooldownAsync must each persist desire state on coherence violation");
    }

    /// <summary>
    /// SPEC: when the checker returns Coherent under a non-Shared
    /// frame, the pipeline proceeds past N.5 into the downstream
    /// post-coherence steps. We drive the full happy-path tail (echo
    /// guard / claim verifier disabled / persist) to confirm dispatch
    /// proceeds; key assertion is that the checker IS invoked on the
    /// composed message + selected frame.
    /// </summary>
    [Fact]
    public async Task RunOutreach_FlagOn_CheckerReturnsCoherent_ProceedsPastCoherenceStep()
    {
        var options = Options.Create(new AniOptions
        {
            MaxNightOutreach             = 100,
            OutreachFrameSelectorEnabled = true,
            // Disable claim verification so the cycle skips the verifier
            // tail (which would otherwise need its own Ollama mock setup).
            ClaimVerificationEnabled     = false,
        });

        SetupOllamaForDecisionAndCompose();
        SetupMemoryForSuppressionPath();
        SetupConversationsAndPersistTail();

        // Echo-guard EmbedAsync setup. Returning an empty float[] makes
        // the echo check no-op cleanly (early exit on empty embedding).
        MockOllama
            .Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<float>());

        var aniInteriorFrame = new OutreachFrame(
            OutreachFrameType.AniInterior, Anchor: "i was thinking", Confidence: 0.8f);

        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(aniInteriorFrame);

        _mockFrameChecker.Setup(c => c.CheckCoherence(
                It.IsAny<string>(), It.IsAny<OutreachFrame>()))
            .Returns(FrameCoherenceResult.Coherent);

        var phase = BuildPhase(options);

        await phase.RunOutreachAsync(BuildSnapshot(), "i was thinking about him", CancellationToken.None);

        _mockFrameChecker.Verify(c => c.CheckCoherence(
            ComposedMessage, aniInteriorFrame),
            Times.Once,
            "the checker must run on the composed message under the selected frame");

        // Coherent path proceeded — the persist step at the tail of the
        // happy path was reached.
        MockMemory.Verify(m => m.SaveAsync(
            It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the post-dispatch persist must run on the Coherent path (proves we got past the coherence step)");
    }

    /// <summary>
    /// SPEC: when the selector returns the Shared frame, the checker
    /// is NOT called (Shared is the only frame where shared predicates
    /// are valid; calling the checker would be wasted work). Verifies
    /// the gating shape inside OutreachPhase, not the checker's
    /// internal Shared-no-op behavior (that is covered in
    /// <see cref="FrameCoherenceCheckerTests"/>).
    /// </summary>
    [Fact]
    public async Task RunOutreach_SharedFrame_CheckerNotInvoked()
    {
        var options = Options.Create(new AniOptions
        {
            MaxNightOutreach             = 100,
            OutreachFrameSelectorEnabled = true,
            ClaimVerificationEnabled     = false,
        });

        SetupOllamaForDecisionAndCompose();
        SetupMemoryForSuppressionPath();
        SetupConversationsAndPersistTail();

        MockOllama
            .Setup(o => o.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<float>());

        var sharedFrame = new OutreachFrame(
            OutreachFrameType.Shared, Anchor: "Mark said: had a long day", Confidence: 0.9f);

        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sharedFrame);

        // No setup on _mockFrameChecker — strict mock will throw if called.

        var phase = BuildPhase(options);

        await phase.RunOutreachAsync(BuildSnapshot(), "i was thinking about him", CancellationToken.None);

        _mockFrameChecker.Verify(c => c.CheckCoherence(
            It.IsAny<string>(), It.IsAny<OutreachFrame>()),
            Times.Never,
            "the checker is short-circuited under the Shared frame (no forbidden predicates apply)");
    }

    /// <summary>
    /// Strict-mock setups for the post-coherence happy-path tail:
    /// conversations get/save/add (RecordOutboundInThreadAsync) +
    /// memory SaveAsync (post-dispatch persist).
    /// </summary>
    private void SetupConversationsAndPersistTail()
    {
        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationThread?)null);
        _mockConversations.Setup(c => c.SaveThreadAsync(
                It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConversations.Setup(c => c.AddMessageAsync(
                It.IsAny<Guid>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        MockMemory.Setup(m => m.SaveAsync(
                It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }
}
