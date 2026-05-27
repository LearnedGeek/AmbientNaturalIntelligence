namespace AniRuntime.Core.Models;

/// <summary>
/// Contribution 9 (Issue #68) — dimensional-feature axes for type-safe access
/// to the substrate's continuous dimensional components. Distinct from
/// <see cref="EmotionAxis"/>, which carries categorical-emotion intensities
/// on the same substrate.
///
/// <para>
/// Dimensional features describe orthogonal axes of affective space rather
/// than amounts of named emotions. Valence is the canonical example: the
/// positive-to-negative axis along which emotions vary. Russell's circumplex,
/// PAD (Pleasure-Arousal-Dominance), and the EmoBank annotation framework all
/// treat valence, arousal, and dominance as separate dimensional features
/// from categorical-emotion labels. The current production scorer
/// (EmoLLaMA-chat-7B V-reg) emits valence as a regression scalar 0-1; future
/// scorers may add arousal, dominance, or other dimensional axes.
/// </para>
///
/// <para>
/// Storage is dictionary-keyed in <see cref="EmotionVector.Components"/>;
/// the production schema uses namespaced keys (<c>"dim.valence"</c>,
/// eventually <c>"dim.arousal"</c>, etc.). Consumers that need future
/// dimensional axes can access them via raw-string
/// <see cref="EmotionVector.Get(string)"/> without waiting for the enum to
/// be extended.
/// </para>
/// </summary>
public enum DimensionAxis
{
    Valence,
}

public static class DimensionAxisExtensions
{
    /// <summary>
    /// Stable, lowercase key for the bare dimension name (no source-task
    /// prefix). <c>DimensionAxis.Valence.Key() == "valence"</c>. The
    /// production-substrate namespaced form is <c>$"dim.{axis.Key()}"</c>.
    /// </summary>
    public static string Key(this DimensionAxis axis) =>
        axis.ToString().ToLowerInvariant();
}
