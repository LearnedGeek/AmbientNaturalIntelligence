using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5h-prelude (May 3, 2026) — spec tests for the
/// CognitiveOutputGate evaluation step at the
/// <see cref="InnerThoughtPhase"/> output boundary.
///
/// Pins the contract: every produced inner thought routes through the
/// gate before becoming substrate. Pass → thought continues to valence
/// scoring + reflection + downstream substrate save. Remediate/Fail →
/// thought is dropped (returned as empty), valence + reflection skipped.
/// Gate exception → thought passes through uncovered (gate observability
/// bugs MUST NOT block the cognitive cycle).
///
/// **Why drop, not regen, on inner-thought gate fail.** Inner thoughts
/// are not user-facing; the next cognitive cycle generates fresh.
/// Regenerating costs an extra Ollama call and the regen would face the
/// same substrate / prompt that produced the original suspect output —
/// likely to hit the same invariant. Dropping is cheaper and avoids
/// substrate accumulation of the suspect content. The May 3 10:55
/// "perez" cascade is the canonical case for why substrate accumulation
/// matters: the inner-thought-side fabrication was saved as Interior
/// substrate and lifted into outreach composition. Gating at production
/// catches the laundering at its source.
/// </summary>
public class InnerThoughtPhaseGateTests
{
    private readonly Mock<IOllamaClient>          _mockOllama = new();
    private readonly Mock<ICognitiveOutputGate>   _mockGate   = new(MockBehavior.Strict);

    private const string Thought    = "the bookstore feels heavier today, like rain hasn't decided to fall yet";
    private const string Reflection = "this might be why i keep reaching for the romance shelf — they're certain about endings";
    private const string ValenceJson = "{\"score\": 0.62}";

    private InnerThoughtPhase BuildPhase(ICognitiveOutputGate? gate)
        => new(_mockOllama.Object, NullLogger<InnerThoughtPhase>.Instance, gate);

    private static ContextSnapshot BuildSnapshot(IEnumerable<MemoryRecord>? priorThoughts = null)
        => new()
        {
            CharacterState = new CharacterStateDoc
            {
                Name               = "Ani",
                PrimaryContactName = "Mark",
            },
            DesireState     = new DesireState(),
            EmotionalState  = new EmotionalState(),
            BuiltAt         = DateTimeOffset.UtcNow,
            RelevantMemory  = (priorThoughts ?? Enumerable.Empty<MemoryRecord>()).ToList(),
        };

    private void SetupOllama(string thought, string? reflection = null)
    {
        // First InnerMonologueChatAsync call is the thought; second is the
        // reflection. Discriminate by call order (SetupSequence) — robust to
        // PromptBuilder text changes. InnerMonologueChatAsync has optional
        // `keepAlive` so Moq requires explicit `It.IsAny<string?>()`.
        _mockOllama.SetupSequence(o => o.InnerMonologueChatAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(thought)
            .ReturnsAsync(reflection ?? string.Empty);

        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ValenceJson);
    }

    // ── Flag / dependency no-op ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GateNull_DispatchesThoughtUncovered()
    {
        SetupOllama(Thought, Reflection);
        var phase = BuildPhase(gate: null);

        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Thought.Should().Be(Thought, "no gate → original thought passes through");
        result.Reflection.Should().Be(Reflection);
        result.Valence.Should().BeApproximately(0.62f, 0.01f);
        _mockGate.VerifyNoOtherCalls();
    }

    // ── Gate verdict handling ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GatePass_ThoughtContinuesToReflectionAndValence()
    {
        SetupOllama(Thought, Reflection);
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(OutputGateResult.Pass());

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Thought.Should().Be(Thought);
        result.Reflection.Should().Be(Reflection, "Pass verdict → reflection runs");
        result.Valence.Should().BeApproximately(0.62f, 0.01f, "Pass verdict → valence scoring runs");
        _mockGate.Verify(
            g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_GateRemediate_DropsThoughtAndSkipsDownstream()
    {
        // The May 3 10:55 "perez" failure shape: an inner-thought-side
        // fabrication that would otherwise become Interior-tier substrate.
        // Dropping prevents the laundering at its source.
        SetupOllama(Thought, Reflection);
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict          = OutputGateVerdict.Remediate,
                     FiredInvariants  = new[] { "self-echo" },
                     RemediationHint  = "thought repeats prior inner thought (5-token verbatim run)",
                 });

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Thought.Should().Be(string.Empty, "Remediate verdict → thought dropped");
        result.Reflection.Should().BeNull("dropped thought → no reflection");
        result.Valence.Should().BeApproximately(0.3f, 0.01f, "dropped thought → fallback valence (no Ollama scoring call)");

        _mockOllama.Verify(o => o.ChatJsonAsync(
            It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "valence scoring must NOT run on a dropped thought (saves Ollama cost + avoids scoring nothing)");
    }

    [Fact]
    public async Task RunAsync_GateFail_DropsThoughtAndSkipsDownstream()
    {
        SetupOllama(Thought, Reflection);
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OutputGateResult
                 {
                     Verdict          = OutputGateVerdict.Fail,
                     FiredInvariants  = new[] { "confabulation" },
                     RemediationHint  = "high-confidence confab classifier hit",
                 });

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Thought.Should().Be(string.Empty);
        result.Reflection.Should().BeNull();
    }

    // ── Failure containment ─────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_GateThrows_PassesThoughtUncovered()
    {
        SetupOllama(Thought, Reflection);
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("gate bug"));

        var phase = BuildPhase(gate: _mockGate.Object);
        var result = await phase.RunAsync(BuildSnapshot(), CancellationToken.None);

        result.Thought.Should().Be(Thought, "gate exception must NOT block the cognitive cycle");
        result.Reflection.Should().Be(Reflection);
        result.Valence.Should().BeApproximately(0.62f, 0.01f);
    }

    // ── Artifact construction ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_BuildsArtifactWithCorrectProducerSinkAndPriorThoughts()
    {
        SetupOllama(Thought, Reflection);

        CognitiveArtifact? captured = null;
        _mockGate.Setup(g => g.EvaluateAsync(It.IsAny<CognitiveArtifact>(), It.IsAny<CancellationToken>()))
                 .Callback<CognitiveArtifact, CancellationToken>((a, _) => captured = a)
                 .ReturnsAsync(OutputGateResult.Pass());

        var priorThoughts = new[]
        {
            new MemoryRecord { Type = MemoryType.InnerThought, Content = "earlier thought about coffee" },
            new MemoryRecord { Type = MemoryType.Episodic,     Content = "episodic memory should NOT be passed as PriorAniMessages" },
            new MemoryRecord { Type = MemoryType.InnerThought, Content = "another earlier thought" },
        };

        var phase = BuildPhase(gate: _mockGate.Object);
        await phase.RunAsync(BuildSnapshot(priorThoughts), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ProducerKind.Should().Be(CognitiveProducerKind.InnerThought);
        captured.IntendedSink.Should().Be(CognitiveOutputSink.PersistedMemory);
        captured.Content.Should().Be(Thought);
        captured.ContactName.Should().Be("Mark");
        captured.PriorAniMessages.Should().NotBeNull();
        captured.PriorAniMessages!.Should().Contain("earlier thought about coffee");
        captured.PriorAniMessages.Should().Contain("another earlier thought");
        captured.PriorAniMessages.Should().NotContain(
            "episodic memory should NOT be passed as PriorAniMessages",
            "PriorAniMessages must be filtered to InnerThought-typed memories only");
        captured.SystemPromptText.Should().NotBeNullOrEmpty(
            "system prompt is captured for prompt-template-leak invariant");
    }
}
