using System.Threading;
using AniRuntime.Core.Models;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests;

/// <summary>
/// Contribution 9 (Issue #68) — pins the stub scorer contract:
/// deterministic output (same input → same vector), schema-stamped
/// output, graceful empty-input handling. Stub is for unit tests + dev
/// environments; production code uses the EmoLLaMA-backed scorer
/// (PR-2).
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
    public async Task ScoreAsync_populates_all_standard_axes()
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        vec.Get(EmotionAxis.Anger).Should().NotBeNull();
        vec.Get(EmotionAxis.Fear).Should().NotBeNull();
        vec.Get(EmotionAxis.Joy).Should().NotBeNull();
        vec.Get(EmotionAxis.Sadness).Should().NotBeNull();
        vec.Get(EmotionAxis.Valence).Should().NotBeNull();
    }

    [Fact]
    public async Task ScoreAsync_values_are_in_unit_interval()
    {
        var scorer = new StubSubstrateScorer();
        var vec = await scorer.ScoreAsync("any text", CancellationToken.None);

        foreach (var (_, v) in vec.Components)
        {
            v.Should().BeInRange(0.0, 1.0);
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
        vec.Get(EmotionAxis.Anger).Should().Be(0.0);
        vec.Get(EmotionAxis.Valence).Should().Be(0.5, "neutral valence for empty input");
    }
}
