using System.Collections.Generic;

namespace AniRuntime.Core.Models;

/// <summary>
/// Contribution 9 (Issue #68, 2026-05-27) — continuous-vector emotion
/// measurement produced by <see cref="Interfaces.IEmotionalSubstrateScorer"/>.
/// Single substrate of truth for downstream consumers; replaces the two
/// divergent Ollama-via-prompt discrete classifiers flagged in #66.
///
/// <para>
/// Schema-flexible by design (per Mark's 5/27 review): axes are carried
/// as data in <see cref="Components"/>, not baked into the type structure.
/// EmoLLaMA-chat-7B contributes <c>anger / fear / joy / sadness / valence</c>;
/// future models can contribute additional axes (<c>arousal</c>,
/// <c>severity</c>, <c>dominance</c>) without breaking this type or any
/// consumer that doesn't need the new axes.
/// </para>
///
/// <para>
/// <see cref="MeasurementSchema"/> is a stable identifier for the
/// scorer that produced the vector (e.g. <c>"emollama-chat-7b-v1"</c>).
/// Consumers use it to reason about substrate compatibility across model
/// swaps; <see cref="EmotionVectorProjector"/> uses it to dispatch
/// schema-specific projection rules.
/// </para>
/// </summary>
public sealed record EmotionVector(
    IReadOnlyDictionary<string, double> Components,
    string MeasurementSchema)
{
    /// <summary>
    /// Empty vector — no axes present. Useful as a placeholder for
    /// empty/whitespace input or for tests. Schema is empty string;
    /// consumers must check schema before assuming any axis is present.
    /// </summary>
    public static EmotionVector Empty { get; } =
        new(new Dictionary<string, double>(), string.Empty);

    /// <summary>
    /// Retrieve a single axis value. Returns <c>null</c> if the axis
    /// is not present in this vector's schema. Consumers MUST handle
    /// the null case — silent default-to-zero would mask schema
    /// mismatches.
    /// </summary>
    public double? Get(string axis) =>
        Components.TryGetValue(axis, out var v) ? v : null;

    /// <summary>
    /// Convenience overload for known categorical-emotion axes via the
    /// <see cref="EmotionAxis"/> enum. Equivalent to
    /// <c>Get(axis.Key())</c>. Note this accesses the bare-key form
    /// (e.g. <c>"anger"</c>); consumers that need namespaced
    /// production-substrate keys (<c>"ei.anger"</c>, <c>"ec.anger"</c>) must
    /// use the raw-string <see cref="Get(string)"/> overload directly.
    /// </summary>
    public double? Get(EmotionAxis axis) => Get(axis.Key());

    /// <summary>
    /// Convenience overload for known dimensional axes via the
    /// <see cref="DimensionAxis"/> enum. Equivalent to
    /// <c>Get(axis.Key())</c>. Same bare-vs-namespaced caveat as the
    /// <see cref="EmotionAxis"/> overload applies.
    /// </summary>
    public double? Get(DimensionAxis axis) => Get(axis.Key());
}
