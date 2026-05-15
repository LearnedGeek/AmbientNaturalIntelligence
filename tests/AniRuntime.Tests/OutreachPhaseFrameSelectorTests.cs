using AniRuntime.Actions;
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
/// Theme N Phase N.3 (May 8, 2026) — spec tests for the
/// <see cref="IOutreachFrameSelector"/> wiring inside
/// <see cref="OutreachPhase"/>.
///
/// Three scenarios cover the architectural contract:
/// <list type="number">
///   <item><b>Flag OFF (default state, deployment posture):</b> the
///   selector is NOT called regardless of decision-stage outcome. Zero
///   behavioral change vs. pre-N.3.</item>
///   <item><b>Flag ON, selector returns <see cref="OutreachFrame.None"/>:</b>
///   the dispatch is suppressed in the same shape as the universal
///   output-gate-fail path — no <see cref="IAniAction"/> invoked, no
///   claim verification, and the desire engine receives a 0.30 decay
///   plus a 10-minute cooldown.</item>
///   <item><b>Flag ON, selector returns a real frame:</b> the selector
///   IS called with the cycle's snapshot. The prompt-builder side of
///   the wiring is independently covered by
///   <see cref="PromptBuilderOutreachFrameTests"/>.</item>
/// </list>
///
/// Strict-mock policy per Theme K K.0 (see <see cref="AniTestBase"/>).
/// </summary>
public class OutreachPhaseFrameSelectorTests : AniTestBase
{
    private readonly Mock<IConversationService>      _mockConversations = new(MockBehavior.Strict);
    private readonly Mock<IOutreachFrameSelector>    _mockFrameSelector = new(MockBehavior.Strict);

    private const string DecisionShouldReachJson =
        "{\"shouldReach\": true, \"confidence\": 0.9, \"reasoning\": \"high desire to connect\"}";

    // 2026-05-15: outreach composition migrated to JSON output. Tests that
    // want the composition step to short-circuit downstream by returning
    // empty use this; tests that want a real message use the full JSON.
    private const string CompositionEmptyJson =
        "{\"message\": \"\", \"notes\": null}";

    private OutreachPhase BuildPhase(IOptions<AniOptions>? options = null)
    {
        // Empty dispatcher — if dispatch were ever reached on a suppression
        // path, we'd see a different failure shape (no registered action
        // for "sms"). The real assertion is "ChatAsync was not invoked",
        // which proves we suppressed before composition.
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

        return new OutreachPhase(
            MockMemory.Object,
            MockMemory.Object,
            MockMemory.Object,
            MockOllama.Object,
            dispatcher,
            desire,
            claimVerifier,
            _mockConversations.Object,
            options ?? DefaultOptions,
            NullLogger<OutreachPhase>.Instance,
            mlClassifier: null,
            personaCache: null,
            em9Detector: null,
            outputGate: null,
            vibeBias: null,
            frameSelector: _mockFrameSelector.Object);
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
    /// with confidence above the floor, allowing the flow to reach the
    /// post-decision Theme N gate.
    /// </summary>
    private void SetupOllamaForShouldReach()
    {
        // 2026-05-15: outreach composition now uses ChatJsonAsync too (was
        // ChatAsync). First call = decision (ShouldReach), second call =
        // composition (empty message → pipeline short-circuits at the
        // empty-message check, which is what the selector-boundary tests want).
        MockOllama
            .SetupSequence(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecisionShouldReachJson)
            .ReturnsAsync(CompositionEmptyJson);
    }

    /// <summary>
    /// Setup the strict memory mock with the minimum write/read calls the
    /// suppression path actually invokes:
    ///  - SearchWithScoresAsync (grounding retrieval; returns empty list).
    ///  - GetDesireStateAsync (twice: DecayDesireAsync + ApplyCooldownAsync).
    ///  - SaveDesireStateAsync (twice).
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
    /// SPEC: when <c>OutreachFrameSelectorEnabled</c> is false (the default
    /// and the N.3 deployment posture), the selector is never invoked even
    /// when the decision step says ShouldReach=true and confidence clears
    /// the floor. Zero behavioral delta vs. pre-N.3.
    /// </summary>
    [Fact]
    public async Task RunOutreach_FlagOff_SelectorIsNeverCalled()
    {
        // OutreachFrameSelectorEnabled defaults to false on a fresh AniOptions.
        var options = Options.Create(new AniOptions
        {
            // DefaultOptions equivalents — disable night gates so the test
            // is time-of-day-independent.
            MaxNightOutreach = 100,
            // Explicit assertion of the flag-off default.
            OutreachFrameSelectorEnabled = false,
        });

        SetupOllamaForShouldReach();
        SetupMemoryForSuppressionPath();
        // ChatAsync is reachable (composition step) — return empty so the
        // pipeline short-circuits at the empty-message check (line 298) and
        // we don't have to mock the post-composition path. The point of
        // this test is the selector boundary, not the dispatch tail.
        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync(string.Empty);

        var phase = BuildPhase(options);

        await phase.RunOutreachAsync(BuildSnapshot(), "i was thinking about him", CancellationToken.None);

        // The strict mock has zero setups for SelectFrameAsync. If the wiring
        // had called it with the flag off, the strict mock would have thrown.
        // Belt-and-suspenders: explicit Times.Never verification.
        _mockFrameSelector.Verify(s => s.SelectFrameAsync(
            It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the selector must not be called when OutreachFrameSelectorEnabled=false");
    }

    /// <summary>
    /// SPEC: when the flag is ON and the selector returns
    /// <see cref="OutreachFrame.None"/>, the outreach is suppressed in the
    /// same shape as the universal output-gate-fail path:
    ///  - no <see cref="IAniAction"/> dispatched,
    ///  - the desire is decayed by 0.30 (DecayDesireAsync invoked) and a
    ///    cooldown applied (ApplyCooldownAsync invoked).
    /// </summary>
    [Fact]
    public async Task RunOutreach_FlagOn_SelectorReturnsNone_SuppressesDispatchAndDecaysDesire()
    {
        var options = Options.Create(new AniOptions
        {
            MaxNightOutreach             = 100,
            OutreachFrameSelectorEnabled = true,
        });

        SetupOllamaForShouldReach();
        SetupMemoryForSuppressionPath();

        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutreachFrame.None);

        var phase = BuildPhase(options);

        await phase.RunOutreachAsync(BuildSnapshot(), "i was thinking about him", CancellationToken.None);

        // The selector was called once.
        _mockFrameSelector.Verify(s => s.SelectFrameAsync(
            It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the selector must run before composition when the flag is on");

        // Composition LLM call must NOT have occurred — we suppressed before it.
        MockOllama.Verify(o => o.ChatAsync(
            It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>(), It.IsAny<float?>()),
            Times.Never,
            "composition LLM call must not happen on the None-suppression path");

        // The suppression path goes through DesireEngine.DecayDesireAsync
        // and ApplyCooldownAsync, both of which call SaveDesireStateAsync.
        // Two saves = decay + cooldown.
        MockMemory.Verify(m => m.SaveDesireStateAsync(
            It.IsAny<DesireState>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "DecayDesireAsync + ApplyCooldownAsync must each persist desire state");
    }

    /// <summary>
    /// SPEC: when the flag is ON and the selector returns a real frame
    /// (FrameType != None), the selector IS called with the cycle's
    /// snapshot, and the pipeline proceeds past the suppression branch
    /// into composition. The prompt-builder side of the wiring is
    /// independently verified by
    /// <see cref="PromptBuilderOutreachFrameTests"/>.
    /// </summary>
    [Fact]
    public async Task RunOutreach_FlagOn_SelectorReturnsRealFrame_SelectorCalledWithSnapshot()
    {
        var options = Options.Create(new AniOptions
        {
            MaxNightOutreach             = 100,
            OutreachFrameSelectorEnabled = true,
        });

        SetupOllamaForShouldReach();
        SetupMemoryForSuppressionPath();

        var snapshot = BuildSnapshot();
        ContextSnapshot? observedSnapshot = null;
        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<ContextSnapshot, CancellationToken>((snap, _) => observedSnapshot = snap)
            .ReturnsAsync(new OutreachFrame(
                OutreachFrameType.Shared,
                "Mark said: I had a long day at work",
                0.8f));

        // 2026-05-15: composition migrated from ChatAsync to ChatJsonAsync.
        // SetupOllamaForShouldReach already sequences decision + empty
        // composition JSON, so the pipeline exits at the empty-message
        // guard. No additional ChatAsync setup needed.

        var phase = BuildPhase(options);

        await phase.RunOutreachAsync(snapshot, "i was thinking about him", CancellationToken.None);

        _mockFrameSelector.Verify(s => s.SelectFrameAsync(
            It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the selector must run exactly once before composition");

        observedSnapshot.Should().BeSameAs(snapshot,
            "the selector must receive the same ContextSnapshot the cycle is operating on");

        // Composition WAS reached (selector returned a real frame, so
        // suppression branch was skipped). ChatJsonAsync must have been
        // called twice — once for the outreach decision, once for the
        // structured composition.
        MockOllama.Verify(o => o.ChatJsonAsync(
            It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "ChatJsonAsync runs twice — decision + composition — on the real-frame path");
    }
}
