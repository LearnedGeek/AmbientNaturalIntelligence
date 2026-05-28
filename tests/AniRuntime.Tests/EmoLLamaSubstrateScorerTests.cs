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
/// Contribution 9 PR-3 (Issue #68, May 28, 2026) — pins the EmoLLaMA-7B
/// scorer's multi-call architecture against Ollama's <c>/api/generate</c>
/// endpoint with the EmoLLM paper's verbatim instruction templates.
///
/// <para>
/// The architecture is 6 calls per scoring event (4 EI-reg, 1 E-c
/// multi-label, 1 V-reg) — the PR-2 single-chat-JSON approach was rejected
/// after empirical probing showed the model only emits structured task
/// output via /api/generate with the paper's raw templates.
/// </para>
/// </summary>
public class EmoLLamaSubstrateScorerTests
{
    private static EmoLLamaSubstrateScorer BuildScorer(
        Mock<IOllamaClient> ollama, string substrateModel = "emollama-7b")
    {
        var opts = Options.Create(new OllamaOptions { SubstrateModel = substrateModel });
        return new EmoLLamaSubstrateScorer(ollama.Object, opts, NullLogger<EmoLLamaSubstrateScorer>.Instance);
    }

    private static Mock<IOllamaClient> MockOllamaResponding(
        Func<string, string> respondToPrompt)
    {
        var mock = new Mock<IOllamaClient>();
        mock.Setup(o => o.GenerateAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<float?>(),
                    It.IsAny<int?>(),
                    It.IsAny<string?>()))
            .ReturnsAsync((string model, string prompt, CancellationToken ct, float? t, int? mt, string? ka) =>
                respondToPrompt(prompt));
        return mock;
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
        vec.Get("dim.valence").Should().Be(0.5);
        vec.Get("ei.joy").Should().Be(0.0);
        vec.Get("ec.joy").Should().Be(0.0);
        // Strict mock with no setups asserts no Ollama call happened.
    }

    [Fact]
    public async Task ScoreAsync_makes_six_generate_calls_per_scoring_event()
    {
        var ollama = MockOllamaResponding(_ => "0.5");
        var scorer = BuildScorer(ollama);

        await scorer.ScoreAsync("some text", CancellationToken.None);

        // 4 EI-reg + 1 E-c + 1 V-reg = 6 calls
        ollama.Verify(o => o.GenerateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
            It.IsAny<float?>(), It.IsAny<int?>(), It.IsAny<string?>()),
            Times.Exactly(6));
    }

    [Fact]
    public async Task ScoreAsync_passes_configured_substrate_model_to_every_call()
    {
        var ollama = MockOllamaResponding(_ => "0.5");
        var scorer = BuildScorer(ollama, substrateModel: "custom-emollama-tag");

        await scorer.ScoreAsync("some text", CancellationToken.None);

        ollama.Verify(o => o.GenerateAsync(
            "custom-emollama-tag",
            It.IsAny<string>(), It.IsAny<CancellationToken>(),
            It.IsAny<float?>(), It.IsAny<int?>(), It.IsAny<string?>()),
            Times.Exactly(6));
    }

    [Fact]
    public async Task ScoreAsync_parses_EI_reg_intensities_from_per_emotion_calls()
    {
        // Respond with a distinct intensity per emotion so we can verify
        // the per-emotion routing.
        var ollama = MockOllamaResponding(prompt =>
        {
            if (prompt.Contains("Emotion: anger"))   return " 0.10";
            if (prompt.Contains("Emotion: fear"))    return " 0.20";
            if (prompt.Contains("Emotion: joy"))     return " 0.85";
            if (prompt.Contains("Emotion: sadness")) return " 0.05";
            return " 0.0";
        });

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.anger").Should().Be(0.10);
        vec.Get("ei.fear").Should().Be(0.20);
        vec.Get("ei.joy").Should().Be(0.85);
        vec.Get("ei.sadness").Should().Be(0.05);
    }

    [Fact]
    public async Task ScoreAsync_parses_E_c_multilabel_from_comma_separated_list()
    {
        var ollama = MockOllamaResponding(prompt =>
        {
            // E-c call recognizable by its "This text contains emotions:" suffix
            if (prompt.Contains("This text contains emotions:"))
                return " joy, love, optimism.";
            return " 0.5";
        });

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ec.joy").Should().Be(1.0);
        vec.Get("ec.love").Should().Be(1.0);
        vec.Get("ec.optimism").Should().Be(1.0);
        // Emotions NOT in the list should be 0.0
        vec.Get("ec.anger").Should().Be(0.0);
        vec.Get("ec.sadness").Should().Be(0.0);
        vec.Get("ec.disgust").Should().Be(0.0);
    }

    [Fact]
    public async Task ScoreAsync_rescales_V_reg_from_signed_to_unit_interval()
    {
        // EmoLLaMA emits V-reg on [-1, +1]; scorer must rescale to [0, 1].
        var ollama = MockOllamaResponding(prompt =>
        {
            if (prompt.Contains("Valence Score:")) return " -0.32";
            return " 0.5";
        });

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        // -0.32 in [-1, +1] → 0.34 in [0, 1]
        vec.Get("dim.valence").Should().BeApproximately(0.34, 0.001);
    }

    [Fact]
    public async Task ScoreAsync_rescales_V_reg_neutral_to_half()
    {
        var ollama = MockOllamaResponding(prompt =>
        {
            if (prompt.Contains("Valence Score:")) return " 0.0";
            return " 0.5";
        });

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        // 0.0 in [-1, +1] → 0.5 in [0, 1]
        vec.Get("dim.valence").Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public async Task ScoreAsync_clamps_V_reg_above_one_to_unit()
    {
        var ollama = MockOllamaResponding(prompt =>
        {
            if (prompt.Contains("Valence Score:")) return " 1.0";
            return " 0.5";
        });

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("dim.valence").Should().Be(1.0);
    }

    [Fact]
    public async Task ScoreAsync_clamps_EI_reg_above_one_to_unit()
    {
        var ollama = MockOllamaResponding(_ => " 1.5");
        var scorer = BuildScorer(ollama);

        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.anger").Should().Be(1.0);
        vec.Get("ei.joy").Should().Be(1.0);
    }

    [Fact]
    public async Task ScoreAsync_clamps_EI_reg_below_zero_to_zero()
    {
        var ollama = MockOllamaResponding(_ => " -0.2");
        var scorer = BuildScorer(ollama);

        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.anger").Should().Be(0.0);
    }

    [Fact]
    public async Task ScoreAsync_unparseable_EI_reg_response_falls_back_to_neutral_for_that_axis()
    {
        var ollama = MockOllamaResponding(prompt =>
        {
            if (prompt.Contains("Emotion: anger")) return "totally garbage";
            return " 0.5";
        });

        var scorer = BuildScorer(ollama);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.anger").Should().Be(0.0,
            "unparseable EI-reg response falls back to neutral 0.0 for that axis");
        vec.Get("ei.joy").Should().Be(0.5,
            "other axes that parsed cleanly are preserved");
    }

    [Fact]
    public async Task ScoreAsync_stamps_schema_id()
    {
        var ollama = MockOllamaResponding(_ => " 0.5");
        var scorer = BuildScorer(ollama);

        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.MeasurementSchema.Should().Be(EmoLLamaSubstrateScorer.SchemaId);
        vec.MeasurementSchema.Should().Be("emollama-7b-v1");
    }

    [Fact]
    public async Task ScoreAsync_populates_all_16_axes()
    {
        var ollama = MockOllamaResponding(prompt =>
        {
            if (prompt.Contains("This text contains emotions:")) return " joy, trust.";
            return " 0.5";
        });

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

    [Fact]
    public async Task ScoreAsync_returns_partial_vector_when_a_single_call_throws()
    {
        var anger = "Emotion: anger";
        var mock = new Mock<IOllamaClient>();
        mock.Setup(o => o.GenerateAsync(
                    It.IsAny<string>(), It.Is<string>(p => p.Contains(anger)),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<float?>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("ollama hiccup on anger"));
        mock.Setup(o => o.GenerateAsync(
                    It.IsAny<string>(), It.Is<string>(p => !p.Contains(anger)),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<float?>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .ReturnsAsync(" 0.7");

        var scorer = BuildScorer(mock);
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.anger").Should().Be(0.0,
            "the failing axis falls back to neutral");
        vec.Get("ei.joy").Should().Be(0.7,
            "the other axes that succeeded are preserved");
    }

    [Fact]
    public async Task ScoreAsync_propagates_cancellation()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(o => o.GenerateAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<float?>(), It.IsAny<int?>(), It.IsAny<string?>()))
              .ThrowsAsync(new OperationCanceledException());

        var scorer = BuildScorer(ollama);
        var act = async () => await scorer.ScoreAsync("any text", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
