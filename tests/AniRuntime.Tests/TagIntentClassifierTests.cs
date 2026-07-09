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
/// Issue #93 Phase 2 (2026-07-06) — spec tests for
/// <see cref="OllamaTagIntentClassifier"/>. Pins:
///
/// <list type="number">
///   <item>The <c>ParseVerdict</c> helper handles the four canonical intent
///     labels + malformed input, and normalises confidence to [0,1].</item>
///   <item><c>ClassifyAsync</c> fails open to
///     <see cref="TagIntent.Unknown"/> on any Ollama throw — matching the
///     verifier / routing-classifier fallback shape.</item>
///   <item>Fence-wrapped JSON is tolerated (```json ... ```) — Ollama
///     format=json still occasionally emits fences.</item>
/// </list>
/// </summary>
public class TagIntentClassifierTests
{
    // ── ParseVerdict — happy paths ─────────────────────────────────────────

    [Theory]
    [InlineData("confirm",    TagIntent.Confirm)]
    [InlineData("Confirm",    TagIntent.Confirm)]
    [InlineData("CONFIRM",    TagIntent.Confirm)]
    [InlineData("invalidate", TagIntent.Invalidate)]
    [InlineData("clarify",    TagIntent.Clarify)]
    [InlineData("unrelated",  TagIntent.Unrelated)]
    public void ParseVerdict_MapsCanonicalIntentLabels(string label, TagIntent expected)
    {
        var raw = $"{{\"intent\":\"{label}\",\"confidence\":0.9,\"reason\":\"test\"}}";
        var v = OllamaTagIntentClassifier.ParseVerdict(raw);
        v.Intent.Should().Be(expected);
        v.Confidence.Should().BeApproximately(0.9f, precision: 0.001f);
        v.Reason.Should().Be("test");
    }

    [Fact]
    public void ParseVerdict_ClampsConfidenceBelowZero()
    {
        var v = OllamaTagIntentClassifier.ParseVerdict(
            "{\"intent\":\"confirm\",\"confidence\":-0.3,\"reason\":\"x\"}");
        v.Confidence.Should().Be(0f);
    }

    [Fact]
    public void ParseVerdict_ClampsConfidenceAboveOne()
    {
        var v = OllamaTagIntentClassifier.ParseVerdict(
            "{\"intent\":\"invalidate\",\"confidence\":1.7,\"reason\":\"x\"}");
        v.Confidence.Should().Be(1f);
    }

    [Fact]
    public void ParseVerdict_TolerantOfJsonFences()
    {
        var raw = "```json\n{\"intent\":\"confirm\",\"confidence\":0.85,\"reason\":\"fenced\"}\n```";
        var v = OllamaTagIntentClassifier.ParseVerdict(raw);
        v.Intent.Should().Be(TagIntent.Confirm);
        v.Confidence.Should().BeApproximately(0.85f, precision: 0.001f);
    }

    // ── ParseVerdict — malformed inputs return Unknown ─────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{invalid json}")]
    public void ParseVerdict_UnparseableInput_ReturnsUnknown(string raw)
    {
        var v = OllamaTagIntentClassifier.ParseVerdict(raw);
        v.Intent.Should().Be(TagIntent.Unknown);
    }

    [Fact]
    public void ParseVerdict_ValidJsonButUnknownIntent_ReturnsUnknown()
    {
        var v = OllamaTagIntentClassifier.ParseVerdict(
            "{\"intent\":\"nonsense\",\"confidence\":0.9,\"reason\":\"x\"}");
        v.Intent.Should().Be(TagIntent.Unknown);
    }

    [Fact]
    public void ParseVerdict_ValidJsonMissingConfidence_DefaultsToZero()
    {
        var v = OllamaTagIntentClassifier.ParseVerdict(
            "{\"intent\":\"confirm\",\"reason\":\"missing conf\"}");
        v.Intent.Should().Be(TagIntent.Confirm);
        v.Confidence.Should().Be(0f);
    }

    // ── ClassifyAsync — end-to-end with mocked Ollama ──────────────────────

    private static OllamaTagIntentClassifier Build(Mock<IOllamaClient> ollama)
    {
        var opts = Options.Create(new AniOptions { LocalVerifierModelTag = "qwen3:14b" });
        return new OllamaTagIntentClassifier(
            ollama.Object, opts,
            NullLogger<OllamaTagIntentClassifier>.Instance);
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
            .ReturnsAsync("{\"intent\":\"invalidate\",\"confidence\":0.95,\"reason\":\"walk-back\"}");

        var classifier = Build(ollama);
        var v = await classifier.ClassifyAsync(
            tagNote: "confab",
            aniReply: "and then we went hiking last weekend",
            markPriorMessage: "how was your day",
            ct: CancellationToken.None);

        v.Intent.Should().Be(TagIntent.Invalidate);
        v.Confidence.Should().BeApproximately(0.95f, precision: 0.001f);
        v.Reason.Should().Be("walk-back");
    }

    [Fact]
    public async Task ClassifyAsync_OllamaThrows_FailsOpenToUnknown()
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
            tagNote: "yes",
            aniReply: "the coffee shop was closed today",
            markPriorMessage: "did they open",
            ct: CancellationToken.None);

        v.Intent.Should().Be(TagIntent.Unknown);
        v.Confidence.Should().Be(0f);
        v.Reason.Should().Contain("failed");
    }

    [Fact]
    public async Task ClassifyAsync_PassesConfiguredModelTag()
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
            .ReturnsAsync("{\"intent\":\"clarify\",\"confidence\":0.7,\"reason\":\"why\"}");

        var opts = Options.Create(new AniOptions { LocalVerifierModelTag = "custom-model:latest" });
        var classifier = new OllamaTagIntentClassifier(
            ollama.Object, opts,
            NullLogger<OllamaTagIntentClassifier>.Instance);

        await classifier.ClassifyAsync("why?", "and then...", "", CancellationToken.None);

        capturedModel.Should().Be("custom-model:latest");
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
            .ReturnsAsync("{\"intent\":\"unrelated\",\"confidence\":0.5,\"reason\":\"off\"}");

        var opts = Options.Create(new AniOptions { LocalVerifierModelTag = "" });
        var classifier = new OllamaTagIntentClassifier(
            ollama.Object, opts,
            NullLogger<OllamaTagIntentClassifier>.Instance);

        await classifier.ClassifyAsync("test", "reply", "prior", CancellationToken.None);

        capturedModel.Should().Be("qwen3:14b");
    }

    // ── Prompt shape — quick sanity checks (regressions on the schema) ─────

    [Fact]
    public void BuildSystemPrompt_ContainsAllFourIntentLabels()
    {
        var s = OllamaTagIntentClassifier.BuildSystemPrompt();
        s.Should().Contain("\"confirm\"");
        s.Should().Contain("\"invalidate\"");
        s.Should().Contain("\"clarify\"");
        s.Should().Contain("\"unrelated\"");
    }

    [Fact]
    public void BuildSystemPrompt_RequiresJsonOnlyReply()
    {
        var s = OllamaTagIntentClassifier.BuildSystemPrompt();
        s.Should().Contain("Reply ONLY");
    }

    [Fact]
    public void BuildUserPrompt_IncludesAllThreeInputBlocks()
    {
        var p = OllamaTagIntentClassifier.BuildUserPrompt(
            tagNote: "confab",
            aniReply: "and then we hiked",
            markPriorMessage: "how was your day");

        p.Should().Contain("[MARK'S TAG NOTE]");
        p.Should().Contain("[ANI REPLY THE TAG ANCHORS AGAINST]");
        p.Should().Contain("[MARK'S PRIOR MESSAGE");
        p.Should().Contain("confab");
        p.Should().Contain("and then we hiked");
        p.Should().Contain("how was your day");
    }

    [Fact]
    public void BuildUserPrompt_EmptyPriorMessage_ShowsPlaceholder()
    {
        var p = OllamaTagIntentClassifier.BuildUserPrompt("tag", "reply", markPriorMessage: "");
        p.Should().Contain("(no prior message)");
    }
}
