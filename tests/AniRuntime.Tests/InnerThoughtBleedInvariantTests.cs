using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5g (May 2, 2026) spec tests for
/// <see cref="InnerThoughtBleedInvariant"/>. Pins the universalised
/// Door C contract: applies to Dispatch sinks for ConversationReply
/// / Outreach / Voice; SUPPRESS verdict → Fail; SEND verdict →
/// Pass; LLM exception → Pass (uncovered, gate failure non-fatal).
/// </summary>
public class InnerThoughtBleedInvariantTests
{
    private readonly Mock<IOllamaClient> _mockOllama = new(MockBehavior.Strict);

    private InnerThoughtBleedInvariant Build() =>
        new(_mockOllama.Object, NullLogger<InnerThoughtBleedInvariant>.Instance);

    private static CognitiveArtifact Artifact(
        string content = "test message",
        CognitiveProducerKind producer = CognitiveProducerKind.Outreach,
        CognitiveOutputSink sink = CognitiveOutputSink.Dispatch,
        string? innerThought = null) => new()
    {
        Content              = content,
        ProducerKind         = producer,
        IntendedSink         = sink,
        ContactName          = Roles.Mark,
        WriterInnerThought   = innerThought,
    };

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Outreach,             CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Voice,                CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary,  CognitiveOutputSink.PersistedSummary, false)]
    [InlineData(CognitiveProducerKind.InnerThought,         CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.WorldExperience,      CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Reflection,           CognitiveOutputSink.PersistedSummary, false)]
    [InlineData(CognitiveProducerKind.Outreach,             CognitiveOutputSink.PersistedMemory,  false)]
    public void AppliesTo_DispatchSinkAndContactFacingProducers(
        CognitiveProducerKind producer, CognitiveOutputSink sink, bool expected)
    {
        Build().AppliesTo(Artifact(producer: producer, sink: sink)).Should().Be(expected);
    }

    // ── Evaluate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_DoorA_Send_Passes()
    {
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{ "door": "A", "verdict": "SEND", "reasoning": "grounded reference" }""");

        var result = await Build().EvaluateAsync(
            Artifact("how did the dentist go?"), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_DoorB_Send_Passes()
    {
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{ "door": "B", "verdict": "SEND", "reasoning": "standalone creative" }""");

        var result = await Build().EvaluateAsync(
            Artifact("what are you doing right now?"), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_DoorC_Suppress_Fails()
    {
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{ "door": "C", "verdict": "SUPPRESS", "reasoning": "only makes sense if reader had inner thought" }""");

        var result = await Build().EvaluateAsync(
            Artifact("the way the light moved across the floorboards just now reminded me of you"),
            CancellationToken.None);
        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("Door C");
        result.RemediationHint.Should().Contain("only makes sense");
    }

    [Fact]
    public async Task Evaluate_LLMThrows_PassesAsUncovered()
    {
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        var result = await Build().EvaluateAsync(Artifact(), CancellationToken.None);
        result.Passed.Should().BeTrue(
            "gate failure must NOT block dispatch — observability bugs in the gate stay non-fatal");
    }

    [Fact]
    public async Task Evaluate_EmptyContent_PassesWithoutCallingLLM()
    {
        var result = await Build().EvaluateAsync(Artifact(content: ""), CancellationToken.None);
        result.Passed.Should().BeTrue();
        _mockOllama.Verify(o => o.ChatJsonAsync(
            It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Evaluate_ContextWithInnerThought_IncludedInPrompt()
    {
        string? capturedUser = null;
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<ChatMessage>, string, CancellationToken>(
                (_, _, user, _) => capturedUser = user)
            .ReturnsAsync("""{ "door": "A", "verdict": "SEND" }""");

        await Build().EvaluateAsync(
            Artifact(innerThought: "i was thinking about old wood and slow sun"),
            CancellationToken.None);

        capturedUser.Should().Contain("old wood and slow sun",
            "outreach has the inner thought; the prompt must surface it for Door C comparison");
    }

    [Fact]
    public async Task Evaluate_NoInnerThought_PromptUsesStandaloneSensibilityForm()
    {
        string? capturedUser = null;
        _mockOllama.Setup(o => o.ChatJsonAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<ChatMessage>, string, CancellationToken>(
                (_, _, user, _) => capturedUser = user)
            .ReturnsAsync("""{ "door": "B", "verdict": "SEND" }""");

        await Build().EvaluateAsync(
            Artifact(producer: CognitiveProducerKind.ConversationReply, innerThought: null),
            CancellationToken.None);

        capturedUser.Should().NotContain("inner thought",
            "without inner thought context, prompt uses the standalone-sensibility form (no inner-thought reference)");
        capturedUser.Should().Contain("standalone reader-perspective",
            "the standalone-sensibility prompt asks about reader-perspective sense");
    }

    // ── ParseCoherenceVerdict ────────────────────────────────────────────

    [Fact]
    public void ParseCoherenceVerdict_ValidJson_ParsesAllFields()
    {
        var v = InnerThoughtBleedInvariant.ParseCoherenceVerdict(
            """{ "door": "C", "verdict": "SUPPRESS", "reasoning": "leaked" }""");
        v.Door.Should().Be("C");
        v.Verdict.Should().Be("SUPPRESS");
        v.Reasoning.Should().Be("leaked");
    }

    [Fact]
    public void ParseCoherenceVerdict_GarbledJson_ContainsSuppress_FailsClosed()
    {
        var v = InnerThoughtBleedInvariant.ParseCoherenceVerdict("oops not json but SUPPRESS appears");
        v.Verdict.Should().Be("SUPPRESS",
            "fail-closed when 'SUPPRESS' is detected in malformed output — safer to suppress than dispatch ambiguous content");
    }

    [Fact]
    public void ParseCoherenceVerdict_GarbledJson_NoSuppressMention_FailsOpen()
    {
        var v = InnerThoughtBleedInvariant.ParseCoherenceVerdict("totally not json");
        v.Verdict.Should().Be("SEND",
            "fail-open on garbled non-SUPPRESS output — defaulting to SEND avoids false positives");
    }
}
