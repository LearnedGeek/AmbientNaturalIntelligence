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
/// Theme N Phase N.6 (May 10, 2026) — spec tests for the
/// <see cref="IOutreachFrameSelector"/> wiring inside
/// <see cref="OutreachPhase.TryReactiveShareAsync"/>.
///
/// **Empirical motivation.** May 10 06:38 + 07:23 RSS-triggered shares
/// dispatched messages that bypassed Theme N entirely (selector not
/// invoked, no frame, no anchor in the prompt) because the reactive-share
/// path predates N.3 and ran outside <c>OutreachPhase.RunOutreachAsync</c>.
/// Mark's *copy-paste-to-a-friend test* surfaced the failure: the shares
/// were technically grounded but a substrate-naive third-party reader had
/// no hook for what *"this"* refers to. N.6 fixes this by extending the
/// selector + frame-aware composition to the reactive-share path.
///
/// Four scenarios cover the architectural contract:
/// <list type="number">
///   <item><b>Flag OFF (deployment posture before flag flip):</b> the
///   selector is NOT called, regardless of the share-eligibility gates
///   passing. Zero behavioral change vs. pre-N.6.</item>
///   <item><b>Flag ON, frame=WorldPerception + recent shared topic:</b>
///   composition prompt contains the article anchor, the
///   <c>[FRAME: WorldPerception]</c> header, AND the shared-topic context
///   — anchored to the closed-conversation gist.</item>
///   <item><b>Flag ON, frame=WorldPerception + no shared topic:</b>
///   composition prompt contains the article anchor + clean-external-
///   observation guidance (do NOT pretend to join an in-flight
///   conversation).</item>
///   <item><b>Flag ON, selector returns OutreachFrame.None:</b> the
///   share is suppressed, no LLM composition call, no dispatch, the
///   method returns false.</item>
/// </list>
///
/// Strict-mock policy per Theme K K.0 (see <see cref="AniTestBase"/>).
/// </summary>
public class ReactiveShareFrameSelectorTests : AniTestBase
{
    private readonly Mock<IConversationService>      _mockConversations = new(MockBehavior.Strict);
    private readonly Mock<IOutreachFrameSelector>    _mockFrameSelector = new(MockBehavior.Strict);
    private readonly Mock<IClosedConversationStore>  _mockClosedStore   = new(MockBehavior.Strict);
    private readonly Mock<IAniAction>                _mockSmsAction     = new();

    /// <summary>
    /// Build an OutreachPhase with the dispatcher pre-wired to a captured
    /// SMS action so the dispatch-vs-suppress branch is observable in
    /// tests. Dispatcher key is <c>ActionTypes.Sms</c> (the value the
    /// reactive-share path uses on its <see cref="OutreachDecision"/>).
    /// </summary>
    private OutreachPhase BuildPhase(IOptions<AniOptions> options, bool wireClosedStore = true)
    {
        _mockSmsAction.SetupGet(a => a.ActionType).Returns(ActionTypes.Sms);
        _mockSmsAction.Setup(a => a.ExecuteAsync(It.IsAny<OutreachDecision>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(true);

        var dispatcher = new AniActionDispatcher(
            new[] { _mockSmsAction.Object },
            NullLogger<AniActionDispatcher>.Instance);

        var desire = new DesireEngine(
            MockMemory.Object, MockMemory.Object, options,
            NullLogger<DesireEngine>.Instance);

        var claimVerifier = new ClaimVerificationPhase(
            MockMemory.Object, MockOllama.Object, options,
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
            options,
            mlClassifier: null,
            personaCache: null,
            em9Detector: null,
            outputGate: null,
            vibeBias: null,
            frameSelector: _mockFrameSelector.Object,
            frameChecker: null,
            closedStore: wireClosedStore ? _mockClosedStore.Object : null);
    }

    private static IOptions<AniOptions> BuildOptions(bool selectorEnabled) => Options.Create(new AniOptions
    {
        ReactiveShareThreshold        = 0.5,
        MaxReactiveSharesPerDay       = 10,
        ReactiveShareCooldownMinutes  = 0.0,
        OutreachFrameSelectorEnabled  = selectorEnabled,
        // Pin night-hour options so DesireEngine.IsNightHours() is
        // deterministically false (hour >= 0 && hour < 0 is false for
        // every hour). Without this the test result depends on the
        // runner's local wall clock — between 22:00 and 05:59 local
        // OutreachPhase.TryReactiveShareAsync short-circuits before the
        // selector is invoked. Pre-existing N.6 oversight surfaced by
        // P.1 deploy hitting the self-hosted runner at 05:53 local.
        NightStartHour                = 0,
        NightEndHour                  = 0,
    });

    private static List<PerceptionEvent> BuildHighRelevanceRssPerception(string articleSummary) =>
        new()
        {
            new PerceptionEvent
            {
                SourceName       = "rss",
                Category         = PerceptionCategory.Content,
                Summary          = articleSummary,
                ContactRelevance = 0.85f,
                OccurredAt       = DateTimeOffset.UtcNow,
            },
        };

    private static CharacterStateDoc BuildCharState() => new()
    {
        Name               = "Ani",
        PrimaryContactName = "Mark",
    };

    /// <summary>
    /// Set up the strict memory mock with calls the share path always
    /// invokes (regardless of frame branch):
    ///  - GetEmotionalStateAsync (composition mood coloring; AniTestBase
    ///    already stubs this on the base class).
    ///  - GetDesireStateAsync (cooldown gate).
    /// </summary>
    private void SetupMemoryForShareBaseline()
    {
        // GetDesireStateAsync — long-ago LastOutreach so the cooldown
        // gate passes deterministically.
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesireState
            {
                DesireToConnect  = 0.2f,
                LastOutreach     = DateTimeOffset.UtcNow.AddHours(-12),
                LastInnerThought = DateTimeOffset.UtcNow,
            });
    }

    /// <summary>
    /// Set up the conversation-service strict mock for the dispatch tail
    /// path (RecordOutboundInThreadAsync reads the active thread and
    /// appends a message to it). Used only on the dispatch branch tests.
    /// </summary>
    private void SetupConversationsForDispatchTail()
    {
        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new ConversationThread
                          {
                              Id            = Guid.NewGuid(),
                              InitiatedBy   = Roles.Mark,
                              StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-30),
                              LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                          });
        _mockConversations.Setup(c => c.AddMessageAsync(
                It.IsAny<Guid>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Set up the strict memory mock for the dispatch-tail writes:
    ///  - SaveAsync(MemoryRecord) — the post-share Episodic record.
    ///  - SaveDesireStateAsync — ResetAfterOutreachAsync persists.
    /// </summary>
    private void SetupMemoryForDispatchTail()
    {
        MockMemory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.SaveDesireStateAsync(
                It.IsAny<DesireState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    /// <summary>
    /// SPEC: when <c>OutreachFrameSelectorEnabled</c> is false (the N.6-
    /// default deployment posture), the selector is NEVER invoked even when
    /// every reactive-share gate passes (high-relevance RSS, daytime, under
    /// daily limit, cooldown elapsed). Zero behavioral change vs. pre-N.6.
    /// </summary>
    [Fact]
    public async Task TryReactiveShare_FlagOff_SelectorIsNeverCalled()
    {
        var options = BuildOptions(selectorEnabled: false);
        SetupMemoryForShareBaseline();
        SetupMemoryForDispatchTail();
        SetupConversationsForDispatchTail();

        // Composition runs (flag off = unchanged old behavior); return
        // empty so the share short-circuits at the empty-message guard
        // and we don't have to mock the dispatch path.
        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync(string.Empty);

        var phase = BuildPhase(options);
        var perceptions = BuildHighRelevanceRssPerception("[NPR] some article");

        var result = await phase.TryReactiveShareAsync(perceptions, BuildCharState(), CancellationToken.None);

        result.Should().BeFalse(
            "an empty composition is treated as a non-share (existing pre-N.6 behavior)");

        // The strict mocks would have thrown on any unexpected call. The
        // explicit Times.Never assertion is belt-and-suspenders.
        _mockFrameSelector.Verify(s => s.SelectFrameAsync(
            It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the selector must not be called when OutreachFrameSelectorEnabled=false");
        _mockClosedStore.Verify(s => s.GetRecentAsync(
            It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the closed-conversation lookup must not happen when the flag is off");
    }

    /// <summary>
    /// SPEC: when the flag is ON and the selector returns
    /// <see cref="OutreachFrameType.WorldPerception"/> AND a recent closed-
    /// conversation gist is available, the composition prompt contains the
    /// article anchor, the <c>[FRAME: WorldPerception]</c> header, and the
    /// shared-topic gist as the anchor-to-it instruction. This is the N.6
    /// happy path: a substrate-naive reader can see what the share is
    /// about AND why it ties to the recent conversation.
    /// </summary>
    [Fact]
    public async Task TryReactiveShare_FlagOn_FrameWorldPerception_WithSharedTopic_ComposesWithBothAnchors()
    {
        var options = BuildOptions(selectorEnabled: true);
        SetupMemoryForShareBaseline();
        SetupMemoryForDispatchTail();
        SetupConversationsForDispatchTail();

        const string articleAnchor = "rss: NPR — bookstore architecture in small Wisconsin towns";
        const string sharedGist    = "Mark mentioned wanting to visit a small-town bookstore last week";

        _mockClosedStore.Setup(s => s.GetRecentAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ClosedConversationRecord { Gist = sharedGist, ClosedAt = DateTimeOffset.UtcNow.AddDays(-1) },
            });

        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutreachFrameEnvelope { Frame = new OutreachFrame(
                OutreachFrameType.WorldPerception, articleAnchor, 0.82f) });

        // Capture the user prompt the composition step receives so we
        // can assert the [FRAME:] / [ANCHOR] / shared-topic shape.
        string? observedUserPrompt = null;
        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .Callback<string, IEnumerable<ChatMessage>, string, CancellationToken, float?>(
                (_, _, user, _, _) => observedUserPrompt = user)
            // Returning empty bypasses the rest of the dispatch tail (we're
            // testing the prompt shape, not the send behavior). The
            // dispatch tail is covered separately by OutreachPhaseTests.
            .ReturnsAsync(string.Empty);

        var phase = BuildPhase(options);
        var perceptions = BuildHighRelevanceRssPerception(articleAnchor);

        await phase.TryReactiveShareAsync(perceptions, BuildCharState(), CancellationToken.None);

        observedUserPrompt.Should().NotBeNull(
            "composition must run when the selector returns a real (non-None) frame");
        observedUserPrompt!.Should().Contain("[FRAME: WorldPerception]");
        observedUserPrompt.Should().Contain($"[ANCHOR] {articleAnchor}");
        observedUserPrompt.Should().Contain(sharedGist,
            "the recent shared-topic gist must be surfaced in the WORLD_PERCEPTION composition guidance when one is available");
        observedUserPrompt.Should().Contain("anchor to it",
            "the shared-topic branch of WORLD_PERCEPTION guidance must instruct the model to anchor to the prior topic");
    }

    /// <summary>
    /// SPEC: when the flag is ON and the selector returns
    /// <see cref="OutreachFrameType.WorldPerception"/> but NO recent
    /// closed-conversation gist exists, the composition prompt contains
    /// the article anchor + the clean-external-observation branch of
    /// the WORLD_PERCEPTION guidance. The model is explicitly told to
    /// frame as a clean external observation and NOT pretend to be
    /// joining an in-flight conversation.
    /// </summary>
    [Fact]
    public async Task TryReactiveShare_FlagOn_FrameWorldPerception_NoSharedTopic_ComposesAsCleanExternalObservation()
    {
        var options = BuildOptions(selectorEnabled: true);
        SetupMemoryForShareBaseline();
        SetupMemoryForDispatchTail();
        SetupConversationsForDispatchTail();

        const string articleAnchor = "rss: NPR — Mother's Day cookout host serves every guest";

        // No recent closed conversation — the lookup returns an empty list.
        _mockClosedStore.Setup(s => s.GetRecentAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ClosedConversationRecord>());

        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutreachFrameEnvelope { Frame = new OutreachFrame(
                OutreachFrameType.WorldPerception, articleAnchor, 0.82f) });

        string? observedUserPrompt = null;
        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .Callback<string, IEnumerable<ChatMessage>, string, CancellationToken, float?>(
                (_, _, user, _, _) => observedUserPrompt = user)
            .ReturnsAsync(string.Empty);

        var phase = BuildPhase(options);
        var perceptions = BuildHighRelevanceRssPerception(articleAnchor);

        await phase.TryReactiveShareAsync(perceptions, BuildCharState(), CancellationToken.None);

        observedUserPrompt.Should().NotBeNull();
        observedUserPrompt!.Should().Contain("[FRAME: WorldPerception]");
        observedUserPrompt.Should().Contain($"[ANCHOR] {articleAnchor}");
        observedUserPrompt.Should().Contain("clean external observation",
            "the no-shared-topic branch of WORLD_PERCEPTION guidance must instruct clean-external-observation framing");
        observedUserPrompt.Should().Contain("do NOT pretend",
            "the no-shared-topic branch must explicitly forbid pretending to join an in-flight conversation");
    }

    /// <summary>
    /// SPEC: when the flag is ON and the selector returns
    /// <see cref="OutreachFrame.None"/>, the share is suppressed in
    /// the same shape as the universal substrate-thin path: no
    /// composition LLM call, no dispatch, the method returns false.
    /// This is the rare path for reactive shares (RSS articles are
    /// usually clean substrate) but must be covered.
    /// </summary>
    [Fact]
    public async Task TryReactiveShare_FlagOn_SelectorReturnsNone_SuppressesShareAndReturnsFalse()
    {
        var options = BuildOptions(selectorEnabled: true);
        SetupMemoryForShareBaseline();

        _mockClosedStore.Setup(s => s.GetRecentAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ClosedConversationRecord>());

        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OutreachFrameEnvelope.None);

        var phase = BuildPhase(options);
        var perceptions = BuildHighRelevanceRssPerception("[NPR] some article");

        var result = await phase.TryReactiveShareAsync(perceptions, BuildCharState(), CancellationToken.None);

        result.Should().BeFalse(
            "the suppression path returns false so the cycle continues to subsequent phases");

        // The selector was called once.
        _mockFrameSelector.Verify(s => s.SelectFrameAsync(
            It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // No composition call — suppression must short-circuit before LLM.
        MockOllama.Verify(o => o.ChatAsync(
            It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>(), It.IsAny<float?>()),
            Times.Never,
            "composition must not run on the None-suppression path");

        // No dispatch — the SMS action must not have been invoked.
        _mockSmsAction.Verify(a => a.ExecuteAsync(
            It.IsAny<OutreachDecision>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no dispatch on the suppression path");
    }

    /// <summary>
    /// SPEC: the selector is invoked with a snapshot whose
    /// <see cref="ContextSnapshot.Perceptions"/> mirrors the share-eligibility
    /// list passed into <see cref="OutreachPhase.TryReactiveShareAsync"/>.
    /// This is the architectural contract: the selector reads its
    /// <c>WORLD_PERCEPTION</c> candidates from the same perceptions the
    /// caller has — there's no hidden second source of truth.
    /// </summary>
    [Fact]
    public async Task TryReactiveShare_FlagOn_SelectorReceivesSnapshotWithPerceptions()
    {
        var options = BuildOptions(selectorEnabled: true);
        SetupMemoryForShareBaseline();
        SetupMemoryForDispatchTail();
        SetupConversationsForDispatchTail();

        const string articleAnchor = "rss: NPR — anchor under inspection";

        _mockClosedStore.Setup(s => s.GetRecentAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ClosedConversationRecord>());

        ContextSnapshot? observedSnapshot = null;
        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<ContextSnapshot, CancellationToken>((snap, _) => observedSnapshot = snap)
            .ReturnsAsync(new OutreachFrameEnvelope { Frame = new OutreachFrame(
                OutreachFrameType.WorldPerception, articleAnchor, 0.82f) });

        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync(string.Empty);

        var phase = BuildPhase(options);
        var perceptions = BuildHighRelevanceRssPerception(articleAnchor);

        await phase.TryReactiveShareAsync(perceptions, BuildCharState(), CancellationToken.None);

        observedSnapshot.Should().NotBeNull();
        observedSnapshot!.Perceptions.Should().BeSameAs(perceptions,
            "the selector must see the same perceptions list the share-eligibility check ran against");
        observedSnapshot.CharacterState.Name.Should().Be("Ani",
            "character state is carried into the snapshot for any future selector concerns");
    }

    /// <summary>
    /// SPEC: when the flag is ON but no closed-conversation store is
    /// wired (DI optional path), the selector still runs and the share
    /// proceeds — just without the shared-topic anchor. This pins the
    /// nullable-DI shape of the wiring (the closed store is a
    /// composition-side enrichment, not a hard dependency for the
    /// selector boundary).
    /// </summary>
    [Fact]
    public async Task TryReactiveShare_FlagOn_NoClosedStoreWired_StillSelectsAndComposesWithoutSharedTopic()
    {
        var options = BuildOptions(selectorEnabled: true);
        SetupMemoryForShareBaseline();
        SetupMemoryForDispatchTail();
        SetupConversationsForDispatchTail();

        const string articleAnchor = "rss: NPR — recipe trending";

        _mockFrameSelector.Setup(s => s.SelectFrameAsync(
                It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutreachFrameEnvelope { Frame = new OutreachFrame(
                OutreachFrameType.WorldPerception, articleAnchor, 0.82f) });

        string? observedUserPrompt = null;
        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .Callback<string, IEnumerable<ChatMessage>, string, CancellationToken, float?>(
                (_, _, user, _, _) => observedUserPrompt = user)
            .ReturnsAsync(string.Empty);

        // Build the phase with closedStore intentionally omitted.
        var phase = BuildPhase(options, wireClosedStore: false);
        var perceptions = BuildHighRelevanceRssPerception(articleAnchor);

        await phase.TryReactiveShareAsync(perceptions, BuildCharState(), CancellationToken.None);

        _mockFrameSelector.Verify(s => s.SelectFrameAsync(
            It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the selector boundary does not depend on the closed store being wired");

        observedUserPrompt.Should().NotBeNull();
        observedUserPrompt!.Should().Contain("[FRAME: WorldPerception]");
        observedUserPrompt.Should().Contain("clean external observation",
            "without a closed store, the no-shared-topic branch must apply (defensive default)");
    }
}
