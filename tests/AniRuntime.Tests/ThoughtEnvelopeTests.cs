using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 3 (2026-08-18) — verifies
/// <see cref="InnerThoughtResult"/> implements <see cref="IThoughtEnvelope"/>
/// and its <see cref="IProvenancedContent{T}.SourceType"/> derives correctly
/// from the classified <see cref="ThoughtShape"/>.
/// </summary>
public class ThoughtEnvelopeTests
{
    [Fact]
    public void InnerThoughtResult_ImplementsThoughtEnvelope_ExposesContentAsThought()
    {
        var result = new InnerThoughtResult(
            Thought:    "sitting in the bookstore",
            Reflection: null,
            Valence:    0.6f,
            Shape:      ThoughtShape.CoherentThought);
        IThoughtEnvelope env = result;

        env.Content.Should().Be("sitting in the bookstore");
        env.Shape.Should().Be(ThoughtShape.CoherentThought);
        env.Producer.Should().Be("InnerThoughtPhase");
    }

    [Theory]
    [InlineData(ThoughtShape.CoherentThought,  "thought.coherent-thought")]
    [InlineData(ThoughtShape.ThirdPersonFrame, "thought.third-person-frame")]
    [InlineData(ThoughtShape.FactCatalog,      "thought.fact-catalog")]
    [InlineData(ThoughtShape.MumbleLoop,       "thought.mumble-loop")]
    [InlineData(ThoughtShape.Unclassified,     "thought.unclassified")]
    public void InnerThoughtResult_SourceType_MapsFromShape(ThoughtShape shape, string expected)
    {
        IThoughtEnvelope env = new InnerThoughtResult(
            Thought: "x", Reflection: null, Valence: 0f, Shape: shape);
        env.SourceType.Should().Be(expected);
    }

    [Fact]
    public void InnerThoughtResult_DefaultShape_IsUnclassified()
    {
        // Backward-compatibility: pre-Phase-3 construction sites that don't
        // pass Shape must still get a valid envelope (Shape defaults to
        // Unclassified so downstream can distinguish "not yet classified"
        // from any of the four positive shapes).
        var result = new InnerThoughtResult("x", null, 0f);
        result.Shape.Should().Be(ThoughtShape.Unclassified);
        ((IThoughtEnvelope)result).SourceType.Should().Be("thought.unclassified");
    }
}
