namespace AniRuntime.Core.Models;

/// <summary>
/// Contribution 9 (Issue #68) — categorical-emotion axes for type-safe access
/// to the substrate's emotion components. Distinct from <see cref="DimensionAxis"/>,
/// which carries dimensional features (valence, arousal, dominance) on the same
/// substrate.
///
/// <para>
/// Categorical emotions are quantities of named emotions: how much anger,
/// fear, joy, or sadness is in this text. Dimensional features describe
/// orthogonal axes of affective space (where on the positive-negative
/// dimension this text sits). Conflating the two in one enum forces consumers
/// to treat ontologically different signals as if they were the same kind of
/// thing. This enum stays categorical-emotion-only.
/// </para>
///
/// <para>
/// Storage is dictionary-keyed (see <see cref="EmotionVector.Components"/>),
/// so future-model schemas can introduce axes not enumerated here. The
/// production scorer (PR-2) emits namespaced keys
/// (<c>"ei.anger"</c> for EI-reg regression intensity,
/// <c>"ec.anger"</c> for E-c multilabel presence, <c>"dim.valence"</c>
/// for V-reg dimensional). The keys produced by
/// <see cref="EmotionAxisExtensions.Key"/> are the bare emotion names without
/// the source-task prefix; consumers that need the prefixed form construct it
/// themselves (e.g. <c>$"ei.{EmotionAxis.Anger.Key()}"</c>).
/// </para>
///
/// <para>
/// Honest constraint on the current production substrate (EmoLLaMA-chat-7B
/// EI-reg): only four categorical emotions are measured as gradient
/// intensities — anger, fear, joy, sadness. Other categorical emotions
/// (anticipation, disgust, love, optimism, pessimism, surprise, trust) are
/// available through the same model's E-c multilabel-classification task but
/// arrive as binary presence rather than gradient intensity. PR-2 enriches the
/// substrate by combining EI-reg and E-c outputs. The fractal-seed concern
/// (substrate dimensionality sets the ceiling on downstream affective
/// expressiveness) is named explicitly in #67 and in the EmoLLM-Production-Eval
/// short paper draft.
/// </para>
/// </summary>
public enum EmotionAxis
{
    Anger,
    Fear,
    Joy,
    Sadness,
}

public static class EmotionAxisExtensions
{
    /// <summary>
    /// Stable, lowercase key for the bare emotion name (no source-task
    /// prefix). <c>EmotionAxis.Anger.Key() == "anger"</c>. Consumers that
    /// need namespaced production-substrate keys construct them by combining
    /// the task prefix with this key, e.g. <c>$"ei.{axis.Key()}"</c> or
    /// <c>$"ec.{axis.Key()}"</c>.
    /// </summary>
    public static string Key(this EmotionAxis axis) =>
        axis.ToString().ToLowerInvariant();
}
