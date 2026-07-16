using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #96 (2026-07-15) — spec tests for
/// <see cref="OllamaToolCallClassifier"/>. Pins:
///
/// <list type="number">
///   <item><c>ParseVerdict</c> maps the JSON verdict shape into
///     <see cref="ToolCallVerdict"/> including the argument-map coercion
///     from JSON primitives to string values (numbers / bools get
///     stringified so the action layer has one shape to consume).</item>
///   <item>Unknown / null tool names — even when the model sets
///     <c>should_call_tool=true</c> — coerce to a no-call verdict so a
///     bogus tool identifier never propagates into the dispatcher.</item>
///   <item>Fence-wrapped JSON is tolerated (```json ... ```) — same
///     discipline as <see cref="OllamaTagIntentClassifier"/>.</item>
///   <item><c>ClassifyAsync</c> fails open to a no-call verdict on any
///     Ollama throw — matches the tag-intent / verifier / routing-
///     classifier fallback shape.</item>
///   <item><c>ClassifyAsync</c> short-circuits to no-call when the tool
///     list is empty — no LLM call, no cost.</item>
/// </list>
/// </summary>
public class OllamaToolCallClassifierTests
{
    private static readonly ToolDescriptor RecallMemory = new(
        Name:            "recall_memory",
        Description:     "Search Ani's memory for prior conversations, events, or facts about Mark.",
        ParameterSchema: new Dictionary<string, string>
        {
            ["query"] = "string — the phrase to search memory for",
            ["tier"]  = "string (optional) — 'facts' | 'episodic' | 'interior'",
        });

    private static readonly IReadOnlyList<ToolDescriptor> OneTool = new[] { RecallMemory };

    // ── ParseVerdict — happy paths ─────────────────────────────────────────

    [Fact]
    public void ParseVerdict_ShouldCallTrueWithKnownTool_ReturnsCallVerdict()
    {
        var raw = """
            {
              "should_call_tool": true,
              "tool_name": "recall_memory",
              "arguments": { "query": "Peru trip" },
              "confidence": 0.92,
              "reason": "explicit memory request"
            }
            """;

        var v = OllamaToolCallClassifier.ParseVerdict(raw, OneTool);

        v.ShouldCallTool.Should().BeTrue();
        v.ToolName.Should().Be("recall_memory");
        v.Arguments.Should().NotBeNull();
        v.Arguments!["query"].Should().Be("Peru trip");
        v.Confidence.Should().BeApproximately(0.92f, precision: 0.001f);
        v.Reason.Should().Be("explicit memory request");
    }

    [Fact]
    public void ParseVerdict_ShouldCallFalse_ReturnsNoCallVerdict()
    {
        var raw = """
            {
              "should_call_tool": false,
              "tool_name": null,
              "arguments": null,
              "confidence": 0.88,
              "reason": "greeting, no lookup needed"
            }
            """;

        var v = OllamaToolCallClassifier.ParseVerdict(raw, OneTool);

        v.ShouldCallTool.Should().BeFalse();
        v.ToolName.Should().BeNull();
        v.Arguments.Should().BeNull();
        v.Confidence.Should().BeApproximately(0.88f, precision: 0.001f);
    }

    [Fact]
    public void ParseVerdict_TolerantOfJsonFences()
    {
        var raw = "```json\n{\"should_call_tool\":true,\"tool_name\":\"recall_memory\",\"arguments\":{\"query\":\"x\"},\"confidence\":0.7,\"reason\":\"fenced\"}\n```";
        var v = OllamaToolCallClassifier.ParseVerdict(raw, OneTool);
        v.ShouldCallTool.Should().BeTrue();
        v.ToolName.Should().Be("recall_memory");
    }

    [Fact]
    public void ParseVerdict_ClampsConfidenceBelowZero()
    {
        var v = OllamaToolCallClassifier.ParseVerdict(
            "{\"should_call_tool\":false,\"confidence\":-0.3,\"reason\":\"x\"}", OneTool);
        v.Confidence.Should().Be(0f);
    }

    [Fact]
    public void ParseVerdict_ClampsConfidenceAboveOne()
    {
        var v = OllamaToolCallClassifier.ParseVerdict(
            "{\"should_call_tool\":false,\"confidence\":2.5,\"reason\":\"x\"}", OneTool);
        v.Confidence.Should().Be(1f);
    }

    // ── Argument coercion ──────────────────────────────────────────────────

    [Fact]
    public void ParseVerdict_NumericArguments_StringifiedForActionLayer()
    {
        var raw = """
            {
              "should_call_tool": true,
              "tool_name": "recall_memory",
              "arguments": { "limit": 5, "threshold": 0.75 },
              "confidence": 0.9,
              "reason": "numeric args"
            }
            """;

        var v = OllamaToolCallClassifier.ParseVerdict(raw, OneTool);

        v.ShouldCallTool.Should().BeTrue();
        v.Arguments.Should().NotBeNull();
        v.Arguments!["limit"].Should().Be("5");
        v.Arguments["threshold"].Should().Be("0.75");
    }

    [Fact]
    public void ParseVerdict_BooleanArguments_StringifiedTrueFalse()
    {
        var raw = """
            {
              "should_call_tool": true,
              "tool_name": "recall_memory",
              "arguments": { "include_interior": true, "include_facts": false },
              "confidence": 0.9,
              "reason": "bool args"
            }
            """;

        var v = OllamaToolCallClassifier.ParseVerdict(raw, OneTool);

        v.Arguments.Should().NotBeNull();
        v.Arguments!["include_interior"].Should().Be("true");
        v.Arguments["include_facts"].Should().Be("false");
    }

    // ── Guard rails: unknown / missing tool name coerces to no-call ────────

    [Fact]
    public void ParseVerdict_ShouldCallTrueButToolNameMissing_CoercesToNoCall()
    {
        var raw = """
            {
              "should_call_tool": true,
              "tool_name": null,
              "arguments": { "query": "x" },
              "confidence": 0.9,
              "reason": "missing tool"
            }
            """;

        var v = OllamaToolCallClassifier.ParseVerdict(raw, OneTool);

        v.ShouldCallTool.Should().BeFalse();
        v.ToolName.Should().BeNull();
        v.Reason.Should().Contain("tool_name missing");
    }

    [Fact]
    public void ParseVerdict_ShouldCallTrueButUnknownTool_CoercesToNoCall()
    {
        var raw = """
            {
              "should_call_tool": true,
              "tool_name": "nonexistent_tool",
              "arguments": { "x": "y" },
              "confidence": 0.9,
              "reason": "unknown tool"
            }
            """;

        var v = OllamaToolCallClassifier.ParseVerdict(raw, OneTool);

        v.ShouldCallTool.Should().BeFalse();
        v.ToolName.Should().BeNull();
        v.Reason.Should().Contain("unknown tool_name");
    }

    // ── ParseVerdict — malformed inputs return no-call ─────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{invalid json}")]
    public void ParseVerdict_UnparseableInput_ReturnsNoCall(string raw)
    {
        var v = OllamaToolCallClassifier.ParseVerdict(raw, OneTool);
        v.ShouldCallTool.Should().BeFalse();
        v.ToolName.Should().BeNull();
    }

    // ── ClassifyAsync — end-to-end with mocked Ollama ──────────────────────

    private static OllamaToolCallClassifier Build(Mock<IOllamaClient> ollama)
    {
        var opts = Options.Create(new AniOptions { LocalVerifierModelTag = "qwen3:14b" });
        return new OllamaToolCallClassifier(
            ollama.Object, opts,
            NullLogger<OllamaToolCallClassifier>.Instance);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyToolList_ShortCircuitsWithoutLlmCall()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        // Strict mock will fail if any method is called — proves the
        // short-circuit fires before touching the LLM.
        var classifier = Build(ollama);

        var v = await classifier.ClassifyAsync(
            userMessage:         "what did we talk about yesterday",
            availableTools:      Array.Empty<ToolDescriptor>(),
            conversationContext: "",
            ct:                  CancellationToken.None);

        v.ShouldCallTool.Should().BeFalse();
        v.Reason.Should().Contain("no tools available");
    }

    [Fact]
    public async Task ClassifyAsync_HappyPath_ReturnsParsedVerdict()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatJsonWithModelAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>()))
            .ReturnsAsync("{\"should_call_tool\":true,\"tool_name\":\"recall_memory\",\"arguments\":{\"query\":\"Peru\"},\"confidence\":0.94,\"reason\":\"explicit ask\"}");

        var classifier = Build(ollama);
        var v = await classifier.ClassifyAsync(
            userMessage:         "do you remember when we went to Peru?",
            availableTools:      OneTool,
            conversationContext: "",
            ct:                  CancellationToken.None);

        v.ShouldCallTool.Should().BeTrue();
        v.ToolName.Should().Be("recall_memory");
        v.Arguments.Should().NotBeNull();
        v.Arguments!["query"].Should().Be("Peru");
        v.Confidence.Should().BeApproximately(0.94f, precision: 0.001f);
    }

    [Fact]
    public async Task ClassifyAsync_OllamaThrows_FailsOpenToNoCall()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatJsonWithModelAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("ollama unreachable"));

        var classifier = Build(ollama);
        var v = await classifier.ClassifyAsync(
            userMessage:         "what did we talk about yesterday",
            availableTools:      OneTool,
            conversationContext: "",
            ct:                  CancellationToken.None);

        v.ShouldCallTool.Should().BeFalse();
        v.Confidence.Should().Be(0f);
        v.Reason.Should().Contain("failed");
    }

    [Fact]
    public async Task ClassifyAsync_EmptyModelTag_FallsBackToQwenDefault()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        string? capturedModel = null;
        ollama.Setup(o => o.ChatJsonWithModelAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>()))
            .Callback<string, string, IEnumerable<ChatMessage>, string, CancellationToken, string?>(
                (m, _, _, _, _, _) => capturedModel = m)
            .ReturnsAsync("{\"should_call_tool\":false,\"confidence\":0.5,\"reason\":\"n/a\"}");

        var opts = Options.Create(new AniOptions { LocalVerifierModelTag = "" });
        var classifier = new OllamaToolCallClassifier(
            ollama.Object, opts,
            NullLogger<OllamaToolCallClassifier>.Instance);

        await classifier.ClassifyAsync("test", OneTool, "", CancellationToken.None);

        capturedModel.Should().Be("qwen3:14b");
    }

    // ── Prompt shape — regressions on the schema ───────────────────────────

    [Fact]
    public void BuildSystemPrompt_RendersAllToolNamesAndDescriptions()
    {
        var s = OllamaToolCallClassifier.BuildSystemPrompt(OneTool);
        s.Should().Contain("recall_memory");
        s.Should().Contain("Search Ani's memory");
        s.Should().Contain("query");
        s.Should().Contain("tier");
    }

    [Fact]
    public void BuildSystemPrompt_RequiresJsonOnlyReply()
    {
        var s = OllamaToolCallClassifier.BuildSystemPrompt(OneTool);
        s.Should().Contain("Reply ONLY");
        s.Should().Contain("should_call_tool");
        s.Should().Contain("tool_name");
        s.Should().Contain("arguments");
        s.Should().Contain("confidence");
    }

    [Fact]
    public void BuildSystemPrompt_ToolWithNoParameters_RendersPlaceholder()
    {
        var noParamTool = new ToolDescriptor(
            Name: "ping",
            Description: "Check if the runtime is alive.",
            ParameterSchema: new Dictionary<string, string>());
        var s = OllamaToolCallClassifier.BuildSystemPrompt(new[] { noParamTool });
        s.Should().Contain("ping");
        s.Should().Contain("(none)");
    }

    [Fact]
    public void BuildUserPrompt_IncludesUserMessageAndContext()
    {
        var p = OllamaToolCallClassifier.BuildUserPrompt(
            userMessage:         "do you remember Peru",
            conversationContext: "Mark: we should plan a trip");
        p.Should().Contain("[USER MESSAGE]");
        p.Should().Contain("do you remember Peru");
        p.Should().Contain("[RECENT CONVERSATION CONTEXT]");
        p.Should().Contain("Mark: we should plan a trip");
    }

    [Fact]
    public void BuildUserPrompt_EmptyContext_ShowsPlaceholder()
    {
        var p = OllamaToolCallClassifier.BuildUserPrompt("hi", conversationContext: "");
        p.Should().Contain("(no context)");
    }
}
