using System.Collections.Generic;
using AniRuntime.Core.Models;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests;

/// <summary>
/// Contribution 9 (Issue #68) — pins the schema-flexible substrate
/// contract: enum-keyed access for standard axes, raw-string access for
/// future-schema axes, null on missing axes (no silent default-to-zero).
/// </summary>
public class EmotionVectorTests
{
    [Fact]
    public void Get_returns_value_for_enum_axis_present_in_components()
    {
        var vec = new EmotionVector(
            new Dictionary<string, double>
            {
                ["anger"] = 0.42,
            },
            "test-schema");

        vec.Get(EmotionAxis.Anger).Should().Be(0.42);
    }

    [Fact]
    public void Get_returns_null_for_enum_axis_missing_from_components()
    {
        var vec = new EmotionVector(
            new Dictionary<string, double> { ["anger"] = 0.42 },
            "test-schema");

        vec.Get(EmotionAxis.Fear).Should().BeNull(
            "missing axes must return null so schema mismatches surface");
    }

    [Fact]
    public void Get_returns_value_for_raw_string_axis_not_in_enum()
    {
        // Future-model schemas may emit axes not enumerated in EmotionAxis;
        // raw-string access must still work.
        var vec = new EmotionVector(
            new Dictionary<string, double> { ["severity"] = 0.7 },
            "future-model-v2");

        vec.Get("severity").Should().Be(0.7);
    }

    [Fact]
    public void Get_returns_null_for_unknown_string_axis()
    {
        var vec = new EmotionVector(
            new Dictionary<string, double> { ["anger"] = 0.1 },
            "test-schema");

        vec.Get("dominance").Should().BeNull();
    }

    [Fact]
    public void Empty_vector_has_no_components_and_empty_schema()
    {
        EmotionVector.Empty.Components.Should().BeEmpty();
        EmotionVector.Empty.MeasurementSchema.Should().Be(string.Empty);
        EmotionVector.Empty.Get(EmotionAxis.Anger).Should().BeNull();
    }

    [Theory]
    [InlineData(EmotionAxis.Anger,   "anger")]
    [InlineData(EmotionAxis.Fear,    "fear")]
    [InlineData(EmotionAxis.Joy,     "joy")]
    [InlineData(EmotionAxis.Sadness, "sadness")]
    public void EmotionAxis_Key_extension_produces_lowercase_string_form(EmotionAxis axis, string expected)
    {
        axis.Key().Should().Be(expected);
    }

    [Fact]
    public void DimensionAxis_Key_extension_produces_lowercase_valence()
    {
        DimensionAxis.Valence.Key().Should().Be("valence");
    }

    [Fact]
    public void Get_DimensionAxis_overload_reads_dimensional_features()
    {
        var vec = new EmotionVector(
            new Dictionary<string, double> { ["valence"] = 0.7 },
            "test-schema");

        vec.Get(DimensionAxis.Valence).Should().Be(0.7);
    }

    [Fact]
    public void EmotionAxis_and_DimensionAxis_are_distinct_namespaces()
    {
        // Type system check: if Valence were still in EmotionAxis this wouldn't
        // compile because EmotionAxis would still have the Valence member.
        // Asserting the enum's member count makes the regression explicit.
        Enum.GetValues<EmotionAxis>().Should().HaveCount(4,
            "EmotionAxis is categorical-emotion only — Valence belongs in DimensionAxis");
        Enum.GetValues<DimensionAxis>().Should().HaveCount(1,
            "DimensionAxis currently contains only Valence; future scorers may add Arousal/Dominance");
    }

    [Fact]
    public void MeasurementSchema_is_preserved_through_record_equality()
    {
        var a = new EmotionVector(
            new Dictionary<string, double> { ["anger"] = 0.5 },
            "schema-a");
        var b = new EmotionVector(
            new Dictionary<string, double> { ["anger"] = 0.5 },
            "schema-b");

        a.Should().NotBe(b, "different schemas must produce different vectors");
    }
}
