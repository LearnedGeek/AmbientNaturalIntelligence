using System.Collections.Generic;
using AniRuntime.Core.Models;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests;

/// <summary>
/// Contribution 9 (Issue #68) — pins the projection contract:
/// schema-aware (returns null on missing axes), deterministic, no silent
/// mis-projection. Refined as production data accumulates post-cut-over.
/// </summary>
public class EmotionVectorProjectorTests
{
    private static EmotionVector Vec(double anger, double fear, double joy, double sadness, double valence) =>
        new EmotionVector(
            new Dictionary<string, double>
            {
                [EmotionAxis.Anger.Key()]   = anger,
                [EmotionAxis.Fear.Key()]    = fear,
                [EmotionAxis.Joy.Key()]     = joy,
                [EmotionAxis.Sadness.Key()] = sadness,
                [EmotionAxis.Valence.Key()] = valence,
            },
            EmotionVectorProjector.EmoLLamaChat7bV1Schema);

    // ===== ToRegister =====

    [Fact]
    public void ToRegister_returns_null_when_required_axis_missing()
    {
        var vec = new EmotionVector(
            new Dictionary<string, double>
            {
                [EmotionAxis.Anger.Key()]   = 0.5,
                // fear missing
                [EmotionAxis.Joy.Key()]     = 0.1,
                [EmotionAxis.Sadness.Key()] = 0.2,
                [EmotionAxis.Valence.Key()] = 0.4,
            },
            "partial-schema");

        EmotionVectorProjector.ToRegister(vec).Should().BeNull(
            "missing axes must produce null, not a silent default register");
    }

    [Fact]
    public void ToRegister_maps_anger_primary_to_Frustration()
    {
        // anger=0.68, valence=0.2 — the "see red" production case
        EmotionVectorProjector.ToRegister(Vec(0.68, 0.6, 0.0, 0.5, 0.2))
            .Should().Be("Frustration");
    }

    [Fact]
    public void ToRegister_maps_sadness_primary_with_low_valence_to_Wistful()
    {
        // sadness=0.7, valence=0.2 — grief profile
        EmotionVectorProjector.ToRegister(Vec(0.2, 0.4, 0.1, 0.7, 0.2))
            .Should().Be("Wistful");
    }

    [Fact]
    public void ToRegister_maps_sadness_primary_with_higher_valence_to_Longing()
    {
        // sadness=0.55, valence=0.40 — yearning rather than despair
        EmotionVectorProjector.ToRegister(Vec(0.2, 0.3, 0.2, 0.55, 0.40))
            .Should().Be("Longing");
    }

    [Fact]
    public void ToRegister_maps_high_joy_with_high_valence_to_Delight()
    {
        // joy=0.90, valence=0.92 — "I just got the job!" profile
        EmotionVectorProjector.ToRegister(Vec(0.05, 0.0, 0.90, 0.0, 0.92))
            .Should().Be("Delight");
    }

    [Fact]
    public void ToRegister_maps_joy_primary_with_moderate_valence_to_Tenderness()
    {
        // joy=0.60, valence=0.70 — warm-affection profile
        EmotionVectorProjector.ToRegister(Vec(0.05, 0.0, 0.60, 0.2, 0.70))
            .Should().Be("Tenderness");
    }

    [Fact]
    public void ToRegister_maps_fear_primary_with_high_intensity_to_Existential()
    {
        EmotionVectorProjector.ToRegister(Vec(0.2, 0.65, 0.1, 0.3, 0.3))
            .Should().Be("Existential");
    }

    [Fact]
    public void ToRegister_no_primary_above_threshold_uses_valence_fallback()
    {
        // All primaries low; high valence → Tenderness
        EmotionVectorProjector.ToRegister(Vec(0.1, 0.1, 0.3, 0.1, 0.65))
            .Should().Be("Tenderness");

        // All primaries low; low valence → Wistful
        EmotionVectorProjector.ToRegister(Vec(0.1, 0.1, 0.2, 0.2, 0.30))
            .Should().Be("Wistful");

        // All primaries low; mid valence → Curiosity (neutral-ambient default)
        EmotionVectorProjector.ToRegister(Vec(0.1, 0.1, 0.2, 0.2, 0.45))
            .Should().Be("Curiosity");
    }

    // ===== ToFamily =====

    [Fact]
    public void ToFamily_returns_null_when_required_axis_missing()
    {
        var vec = new EmotionVector(
            new Dictionary<string, double>
            {
                [EmotionAxis.Anger.Key()] = 0.5,
            },
            "partial-schema");

        EmotionVectorProjector.ToFamily(vec, "any text").Should().BeNull();
    }

    [Fact]
    public void ToFamily_maps_anger_primary_to_anger_frustration()
    {
        EmotionVectorProjector.ToFamily(Vec(0.68, 0.6, 0.0, 0.5, 0.2), "irrelevant")
            .Should().Be("anger_frustration");
    }

    [Fact]
    public void ToFamily_maps_sadness_primary_to_sadness_melancholy()
    {
        EmotionVectorProjector.ToFamily(Vec(0.2, 0.4, 0.1, 0.7, 0.2), "")
            .Should().Be("sadness_melancholy");
    }

    [Fact]
    public void ToFamily_maps_joy_primary_high_valence_to_affection_love()
    {
        EmotionVectorProjector.ToFamily(Vec(0.05, 0.0, 0.60, 0.2, 0.82), "thinking of you")
            .Should().Be("affection_love");
    }

    [Fact]
    public void ToFamily_maps_joy_primary_low_valence_to_joy_high_energy()
    {
        EmotionVectorProjector.ToFamily(Vec(0.1, 0.1, 0.60, 0.2, 0.50), "")
            .Should().Be("joy_high_energy");
    }

    [Fact]
    public void ToFamily_low_intensity_low_valence_maps_to_negative_low_energy()
    {
        EmotionVectorProjector.ToFamily(Vec(0.1, 0.1, 0.2, 0.2, 0.30), "")
            .Should().Be("negative_low_energy");
    }

    [Fact]
    public void ToFamily_low_intensity_high_valence_maps_to_calm_peaceful()
    {
        EmotionVectorProjector.ToFamily(Vec(0.1, 0.1, 0.3, 0.1, 0.65), "")
            .Should().Be("calm_peaceful");
    }
}
