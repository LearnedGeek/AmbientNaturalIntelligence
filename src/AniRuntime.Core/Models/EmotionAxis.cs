namespace AniRuntime.Core.Models;

/// <summary>
/// Contribution 9 (Issue #68) — the *currently-known* emotion axes
/// emitted by the production substrate scorer (EmoLLaMA-chat-7B
/// EI-reg + V-reg, per Liu et al. 2024 / arXiv 2401.08508 / KDD '24).
///
/// <para>
/// This enum is the consumer-side vocabulary for type-safe access to
/// standard axes — <c>vec.Get(EmotionAxis.Anger.Key())</c>. Storage
/// is dictionary-keyed (see <see cref="EmotionVector.Components"/>),
/// so future-model schemas can introduce axes not enumerated here
/// (e.g. <c>"severity"</c>, <c>"arousal"</c>, <c>"dominance"</c>) without
/// breaking the type. Consumers that need those future axes access them
/// via raw-string <see cref="EmotionVector.Get(string)"/>.
/// </para>
///
/// <para>
/// Stable key strings are produced by <see cref="EmotionAxisExtensions.Key"/>
/// — guaranteed lowercase, no whitespace, schema-stable across renames
/// of enum members (enum members can be renamed in code; the storage
/// key is the string form).
/// </para>
/// </summary>
public enum EmotionAxis
{
    Anger,
    Fear,
    Joy,
    Sadness,
    Valence,
}

public static class EmotionAxisExtensions
{
    /// <summary>
    /// Stable, lowercase, schema-portable key for storage and dictionary
    /// lookup. <c>EmotionAxis.Anger.Key() == "anger"</c>.
    /// </summary>
    public static string Key(this EmotionAxis axis) =>
        axis.ToString().ToLowerInvariant();
}
