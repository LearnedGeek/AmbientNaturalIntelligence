using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Contribution 9 PR-2 (Issue #68) — production implementation of
/// <see cref="IEmotionalSubstrateScorer"/> backed by EmoLLaMA-chat-7B
/// served via Ollama HTTP. Produces the 16-axis continuous-vector emotion
/// substrate (4 namespaced EI-reg gradient axes, 11 namespaced E-c
/// multilabel-presence axes, 1 namespaced V-reg dimensional axis) consumed
/// by every downstream affective projection in the runtime.
///
/// <para>
/// Replaces the two divergent Ollama-via-prompt discrete classifiers flagged
/// in #66 (per-contribution call in <c>EmotionalProcessor</c> and per-turn
/// call in <c>ClosedConversationSummarizer</c>) with one continuous-vector
/// source of truth. Migration of those consumers is out of scope for this
/// PR — they keep their existing scorers until a follow-on PR introduces the
/// projection helper.
/// </para>
///
/// <para>
/// Resilience contract: when EmoLLaMA returns malformed JSON or the call
/// fails, this scorer logs and returns the neutral vector rather than
/// throwing. Downstream consumers must already handle the schema-flexible
/// vector (axes may be missing); a neutral vector is a safe degenerate
/// case. Throwing would propagate model availability concerns into every
/// cognitive cycle, which is the exact coupling the substrate-of-truth
/// design is meant to break.
/// </para>
/// </summary>
public sealed class EmoLLamaSubstrateScorer : IEmotionalSubstrateScorer
{
    /// <summary>
    /// Stable schema identifier for the EmoLLaMA-chat-7B substrate.
    /// Distinct from <c>"stub-substrate-v1"</c> so consumers can tell
    /// production data apart from stub data if it matters.
    /// </summary>
    public const string SchemaId = "emollama-chat-7b-v1";

    private static readonly string[] EiEmotions = new[]
    {
        "anger", "fear", "joy", "sadness",
    };

    private static readonly string[] EcEmotions = new[]
    {
        "anger", "anticipation", "disgust", "fear", "joy", "love",
        "optimism", "pessimism", "sadness", "surprise", "trust",
    };

    private static readonly string[] DimFeatures = new[]
    {
        "valence",
    };

    private const string SystemPrompt = """
        You are an emotion analyzer. Score the user's input text on three tasks
        and return ONLY a JSON object with this exact shape (no prose):

        {
          "ei": {"anger": <0..1>, "fear": <0..1>, "joy": <0..1>, "sadness": <0..1>},
          "ec": {"anger": <0|1>, "anticipation": <0|1>, "disgust": <0|1>, "fear": <0|1>, "joy": <0|1>, "love": <0|1>, "optimism": <0|1>, "pessimism": <0|1>, "sadness": <0|1>, "surprise": <0|1>, "trust": <0|1>},
          "dim": {"valence": <0..1>}
        }

        Definitions:
        - "ei" — EI-reg gradient intensity in [0.0, 1.0] for each emotion.
        - "ec" — E-c binary multilabel presence (0 or 1) for each emotion.
        - "dim.valence" — V-reg dimensional valence in [0.0, 1.0] where 0.0 is
          most negative and 1.0 is most positive.

        Output ONLY the JSON object. No commentary, no markdown fences.
        """;

    private readonly IOllamaClient _ollama;
    private readonly OllamaOptions _opts;
    private readonly ILogger<EmoLLamaSubstrateScorer> _log;

    public EmoLLamaSubstrateScorer(
        IOllamaClient ollama,
        IOptions<OllamaOptions> opts,
        ILogger<EmoLLamaSubstrateScorer> log)
    {
        _ollama = ollama;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<EmotionVector> ScoreAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return BuildNeutralVector();
        }

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                _opts.SubstrateModel,
                SystemPrompt,
                Array.Empty<ChatMessage>(),
                text,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "EmoLLaMA substrate scoring failed; returning neutral vector");
            return BuildNeutralVector();
        }

        return ParseOrNeutral(raw);
    }

    private EmotionVector ParseOrNeutral(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var components = new Dictionary<string, double>();
            CopyGroup(root, "ei", EiEmotions, components, clampBinary: false);
            CopyGroup(root, "ec", EcEmotions, components, clampBinary: true);
            CopyGroup(root, "dim", DimFeatures, components, clampBinary: false);

            return new EmotionVector(components, SchemaId);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "EmoLLaMA returned malformed JSON; returning neutral vector. Raw={Raw}", Truncate(raw, 200));
            return BuildNeutralVector();
        }
    }

    private static void CopyGroup(
        JsonElement root, string group, string[] keys,
        Dictionary<string, double> components, bool clampBinary)
    {
        if (!root.TryGetProperty(group, out var section) ||
            section.ValueKind != JsonValueKind.Object)
        {
            // Group missing — fill neutrals so consumers see the full schema
            foreach (var key in keys)
                components[$"{group}.{key}"] = group == "dim" && key == "valence" ? 0.5 : 0.0;
            return;
        }

        foreach (var key in keys)
        {
            double value = group == "dim" && key == "valence" ? 0.5 : 0.0;
            if (section.TryGetProperty(key, out var prop) &&
                prop.ValueKind is JsonValueKind.Number)
            {
                value = prop.GetDouble();
            }

            if (clampBinary)
                value = value >= 0.5 ? 1.0 : 0.0;
            else
                value = Math.Clamp(value, 0.0, 1.0);

            components[$"{group}.{key}"] = value;
        }
    }

    private static EmotionVector BuildNeutralVector()
    {
        var components = new Dictionary<string, double>();

        foreach (var e in EiEmotions)
            components[$"ei.{e}"] = 0.0;

        foreach (var e in EcEmotions)
            components[$"ec.{e}"] = 0.0;

        components["dim.valence"] = 0.5;

        return new EmotionVector(components, SchemaId);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
