using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Deterministic stub implementation of <see cref="IEmotionalSubstrateScorer"/>
/// for unit tests and dev environments where the production EmoLLaMA
/// scorer isn't available or desired.
///
/// <para>
/// Output is hashed-deterministic from the input text. The same input
/// always returns the same vector. Useful for tests that need predictable
/// substrate values without mocking the whole interface, and for local
/// dev where you want the runtime to function end-to-end without a model
/// dependency.
/// </para>
///
/// <para>
/// Schema mimics the eventual production schema introduced in PR-2:
/// 4 namespaced EI-reg gradient axes (<c>ei.anger</c>, <c>ei.fear</c>,
/// <c>ei.joy</c>, <c>ei.sadness</c> in [0.0, 1.0]), 11 namespaced E-c
/// multilabel-presence axes (<c>ec.anger</c>, <c>ec.anticipation</c>, etc.
/// in {0.0, 1.0}), and 1 namespaced V-reg dimensional axis
/// (<c>dim.valence</c> in [0.0, 1.0]). The schema identifier marks the
/// stub explicitly so consumers can distinguish stub data from production
/// data if needed.
/// </para>
///
/// <para>
/// NOT a meaningful classifier. The values reflect hash bits of the input,
/// not actual emotion content. Production code uses
/// <c>EmoLLamaSubstrateScorer</c>, added in PR-2.
/// </para>
/// </summary>
public sealed class StubSubstrateScorer : IEmotionalSubstrateScorer
{
    /// <summary>
    /// Stable schema identifier for the stub. Distinct from any real
    /// model schema so consumers and projectors can tell stub data apart
    /// from production data if it matters.
    /// </summary>
    public const string SchemaId = "stub-substrate-v1";

    private static readonly string[] EcEmotions = new[]
    {
        "anger", "anticipation", "disgust", "fear", "joy", "love",
        "optimism", "pessimism", "sadness", "surprise", "trust",
    };

    public Task<EmotionVector> ScoreAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(BuildNeutralVector());
        }

        var hash = unchecked((uint)text.GetHashCode());
        var components = new Dictionary<string, double>();

        // EI-reg gradient values for the four EI-reg emotions
        components[$"ei.{EmotionAxis.Anger.Key()}"]   = ((hash >>  0) & 0xFF) / 255.0;
        components[$"ei.{EmotionAxis.Fear.Key()}"]    = ((hash >>  8) & 0xFF) / 255.0;
        components[$"ei.{EmotionAxis.Joy.Key()}"]     = ((hash >> 16) & 0xFF) / 255.0;
        components[$"ei.{EmotionAxis.Sadness.Key()}"] = ((hash >> 24) & 0xFF) / 255.0;

        // V-reg dimensional value
        components[$"dim.{DimensionAxis.Valence.Key()}"] =
            ((hash ^ 0x5A5A5A5Au) & 0xFF) / 255.0;

        // E-c binary multilabel values (0.0 or 1.0) derived from hash bits
        var ecHash = unchecked((uint)(text.Length * 2654435761u) ^ hash);
        for (var i = 0; i < EcEmotions.Length; i++)
        {
            var bit = (ecHash >> i) & 1u;
            components[$"ec.{EcEmotions[i]}"] = bit == 1 ? 1.0 : 0.0;
        }

        return Task.FromResult(new EmotionVector(components, SchemaId));
    }

    private static EmotionVector BuildNeutralVector()
    {
        var components = new Dictionary<string, double>();

        components[$"ei.{EmotionAxis.Anger.Key()}"]   = 0.0;
        components[$"ei.{EmotionAxis.Fear.Key()}"]    = 0.0;
        components[$"ei.{EmotionAxis.Joy.Key()}"]     = 0.0;
        components[$"ei.{EmotionAxis.Sadness.Key()}"] = 0.0;
        components[$"dim.{DimensionAxis.Valence.Key()}"] = 0.5;

        foreach (var emo in EcEmotions)
        {
            components[$"ec.{emo}"] = 0.0;
        }

        return new EmotionVector(components, SchemaId);
    }
}
