using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5a (Apr 30, 2026) — spec tests for the
/// CognitiveOutputGate evaluation step at the
/// <see cref="ConversationReplyPhase"/> dispatch boundary.
///
/// Targets the internal helper
/// <c>EvaluateAndRemediateReplyAsync</c> directly via
/// <c>InternalsVisibleTo</c>. Tests pin the Pass / Remediate / Fail
/// verdict handling, the flag-off no-op, the gate-not-registered
/// no-op (pre-J.4 deployment paths), and gate exception containment.
///
/// **Validation criterion** (per the J.a decision document): Mark's
/// 1-week post-deploy *"I can talk to her without parsing"* report.
/// These tests pin the contract; production observation is the
/// load-bearing validation.
/// </summary>
public class ConversationReplyPhaseGateTests : AniTestBase
{
    private readonly Mock<IConversationService>     _mockConversations  = new(MockBehavior.Loose);
    private readonly Mock<IReplyChannelResolver>    _mockChannels       = new(MockBehavior.Loose);
    private readonly Mock<ICognitiveOutputGate>     _mockGate           = new(MockBehavior.Strict);
    private readonly Mock<IIntentExtractor>         _mockIntent         = new(MockBehavior.Loose);

    private const string OriginalReply = "okay baby — i'll let you sit with that for a minute. tell me when you're ready.";
    private const string Regenerated   = "got it. i'm here when you want to talk it through.";

    private ConversationReplyPhase BuildPhase(
        IOptions<AniOptions>? options = null,
        ICognitiveOutputGate? gate = null)
    {
        options ??= DefaultOptions;
        var dispatcher = new AniActionDispatcher(
            Array.Empty<IAniAction>(), NullLogger<AniActionDispatcher>.Instance);
        var desire = new DesireEngine(
            MockMemory.Object, MockMemory.Object, options,
            NullLogger<DesireEngine>.Instance);
        var emotional = new EmotionalProcessor(
            MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, options,
            NullLogger<EmotionalProcessor>.Instance);
        var diagnostic = new Mock<IDiagnosticService>();
        diagnostic.Setup(d => d.RunDiagnosticAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new DiagnosticReport());
        var contextBuilder = new ContextBuilder(
            MockMemory.Object, MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, desire, diagnostic.Object, options,
            NullLogger<ContextBuilder>.Instance);
        var keywordExtractor = new KeywordExtractor(
            MockMemory.Object, NullLogger<KeywordExtractor>.Instance);
        var gateState = new ConversationGateState();
        var compressor = new ContextCompressor(
            MockOllama.Object, NullLogger<ContextCompressor>.Instance);
        var claimVerifier = new ClaimVerificationPhase(
            MockMemory.Object, MockOllama.Object, options,
            NullLogger<ClaimVerificationPhase>.Instance);

        return new ConversationReplyPhase(
            MockMemory.Object, MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, _mockConversations.Object,
            _mockChannels.Object, dispatcher, desire, emotional, contextBuilder, keywordExtractor,
            _mockIntent.Object, gateState, compressor, claimVerifier, new WithdrawalStateTracker(), options, DefaultOllamaOptions,
            NullLogger<ConversationReplyPhase>.Instance,
            outputGate: gate);
    }

    private static ConversationThread BuildThread(string markText = "i had a hard day")
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        return new ConversationThread
        {
            Id            = Guid.NewGuid(),
            StartedAt     = t0,
            LastMessageAt = t0.AddMinutes(2),
            InitiatedBy   = Roles.Mark,
            Messages = new()
            {
                new() { Role = Roles.Mark, Content = markText, SentAt = t0 },
            },
        };
    }

    private static ContextSnapshot BuildSnapshot() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name = "Ani",
            PrimaryContactName = "Mark",
        },
        DesireState    = new DesireState(),
        EmotionalState = new EmotionalState(),
        BuiltAt        = DateTimeOffset.UtcNow,
    };

    private static ConversationMessage NewReplyMessage()
        => new() { Role = Roles.Ani, Content = OriginalReply, SentAt = DateTimeOffset.UtcNow };

    private static (string System, string User) Prompt()
        => ("system text", "user prompt text");

    // ── Flag / dependency no-op paths ───────────────────────────────────

    [Fact]
    public async Task EvaluateAndRemediate_GateNull_ReturnsOriginalReply()
    {
        // Pre-J.4 deployments + most existing tests — gate not wired.
        var phase = BuildPhase(gate: null);

        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(OriginalReply);
        _mockGate.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EvaluateAndRemediate_FlagOff_ReturnsOriginalReply_WithoutCallingGate()
    {
        var optsOff = Options.Create(new AniOptions
        {
            ConversationReplyOutputGateEnabled = false,
            // copy a couple of defaults the phase touches indirectly
            MaxNightOutreach = 100,
        });
        var phase = BuildPhase(options: optsOff, gate: _mockGate.Object);

        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(OriginalReply);
        _mockGate.Verify(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Verdict handling ────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAndRemediate_GatePass_ReturnsOriginalReply()
    {
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(OutputGateResult.Pass());

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(OriginalReply);
        MockOllama.Verify(o => o.ChatAsync(
            It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()),
            Times.Never, "Pass verdict must NOT trigger regeneration");
    }

    [Fact]
    public async Task EvaluateAndRemediate_GateRemediate_RegenPassesReEval_ReturnsRegenerated()
    {
        // Happy path under the May 3 re-evaluation contract: original fails,
        // regen passes the gate on re-eval, dispatch the regen.
        _mockGate.SetupSequence(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict          = OutputGateVerdict.Remediate,
                     FiredInvariants  = new[] { "anti-parrot" },
                     RemediationHint  = "output contains 8-token verbatim lift from contact message",
                 })
                 .ReturnsAsync(OutputGateResult.Pass());

        string? capturedUser = null;
        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .Callback<string, IEnumerable<ChatMessage>, string, CancellationToken, float?>(
                (_, _, user, _, _) => capturedUser = user)
            .ReturnsAsync(Regenerated);

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(Regenerated);
        capturedUser.Should().Contain("anti-parrot",
            "remediation prompt must surface the failed invariant name to the regen LLM");
        capturedUser.Should().Contain("verbatim lift",
            "remediation prompt must surface the gate's hint");
        capturedUser.Should().Contain("Original prompt",
            "remediation prompt must include the original prompt context");
        capturedUser.Should().Contain("Do NOT acknowledge",
            "remediation prompt instructs the model not to surface the gate to Mark");
        _mockGate.Verify(
            g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "May 3 J.5a re-eval contract: gate evaluated once on original + once on regen");
    }

    [Fact]
    public async Task EvaluateAndRemediate_GateRemediate_RegenAlsoFails_ReturnsSafeAcknowledgement()
    {
        // The May 3 10:55 Failure C canonical case: J.5a remediation regen
        // returned a byte-identical copy of the prior assistant turn from
        // chat history; both went out via Twilio ~57 seconds apart because
        // the regen never re-ran the gate. The May 3 re-eval contract
        // re-evaluates the regen — when the regen ALSO fails, dispatch
        // the safe acknowledgement instead of a known-bad regen.
        _mockGate.SetupSequence(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict          = OutputGateVerdict.Remediate,
                     FiredInvariants  = new[] { "inner-thought-bleed" },
                     RemediationHint  = "phone-that-never-rings — inner thought leakage",
                 })
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict          = OutputGateVerdict.Remediate,
                     FiredInvariants  = new[] { "self-echo" },
                     RemediationHint  = "regen panic-reverted to prior assistant turn (byte-identical)",
                 });

        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync(Regenerated);

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(ConversationReplyPhase.SafeAcknowledgement,
            "regen that ALSO fails the gate must NOT dispatch — fall back to safe acknowledgement");
        result.Should().NotBe(Regenerated,
            "May 3 Failure C contract: a known-bad regen must never reach the wire");
    }

    [Fact]
    public async Task EvaluateAndRemediate_GateRemediate_RegenReEvalThrows_ReturnsSafeAcknowledgement()
    {
        // Re-eval exception MUST NOT cause us to dispatch the regen uncovered.
        // The original was already known-bad; the regen is unverified after
        // the re-eval throws; falling back to safe acknowledgement is safer
        // than dispatching either.
        _mockGate.SetupSequence(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict          = OutputGateVerdict.Remediate,
                     FiredInvariants  = new[] { "anti-parrot" },
                     RemediationHint  = "hint",
                 })
                 .ThrowsAsync(new InvalidOperationException("re-eval gate bug"));

        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync(Regenerated);

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(ConversationReplyPhase.SafeAcknowledgement);
    }

    [Fact]
    public async Task EvaluateAndRemediate_GateRemediate_RegenerationEmpty_FallsBackToOriginal()
    {
        // If the regenerated reply is empty/whitespace, the gate's hint
        // is treated as a "could be better" signal, not a hard block.
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict = OutputGateVerdict.Remediate,
                     FiredInvariants = new[] { "anti-parrot" },
                     RemediationHint = "hint",
                 });
        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ReturnsAsync("   \n  ");  // whitespace-only

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(OriginalReply);
    }

    [Fact]
    public async Task EvaluateAndRemediate_GateRemediate_RegenerationThrows_FallsBackToOriginal()
    {
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict = OutputGateVerdict.Remediate,
                     FiredInvariants = new[] { "anti-parrot" },
                     RemediationHint = "hint",
                 });
        MockOllama.Setup(o => o.ChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()))
            .ThrowsAsync(new HttpRequestException("ollama unreachable"));

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(OriginalReply,
            "regen LLM failure must NOT block the conversation — original dispatches uncovered.");
    }

    [Fact]
    public async Task EvaluateAndRemediate_GateFail_DropsReplyForSafeAcknowledgement()
    {
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict = OutputGateVerdict.Fail,
                     FiredInvariants = new[] { "confabulation" },
                     RemediationHint = "high-confidence confabulation classifier hit",
                 });

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().NotBe(OriginalReply, "hard fail must drop the original reply");
        result.Should().NotBeNullOrWhiteSpace("substituted reply must be non-empty so the conversation isn't silent");
        MockOllama.Verify(o => o.ChatAsync(
            It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<float?>()),
            Times.Never, "hard fail must NOT regenerate — regen would likely hit the same issue.");
    }

    // ── Failure containment ─────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAndRemediate_GateThrows_DispatchesOriginalReplyUncovered()
    {
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("gate bug"));

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, BuildThread(), BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        result.Should().Be(OriginalReply,
            "gate exception must NOT block the conversation pipeline — observability bugs in gate stay non-fatal");
    }

    // ── Artifact construction ────────────────────────────────────────────

    [Fact]
    public async Task EvaluateAndRemediate_BuildsArtifactWithCorrectProducerAndSink()
    {
        CognitiveArtifact? captured = null;
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .Callback<CognitiveArtifact, CancellationToken>((a, _) => captured = a)
                 .ReturnsAsync(OutputGateResult.Pass());

        var thread = BuildThread("hey i'm having a rough day");
        thread.Messages.Add(new ConversationMessage
        {
            Role = Roles.Ani, Content = "earlier ani reply", SentAt = DateTimeOffset.UtcNow.AddMinutes(-3),
        });
        thread.Messages.Add(new ConversationMessage
        {
            Role = Roles.Mark, Content = "i miss you", SentAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var phase = BuildPhase(gate: _mockGate.Object);
        await phase.EvaluateAndRemediateReplyAsync(
            OriginalReply, thread, BuildSnapshot(), NewReplyMessage(),
            Prompt(), 0.7f, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ProducerKind.Should().Be(CognitiveProducerKind.ConversationReply);
        captured.IntendedSink.Should().Be(CognitiveOutputSink.Dispatch);
        captured.Content.Should().Be(OriginalReply);
        captured.ContactName.Should().Be("Mark");
        captured.ContactRecentMessages.Should().NotBeNull();
        captured.ContactRecentMessages!.Should().Contain("hey i'm having a rough day").And.Contain("i miss you");
        captured.PriorAniMessages.Should().NotBeNull();
        captured.PriorAniMessages!.Should().Contain("earlier ani reply");
    }
}
