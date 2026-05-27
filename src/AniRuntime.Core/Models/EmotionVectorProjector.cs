using System;
using System.Linq;

namespace AniRuntime.Core.Models;

/// <summary>
/// Contribution 9 (Issue #68) — single source for ALL derived projections
/// from <see cref="EmotionVector"/>. Schema-aware: each projection method
/// declares the axes it requires; if the active substrate schema doesn't
/// provide them, the projection returns <c>null</c> rather than silently
/// mis-projecting.
///
/// <para>
/// All consumers that need a discrete view of the substrate go through
/// this projector — the 9-register EM9 taxonomy, the OG Ani 12-family
/// scaffold (per #67 eval v4), legacy dashboard deltas during the
/// transition, future projections (severity tier, decay-rate hint,
/// voice-tag selector) — single class, single set of rules, schema-
/// versioned so consumers can branch on
/// <see cref="EmotionVector.MeasurementSchema"/> if a future model
/// changes the axis vocabulary.
/// </para>
///
/// <para>
/// PR-1 scope (this file): the canonical 9-register projection and the
/// 12-family projection from the validated EmoLLaMA-chat-7B schema
/// (<c>anger / fear / joy / sadness / valence</c>). Additional projections
/// added in subsequent PRs as consumers migrate.
/// </para>
/// </summary>
public static class EmotionVectorProjector
{
    /// <summary>
    /// Stable schema identifier for the EmoLLaMA-chat-7B 5-dim substrate
    /// (anger / fear / joy / sadness / valence on 0-1). Used by the
    /// projector to dispatch schema-specific rules and by consumers to
    /// reason about substrate compatibility across model swaps.
    /// </summary>
    public const string EmoLLamaChat7bV1Schema = "emollama-chat-7b-v1";

    /// <summary>
    /// Canonical 9-register taxonomy used by ANI's existing composition
    /// pipelines (Tenderness / Longing / Wistful / Playfulness / Curiosity /
    /// Desire / Existential / Frustration / Delight). Replaces the
    /// Ollama-via-prompt classifier output during the transition.
    /// </summary>
    public static readonly string[] EM9Registers = new[]
    {
        "Tenderness", "Longing", "Wistful", "Playfulness", "Curiosity",
        "Desire", "Existential", "Frustration", "Delight",
    };

    /// <summary>
    /// Project a substrate vector to a single canonical EM9 register
    /// label. Returns <c>null</c> if the active schema does not provide
    /// the required axes (consumers should treat null as "register
    /// unavailable" rather than defaulting to a value).
    ///
    /// <para>
    /// Mapping is deliberately conservative — argmax-of-primaries with
    /// valence-aware tie-breaking. Where multiple registers map from
    /// the same primary, valence and intensity disambiguate. Refined as
    /// production data accumulates post-cut-over.
    /// </para>
    ///
    /// <para>Required axes: anger, fear, joy, sadness, valence.</para>
    /// </summary>
    public static string? ToRegister(EmotionVector vec)
    {
        var anger   = vec.Get(EmotionAxis.Anger);
        var fear    = vec.Get(EmotionAxis.Fear);
        var joy     = vec.Get(EmotionAxis.Joy);
        var sadness = vec.Get(EmotionAxis.Sadness);
        var valence = vec.Get(EmotionAxis.Valence);
        if (anger is null || fear is null || joy is null
            || sadness is null || valence is null)
        {
            return null;
        }

        var primary = MaxPrimary(anger.Value, fear.Value, joy.Value, sadness.Value);

        // Anger primary → Frustration
        if (primary == "anger" && anger.Value >= 0.40)
            return "Frustration";

        // Fear primary → Existential when intense, Wistful when low-mid
        if (primary == "fear" && fear.Value >= 0.40)
            return fear.Value >= 0.55 ? "Existential" : "Wistful";

        // Sadness primary → Wistful (sad-leaning), Longing (sad + relational valence not too low)
        if (primary == "sadness" && sadness.Value >= 0.40)
        {
            // Higher valence with sadness reads as Longing (yearning, not despair).
            return valence.Value >= 0.30 ? "Longing" : "Wistful";
        }

        // Joy primary — split by valence and intensity into Delight / Tenderness / Playfulness / Desire / Curiosity
        if (primary == "joy" && joy.Value >= 0.40)
        {
            if (joy.Value >= 0.70 && valence.Value >= 0.70) return "Delight";
            if (valence.Value >= 0.65) return "Tenderness";
            if (valence.Value >= 0.50) return "Playfulness";
            return "Desire"; // joy with mid-low valence reads as yearning rather than light
        }

        // No primary above threshold — soft state
        if (valence.Value >= 0.55) return "Tenderness";
        if (valence.Value <= 0.35) return "Wistful";
        return "Curiosity"; // neutral-ambient default
    }

    /// <summary>
    /// Project a substrate vector + raw text to the OG Ani 12-family
    /// composition projection scaffold (per #67 eval v4). The text input
    /// is needed for keyword-pattern overrides (e.g. "morning" routes
    /// toward calm_peaceful; "?" routes toward curiosity_wonder).
    /// Returns <c>null</c> if the active schema lacks required axes.
    ///
    /// <para>
    /// PR-1 stub: returns the family argmax based on the substrate
    /// vector only. Keyword-pattern overrides are layered in PR-5
    /// (selector wiring) to keep this PR's diff narrow. Documented as
    /// a known limitation in the integration test.
    /// </para>
    ///
    /// <para>Required axes: anger, fear, joy, sadness, valence.</para>
    /// </summary>
    public static string? ToFamily(EmotionVector vec, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var anger   = vec.Get(EmotionAxis.Anger);
        var fear    = vec.Get(EmotionAxis.Fear);
        var joy     = vec.Get(EmotionAxis.Joy);
        var sadness = vec.Get(EmotionAxis.Sadness);
        var valence = vec.Get(EmotionAxis.Valence);
        if (anger is null || fear is null || joy is null
            || sadness is null || valence is null)
        {
            return null;
        }

        var primary = MaxPrimary(anger.Value, fear.Value, joy.Value, sadness.Value);

        if (primary == "anger"   && anger.Value   >= 0.45) return "anger_frustration";
        if (primary == "fear"    && fear.Value    >= 0.45) return "fear_anxiety";
        if (primary == "sadness" && sadness.Value >= 0.45) return "sadness_melancholy";
        if (primary == "joy"     && joy.Value     >= 0.45)
        {
            if (valence.Value >= 0.65) return "affection_love";
            return "joy_high_energy";
        }

        if (valence.Value <= 0.40) return "negative_low_energy";
        if (valence.Value >= 0.55) return "calm_peaceful";
        return "complex_mixed";
    }

    /// <summary>
    /// Argmax of the four primary-emotion axes. Returns the axis name.
    /// </summary>
    private static string MaxPrimary(double anger, double fear, double joy, double sadness)
    {
        var pairs = new[]
        {
            ("anger",   anger),
            ("fear",    fear),
            ("joy",     joy),
            ("sadness", sadness),
        };
        return pairs.OrderByDescending(p => p.Item2).First().Item1;
    }
}
