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
/// Output is hashed-deterministic from the input text — the same input
/// always returns the same vector. Useful for tests that need predictable
/// substrate values without mocking the whole interface, and for local
/// dev where you want the runtime to function end-to-end without a model
/// dependency.
/// </para>
///
/// <para>
/// NOT a meaningful classifier — the values reflect hash bits of the
/// input, not actual emotion content. Production code must use
/// <c>EmoLLamaSubstrateScorer</c> (added in PR-2).
/// </para>
/// </summary>
public sealed class StubSubstrateScorer : IEmotionalSubstrateScorer
{
    /// <summary>
    /// Stable schema identifier for the stub. Distinct from any real
    /// model schema so consumers / projectors can tell stub data apart
    /// from production data if it matters.
    /// </summary>
    public const string SchemaId = "stub-substrate-v1";

    public Task<EmotionVector> ScoreAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new EmotionVector(
                new Dictionary<string, double>
                {
                    [EmotionAxis.Anger.Key()]   = 0.0,
                    [EmotionAxis.Fear.Key()]    = 0.0,
                    [EmotionAxis.Joy.Key()]     = 0.0,
                    [EmotionAxis.Sadness.Key()] = 0.0,
                    [EmotionAxis.Valence.Key()] = 0.5,
                },
                SchemaId));
        }

        // Hash-deterministic per-axis values. Stable across runs; same
        // input → same vector. Values land in [0.0, 1.0].
        var hash = unchecked((uint)text.GetHashCode());
        var components = new Dictionary<string, double>
        {
            [EmotionAxis.Anger.Key()]   = ((hash >>  0) & 0xFF) / 255.0,
            [EmotionAxis.Fear.Key()]    = ((hash >>  8) & 0xFF) / 255.0,
            [EmotionAxis.Joy.Key()]     = ((hash >> 16) & 0xFF) / 255.0,
            [EmotionAxis.Sadness.Key()] = ((hash >> 24) & 0xFF) / 255.0,
            [EmotionAxis.Valence.Key()] = ((hash ^ 0x5A5A5A5Au) & 0xFF) / 255.0,
        };
        return Task.FromResult(new EmotionVector(components, SchemaId));
    }
}
