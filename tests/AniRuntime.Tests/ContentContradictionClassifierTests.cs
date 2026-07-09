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
/// Issue #93 Phase 3 (2026-07-06) — spec tests for
/// <see cref="OllamaContentContradictionClassifier"/>. Same discipline as
/// <c>TagIntentClassifierTests</c>: pin ParseVerdict (canonical labels +
/// malformed input), pin ClassifyAsync fail-open on Ollama throw, pin the
/// prompt shape so future refactors surface here rather than at runtime.
/// </summary>
public class ContentContradictionClassifierTests
{
    // ── ParseVerdict — canonical outcome labels ────────────────────────────

    [Theory]
    [InlineData("contradicts", ContradictionOutcome.Contradicts)]
    [InlineData("Contradicts", ContradictionOutcome.Contradicts)]
    [InlineData("CONTRADICTS", ContradictionOutcome.Contradicts)]
    [InlineData("grounded",    ContradictionOutcome.Grounded)]
    [InlineData("neutral",     ContradictionOutcome.Neutral)]
    public void ParseVerdict_MapsCanonicalOutcomeLabels(string label, ContradictionOutcome expected)
    {
        var raw = $"{{\"outcome\":\"{label}\",\"confidence\":0.9,\"quote\":null,\"reason\":\"test\"}}";
        var v = OllamaContentContradictionClassifier.ParseVerdict(raw);
        v.Outcome.Should().Be(expected);
        v.Confidence.Should().BeApproximately(0.9f, precision: 0.001f);
    }

    [Fact]
    public void ParseVerdict_ContradictsCarriesQuote()
    {
        var raw = "{\"outcome\":\"contradicts\",\"confidence\":0.95," +
                  "\"quote\":\"Ani is a bookstore clerk in a small Wisconsin town\"," +
                  "\"reason\":\"interior claims Chicago residency\"}";
        var v = OllamaContentContradictionClassifier.ParseVerdict(raw);
        v.Outcome.Should().Be(ContradictionOutcome.Contradicts);
        v.Quote.Should().Be("Ani is a bookstore clerk in a small Wisconsin town");
        v.Reason.Should().Contain("Chicago");
    }

    [Fact]
    public void ParseVerdict_ClampsConfidenceBelowZero()
    {
        var v = OllamaContentContradictionClassifier.ParseVerdict(
            "{\"outcome\":\"grounded\",\"confidence\":-0.5,\"quote\":null,\"reason\":\"x\"}");
        v.Confidence.Should().Be(0f);
    }

    [Fact]
    public void ParseVerdict_ClampsConfidenceAboveOne()
    {
        var v = OllamaContentContradictionClassifier.ParseVerdict(
            "{\"outcome\":\"contradicts\",\"confidence\":1.5,\"quote\":\"q\",\"reason\":\"x\"}");
        v.Confidence.Should().Be(1f);
    }

    [Fact]
    public void ParseVerdict_TolerantOfJsonFences()
    {
        var raw = "```json\n{\"outcome\":\"neutral\",\"confidence\":0.85,\"quote\":null,\"reason\":\"fenced\"}\n```";
        var v = OllamaContentContradictionClassifier.ParseVerdict(raw);
        v.Outcome.Should().Be(ContradictionOutcome.Neutral);
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
        OllamaContentContradictionClassifier.ParseVerdict(raw)
            .Outcome.Should().Be(ContradictionOutcome.Unknown);
    }

    [Fact]
    public void ParseVerdict_ValidJsonButUnknownOutcome_ReturnsUnknown()
    {
        OllamaContentContradictionClassifier.ParseVerdict(
            "{\"outcome\":\"maybe\",\"confidence\":0.9,\"quote\":null,\"reason\":\"x\"}")
            .Outcome.Should().Be(ContradictionOutcome.Unknown);
    }

    // ── ClassifyAsync — end-to-end with mocked Ollama ──────────────────────

    private static OllamaContentContradictionClassifier Build(Mock<IOllamaClient> ollama)
    {
        var opts = Options.Create(new AniOptions { LocalVerifierModelTag = "qwen3:14b" });
        return new OllamaContentContradictionClassifier(
            ollama.Object, opts,
            NullLogger<OllamaContentContradictionClassifier>.Instance);
    }

    [Fact]
    public async Task ClassifyAsync_EmptyContent_ReturnsNeutralWithoutOllamaCall()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        // No setup — call must not reach the ollama mock.

        var classifier = Build(ollama);
        var v = await classifier.ClassifyAsync(
            interiorContent:     "",
            confirmedSubstrate:  "- bookstore clerk in Wisconsin",
            ct:                  CancellationToken.None);

        v.Outcome.Should().Be(ContradictionOutcome.Neutral);
        ollama.VerifyNoOtherCalls();
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
            .ReturnsAsync("{\"outcome\":\"contradicts\",\"confidence\":0.95," +
                          "\"quote\":\"Wisconsin town\",\"reason\":\"chicago claim\"}");

        var classifier = Build(ollama);
        var v = await classifier.ClassifyAsync(
            interiorContent:    "I love my Chicago apartment view",
            confirmedSubstrate: "- Ani is a bookstore clerk in a small Wisconsin town",
            ct:                 CancellationToken.None);

        v.Outcome.Should().Be(ContradictionOutcome.Contradicts);
        v.Confidence.Should().BeApproximately(0.95f, precision: 0.001f);
        v.Quote.Should().Be("Wisconsin town");
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
            .ThrowsAsync(new HttpRequestException("ollama down"));

        var classifier = Build(ollama);
        var v = await classifier.ClassifyAsync(
            interiorContent:    "some content",
            confirmedSubstrate: "- some substrate",
            ct:                 CancellationToken.None);

        v.Outcome.Should().Be(ContradictionOutcome.Unknown);
        v.Confidence.Should().Be(0f);
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
            .ReturnsAsync("{\"outcome\":\"neutral\",\"confidence\":0.9,\"quote\":null,\"reason\":\"x\"}");

        var opts = Options.Create(new AniOptions { LocalVerifierModelTag = "specific-model:8b" });
        var classifier = new OllamaContentContradictionClassifier(
            ollama.Object, opts,
            NullLogger<OllamaContentContradictionClassifier>.Instance);

        await classifier.ClassifyAsync("content", "substrate", CancellationToken.None);

        capturedModel.Should().Be("specific-model:8b");
    }

    // ── Prompt shape sanity checks ─────────────────────────────────────────

    [Fact]
    public void BuildSystemPrompt_ContainsAllThreeOutcomeLabels()
    {
        var s = OllamaContentContradictionClassifier.BuildSystemPrompt();
        s.Should().Contain("\"contradicts\"");
        s.Should().Contain("\"grounded\"");
        s.Should().Contain("\"neutral\"");
    }

    [Fact]
    public void BuildSystemPrompt_ExplicitlyForbidsPreamble()
    {
        var s = OllamaContentContradictionClassifier.BuildSystemPrompt();
        s.Should().Contain("Reply ONLY");
    }

    [Fact]
    public void BuildUserPrompt_IncludesBothInputBlocks()
    {
        var p = OllamaContentContradictionClassifier.BuildUserPrompt(
            interiorContent:    "I picture Mark in his Chicago apartment",
            confirmedSubstrate: "- bookstore clerk Wisconsin");

        p.Should().Contain("[INTERIOR CONTENT TO AUDIT]");
        p.Should().Contain("[CONFIRMED SUBSTRATE]");
        p.Should().Contain("Chicago apartment");
        p.Should().Contain("bookstore clerk Wisconsin");
    }

    [Fact]
    public void BuildUserPrompt_EmptySubstrate_ShowsPlaceholder()
    {
        var p = OllamaContentContradictionClassifier.BuildUserPrompt("content", "");
        p.Should().Contain("(no confirmed neighbors above similarity threshold)");
    }

    // ── Batch classification (2026-07-07) ──────────────────────────────────

    [Fact]
    public void ParseBatchVerdict_HappyPath_ReturnsAllVerdictsInOrder()
    {
        var raw = @"{
          ""verdicts"": [
            { ""index"": 1, ""outcome"": ""contradicts"", ""confidence"": 0.95, ""quote"": ""wisc"", ""reason"": ""r1"" },
            { ""index"": 2, ""outcome"": ""grounded"",    ""confidence"": 0.90, ""quote"": null,     ""reason"": ""r2"" },
            { ""index"": 3, ""outcome"": ""neutral"",     ""confidence"": 0.85, ""quote"": null,     ""reason"": ""r3"" }
          ]
        }";
        var v = OllamaContentContradictionClassifier.ParseBatchVerdict(raw, expectedCount: 3);

        v.Should().HaveCount(3);
        v[0].Outcome.Should().Be(ContradictionOutcome.Contradicts);
        v[0].Quote.Should().Be("wisc");
        v[1].Outcome.Should().Be(ContradictionOutcome.Grounded);
        v[2].Outcome.Should().Be(ContradictionOutcome.Neutral);
    }

    [Fact]
    public void ParseBatchVerdict_MissingSlot_LeavesUnknownDefault()
    {
        // Only indices 1 and 3 returned — slot 2 must be Unknown.
        var raw = @"{
          ""verdicts"": [
            { ""index"": 1, ""outcome"": ""contradicts"", ""confidence"": 0.9, ""quote"": ""q"", ""reason"": ""r1"" },
            { ""index"": 3, ""outcome"": ""neutral"",     ""confidence"": 0.8, ""quote"": null, ""reason"": ""r3"" }
          ]
        }";
        var v = OllamaContentContradictionClassifier.ParseBatchVerdict(raw, expectedCount: 3);

        v.Should().HaveCount(3);
        v[0].Outcome.Should().Be(ContradictionOutcome.Contradicts);
        v[1].Outcome.Should().Be(ContradictionOutcome.Unknown, "slot 2 was never returned");
        v[2].Outcome.Should().Be(ContradictionOutcome.Neutral);
    }

    [Fact]
    public void ParseBatchVerdict_OutOfRangeIndex_IsIgnored()
    {
        // Model returns index 5 in a batch of 2 — must be silently ignored.
        var raw = @"{
          ""verdicts"": [
            { ""index"": 1, ""outcome"": ""contradicts"", ""confidence"": 0.9, ""quote"": ""q"", ""reason"": ""r"" },
            { ""index"": 5, ""outcome"": ""grounded"",    ""confidence"": 0.9, ""quote"": null, ""reason"": ""r"" }
          ]
        }";
        var v = OllamaContentContradictionClassifier.ParseBatchVerdict(raw, expectedCount: 2);

        v.Should().HaveCount(2);
        v[0].Outcome.Should().Be(ContradictionOutcome.Contradicts);
        v[1].Outcome.Should().Be(ContradictionOutcome.Unknown, "index 5 doesn't map to any slot");
    }

    [Fact]
    public void ParseBatchVerdict_MalformedJson_ReturnsAllUnknown()
    {
        var v = OllamaContentContradictionClassifier.ParseBatchVerdict("not json", expectedCount: 3);
        v.Should().HaveCount(3);
        v.Should().OnlyContain(x => x.Outcome == ContradictionOutcome.Unknown);
    }

    [Fact]
    public async Task ClassifyBatchAsync_SingleItem_DelegatesToScalarPath()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatJsonWithModelAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>()))
            .ReturnsAsync("{\"outcome\":\"grounded\",\"confidence\":0.9,\"quote\":null,\"reason\":\"ok\"}");

        var classifier = Build(ollama);
        var items = new[] { ("content", "substrate") };
        var v = await classifier.ClassifyBatchAsync(items, CancellationToken.None);

        v.Should().HaveCount(1);
        v[0].Outcome.Should().Be(ContradictionOutcome.Grounded);
    }

    [Fact]
    public async Task ClassifyBatchAsync_EmptyList_ReturnsEmpty_NoOllamaCall()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        var classifier = Build(ollama);

        var v = await classifier.ClassifyBatchAsync(
            Array.Empty<(string, string)>(), CancellationToken.None);

        v.Should().BeEmpty();
        ollama.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClassifyBatchAsync_OllamaThrows_AllSlotsUnknown()
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        ollama.Setup(o => o.ChatJsonWithModelAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("down"));

        var classifier = Build(ollama);
        var items = new[]
        {
            ("c1", "s1"),
            ("c2", "s2"),
            ("c3", "s3"),
        };
        var v = await classifier.ClassifyBatchAsync(items, CancellationToken.None);

        v.Should().HaveCount(3);
        v.Should().OnlyContain(x => x.Outcome == ContradictionOutcome.Unknown);
    }

    [Fact]
    public void BuildBatchUserPrompt_EnumeratesRecordsWithPerRecordSubstrate()
    {
        var items = new[]
        {
            ("interior A", "substrate A"),
            ("interior B", "substrate B"),
        };
        var p = OllamaContentContradictionClassifier.BuildBatchUserPrompt(items);

        p.Should().Contain("=== RECORD 1 ===");
        p.Should().Contain("=== RECORD 2 ===");
        p.Should().Contain("interior A");
        p.Should().Contain("substrate A");
        p.Should().Contain("interior B");
        p.Should().Contain("substrate B");
    }

    [Fact]
    public void BuildBatchSystemPrompt_ContainsBatchSchema()
    {
        var s = OllamaContentContradictionClassifier.BuildBatchSystemPrompt();
        s.Should().Contain("\"verdicts\"");
        s.Should().Contain("SAME ORDER");
        s.Should().Contain("\"index\"");
        s.Should().Contain("Reply ONLY");
    }
}
