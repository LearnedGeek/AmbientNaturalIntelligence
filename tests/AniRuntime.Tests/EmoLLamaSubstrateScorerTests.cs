using System.Threading;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AniRuntime.Tests;

/// <summary>
/// Contribution 9 PR-2 (Issue #68) — pins the EmoLLaMA-backed scorer
/// contract: full 16-axis namespaced schema, EI-reg clamping to [0,1],
/// E-c binarization at 0.5, graceful malformed-JSON handling, schema-stamp,
/// and empty-input shortcut (no Ollama call).
/// </summary>
public class EmoLLamaSubstrateScorerTests
{
    private static readonly string WellFormedJson = """
        {
          "ei": {"anger": 0.10, "fear": 0.20, "joy": 0.85, "sadness": 0.05},
          "ec": {"anger": 0, "anticipation": 1, "disgust": 0, "fear": 0, "joy": 1, "love": 0, "optimism": 1, "pessimism": 0, "sadness": 0, "surprise": 0, "trust": 1},
          "dim": {"valence": 0.78}
        }
        """;

    private static EmoLLamaSubstrateScorer BuildScorer(Mock<IOllamaClient> ollama, string substrateModel = "emollama-chat-7b")
    {
        var opts = Options.Create(new OllamaOptions { SubstrateModel = substrateModel });
        return new EmoLLamaSubstrateScorer(ollama.Object, opts, NullLogger<EmoLLamaSubstrateScorer>.Instance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null!)]
    public async Task ScoreAsync_returns_neutral_without_calling_ollama_for_empty_input(string? input)
    {
        var ollama = new Mock<IOllamaClient>(MockBehavior.Strict);
        var scorer = BuildScorer(ollama);

        var vec = await scorer.ScoreAsync(input!, CancellationToken.None);

        vec.MeasurementSchema.Should().Be(EmoLLamaSubstrateScorer.SchemaId);
        vec.Get("dim.valence").Should().Be(0.5, "neutral valence for empty input");
        vec.Get("ei.joy").Should().Be(0.0);
        vec.Get("ec.joy").Should().Be(0.0);
        // Strict-mock with no setups asserts no Ollama call happened.
    }

    [Fact]
    public async Task ScoreAsync_calls_ollama_with_configured_substrate_model()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<string?>()))
              .ReturnsAsync(WellFormedJson);

        var scorer = BuildScorer(ollama, substrateModel: "custom-emollama-model");
        await scorer.ScoreAsync("some text", CancellationToken.None);

        ollama.Verify(o => o.ChatJsonWithModelAsync(
            "custom-emollama-model",
            It.IsAny<string>(),
            It.IsAny<IEnumerable<ChatMessage>>(),
            "some text",
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ScoreAsync_parses_well_formed_json_into_namespaced_schema()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ReturnsAsync(WellFormedJson);

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.anger").Should().Be(0.10);
        vec.Get("ei.fear").Should().Be(0.20);
        vec.Get("ei.joy").Should().Be(0.85);
        vec.Get("ei.sadness").Should().Be(0.05);
        vec.Get("ec.anticipation").Should().Be(1.0);
        vec.Get("ec.joy").Should().Be(1.0);
        vec.Get("ec.trust").Should().Be(1.0);
        vec.Get("ec.disgust").Should().Be(0.0);
        vec.Get("dim.valence").Should().Be(0.78);
    }

    [Fact]
    public async Task ScoreAsync_stamps_schema_id()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ReturnsAsync(WellFormedJson);

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.MeasurementSchema.Should().Be(EmoLLamaSubstrateScorer.SchemaId);
        vec.MeasurementSchema.Should().Be("emollama-chat-7b-v1");
    }

    [Fact]
    public async Task ScoreAsync_clamps_EI_reg_values_to_unit_interval()
    {
        const string outOfRangeJson = """
            {
              "ei": {"anger": 1.5, "fear": -0.2, "joy": 0.7, "sadness": 0.3},
              "ec": {"anger": 0, "anticipation": 0, "disgust": 0, "fear": 0, "joy": 0, "love": 0, "optimism": 0, "pessimism": 0, "sadness": 0, "surprise": 0, "trust": 0},
              "dim": {"valence": 2.0}
            }
            """;
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ReturnsAsync(outOfRangeJson);

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.anger").Should().Be(1.0, "model output 1.5 clamped to 1.0");
        vec.Get("ei.fear").Should().Be(0.0, "model output -0.2 clamped to 0.0");
        vec.Get("dim.valence").Should().Be(1.0, "valence clamped to unit interval");
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.49, 0.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.7, 1.0)]
    [InlineData(1.0, 1.0)]
    public async Task ScoreAsync_binarizes_E_c_values_at_half(double rawValue, double expected)
    {
        // Some models emit floats for E-c despite the spec; we threshold at 0.5
        // to land on {0.0, 1.0} as the schema requires.
        var json = $$"""
            {
              "ei": {"anger": 0, "fear": 0, "joy": 0, "sadness": 0},
              "ec": {"anger": {{rawValue}}, "anticipation": 0, "disgust": 0, "fear": 0, "joy": 0, "love": 0, "optimism": 0, "pessimism": 0, "sadness": 0, "surprise": 0, "trust": 0},
              "dim": {"valence": 0.5}
            }
            """;
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ReturnsAsync(json);

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ec.anger").Should().Be(expected);
    }

    [Fact]
    public async Task ScoreAsync_returns_neutral_on_malformed_json()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ReturnsAsync("not actually json at all");

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.MeasurementSchema.Should().Be(EmoLLamaSubstrateScorer.SchemaId);
        vec.Get("dim.valence").Should().Be(0.5);
        vec.Get("ei.joy").Should().Be(0.0);
        vec.Get("ec.joy").Should().Be(0.0);
    }

    [Fact]
    public async Task ScoreAsync_returns_neutral_on_ollama_exception()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ThrowsAsync(new HttpRequestException("ollama unreachable"));

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.MeasurementSchema.Should().Be(EmoLLamaSubstrateScorer.SchemaId);
        vec.Get("dim.valence").Should().Be(0.5,
            "neutral vector returned so downstream cognitive cycle isn't coupled to model availability");
    }

    [Fact]
    public async Task ScoreAsync_propagates_cancellation()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ThrowsAsync(new OperationCanceledException());

        var scorer = BuildScorer(ollama);
        var act = async () => await scorer.ScoreAsync("any text", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ScoreAsync_fills_missing_groups_with_neutrals()
    {
        // Model returned only the ei group; ec and dim should be filled with
        // neutral defaults (0.0 / 0.5) so consumers see the full schema.
        const string partialJson = """
            {
              "ei": {"anger": 0.4, "fear": 0.1, "joy": 0.9, "sadness": 0.0}
            }
            """;
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ReturnsAsync(partialJson);

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.joy").Should().Be(0.9);
        vec.Get("ec.joy").Should().Be(0.0, "missing ec group defaults to all-zero");
        vec.Get("dim.valence").Should().Be(0.5, "missing dim.valence defaults to neutral 0.5");
    }

    [Fact]
    public async Task ScoreAsync_populates_all_16_axes()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.ChatJsonWithModelAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string?>()))
              .ReturnsAsync(WellFormedJson);

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        string[] eiKeys  = { "ei.anger", "ei.fear", "ei.joy", "ei.sadness" };
        string[] ecKeys  = { "ec.anger", "ec.anticipation", "ec.disgust", "ec.fear", "ec.joy",
                             "ec.love", "ec.optimism", "ec.pessimism", "ec.sadness",
                             "ec.surprise", "ec.trust" };
        string[] dimKeys = { "dim.valence" };

        foreach (var key in eiKeys.Concat(ecKeys).Concat(dimKeys))
            vec.Get(key).Should().NotBeNull($"{key} must be populated for the full schema");

        vec.Components.Should().HaveCount(16);
    }
}
