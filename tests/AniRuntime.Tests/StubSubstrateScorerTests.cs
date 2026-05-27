using System.Threading;
using AniRuntime.Core.Models;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests;

/// <summary>
/// Contribution 9 (Issue #68) — pins the stub scorer contract:
/// deterministic output (same input → same vector), schema-stamped
/// output, namespaced 16-axis schema mimicking the eventual EI-reg + E-c
/// + V-reg production substrate, graceful empty-input handling.
/// Stub is for unit tests + dev environments; production code uses the
/// EmoLLaMA-backed scorer (PR-2).
/// </summary>
public class StubSubstrateScorerTests
{
    [Fact]
    public async Task ScoreAsync_returns_deterministic_vector_for_same_input()
    {
        var scorer = new StubSubstrateScorer();
        var a = await scorer.ScoreAsync("same input text", CancellationToken.None);
        var b = await scorer.ScoreAsync("same input text", CancellationToken.None);

        // Compare semantic content rather than record-identity: record auto-
        // equality uses reference equality for the Components dictionary, but
        // two scorer calls produce different dictionary instances with the
        // same content. Semantic determinism is what we're pinning here.
        a.MeasurementSchema.Should().Be(b.MeasurementSchema);
        a.Components.Should().BeEquivalentTo(b.Components);
    }

    [Fact]
    public async Task ScoreAsync_returns_different_vectors_for_different_inputs()
    {
        var scorer = new StubSubstrateScorer();
        var a = await scorer.ScoreAsync("first text", CancellationToken.None);
        var b = await scorer.ScoreAsync("second text", CancellationToken.None);

        a.Components.Should().NotBeEquivalentTo(b.Components);
    }

    [Fact]
    public async Task ScoreAsync_populates_namespaced_EI_reg_keys()
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("ei.anger").Should().NotBeNull();
        vec.Get("ei.fear").Should().NotBeNull();
        vec.Get("ei.joy").Should().NotBeNull();
        vec.Get("ei.sadness").Should().NotBeNull();
    }

    [Fact]
    public async Task ScoreAsync_populates_namespaced_dimensional_keys()
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get("dim.valence").Should().NotBeNull();
    }

    [Fact]
    public async Task ScoreAsync_populates_all_11_E_c_multilabel_keys()
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        string[] ecLabels = { "anger", "anticipation", "disgust", "fear", "joy",
                              "love", "optimism", "pessimism", "sadness",
                              "surprise", "trust" };
        foreach (var label in ecLabels)
        {
            vec.Get($"ec.{label}").Should().NotBeNull(
                $"ec.{label} must be populated for the full multilabel schema");
        }
    }

    [Fact]
    public async Task ScoreAsync_EI_reg_values_are_in_unit_interval()
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        foreach (var axis in new[] { "ei.anger", "ei.fear", "ei.joy", "ei.sadness", "dim.valence" })
        {
            var value = vec.Get(axis);
            value.Should().NotBeNull();
            value!.Value.Should().BeInRange(0.0, 1.0);
        }
    }

    [Fact]
    public async Task ScoreAsync_E_c_values_are_binary_zero_or_one()
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        string[] ecLabels = { "anger", "anticipation", "disgust", "fear", "joy",
                              "love", "optimism", "pessimism", "sadness",
                              "surprise", "trust" };
        foreach (var label in ecLabels)
        {
            var value = vec.Get($"ec.{label}");
            value.Should().NotBeNull();
            value!.Value.Should().BeOneOf(new[] { 0.0, 1.0 },
                $"ec.{label} mimics E-c multilabel-presence semantics (binary)");
        }
    }

    [Fact]
    public async Task ScoreAsync_stamps_stub_schema_id()
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.MeasurementSchema.Should().Be(StubSubstrateScorer.SchemaId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null!)]
    public async Task ScoreAsync_handles_empty_input_gracefully(string? input)
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync(input!, CancellationToken.None);

        vec.Should().NotBeNull();
        vec.MeasurementSchema.Should().Be(StubSubstrateScorer.SchemaId);
        vec.Get("ei.anger").Should().Be(0.0);
        vec.Get("dim.valence").Should().Be(0.5, "neutral valence for empty input");
        vec.Get("ec.joy").Should().Be(0.0,
            "E-c labels read as not-present for empty input");
    }
}
