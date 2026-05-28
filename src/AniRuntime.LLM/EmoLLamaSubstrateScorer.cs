using System.Globalization;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Contribution 9 PR-3 (Issue #68, May 28, 2026) — production implementation
/// of <see cref="IEmotionalSubstrateScorer"/> backed by EmoLLaMA-7B (the
/// task-tuned, non-chat variant from Liu et al. 2024 / KDD '24) served via
/// Ollama's <c>/api/generate</c> endpoint with the EmoLLM paper's verbatim
/// instruction templates.
///
/// <para>
/// Architecture finding (May 28, 2026): the chat-tuned variant
/// (<c>Emollama-chat-7b</c>) is a companion model that generates empathic
/// narrative responses, not structured emotion scores — five prompt-shape
/// probes returned story content; with Ollama's <c>format=json</c> mode it
/// returned literally <c>{ }</c> on every call. The task-tuned variant
/// (<c>Emollama-7b</c>) does emit clean numeric and multi-label task
/// outputs, but only when called via <c>/api/generate</c> with the paper's
/// raw <c>Human:/Task:/Text:/Emotion:/Intensity Score:</c> template. The
/// chat-API role split silently breaks the trained behavior.
/// </para>
///
/// <para>
/// Six calls per scoring event:
/// </para>
/// <list type="bullet">
/// <item>4 × EI-reg, one per emotion (anger, fear, joy, sadness) → numeric
///     intensity in [0, 1] → <c>ei.{emotion}</c>.</item>
/// <item>1 × E-c multi-label → comma-separated list of present emotions →
///     <c>ec.{emotion}</c> = 1.0 if present in list else 0.0.</item>
/// <item>1 × V-reg → numeric valence in [-1, +1] (Russell circumplex
///     convention, NOT [0, 1]) → rescaled to [0, 1] for <c>dim.valence</c>.</item>
/// </list>
///
/// <para>
/// Resilience contract: when a single sub-call fails or returns unparseable
/// output, the affected axis falls back to neutral and the rest of the
/// vector is still populated from the calls that did succeed. Whole-call
/// catastrophic failure (e.g. Ollama unreachable) returns the neutral
/// vector. Throwing would propagate model availability concerns into every
/// cognitive cycle.
/// </para>
/// </summary>
public sealed class EmoLLamaSubstrateScorer : IEmotionalSubstrateScorer
{
    /// <summary>
    /// Stable schema identifier for the EmoLLaMA-7B substrate. Distinct
    /// from <c>"stub-substrate-v1"</c> so consumers can tell production
    /// data apart from stub data if it matters. PR-2's
    /// <c>"emollama-chat-7b-v1"</c> is retired with this PR — chat variant
    /// never produced usable output and existing log entries with that
    /// schema id should be treated as non-data.
    /// </summary>
    public const string SchemaId = "emollama-7b-v1";

    private static readonly string[] EiEmotions = new[]
    {
        "anger", "fear", "joy", "sadness",
    };

    private static readonly string[] EcEmotions = new[]
    {
        "anger", "anticipation", "disgust", "fear", "joy", "love",
        "optimism", "pessimism", "sadness", "surprise", "trust",
    };

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

        var components = new Dictionary<string, double>();

        // Seed every axis with neutral defaults so a partial failure still
        // produces a full-schema vector. Individual sub-calls overwrite
        // their respective axes if they succeed.
        foreach (var e in EiEmotions) components[$"ei.{e}"] = 0.0;
        foreach (var e in EcEmotions) components[$"ec.{e}"] = 0.0;
        components["dim.valence"] = 0.5;

        try
        {
            // EI-reg: 4 calls, one per categorical emotion
            foreach (var emotion in EiEmotions)
            {
                var intensity = await CallEiRegAsync(text, emotion, ct).ConfigureAwait(false);
                if (intensity.HasValue)
                    components[$"ei.{emotion}"] = intensity.Value;
            }

            // E-c: 1 call, multi-label
            var present = await CallEcAsync(text, ct).ConfigureAwait(false);
            if (present is not null)
            {
                foreach (var emotion in EcEmotions)
                    components[$"ec.{emotion}"] = present.Contains(emotion) ? 1.0 : 0.0;
            }

            // V-reg: 1 call, [-1, +1] rescaled to [0, 1]
            var valence = await CallVRegAsync(text, ct).ConfigureAwait(false);
            if (valence.HasValue)
                components["dim.valence"] = Math.Clamp((valence.Value + 1.0) / 2.0, 0.0, 1.0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "EmoLLaMA substrate scoring failed mid-batch; returning partial vector");
        }

        return new EmotionVector(components, SchemaId);
    }

    private async Task<double?> CallEiRegAsync(string text, string emotion, CancellationToken ct)
    {
        var prompt =
            "Human:\n" +
            "Task: Assign a numerical value between 0 (least E) and 1 (most E) to represent the intensity of emotion E expressed in the text.\n" +
            $"Text: {text}\n" +
            $"Emotion: {emotion}\n" +
            "Intensity Score:";

        var raw = await SafeGenerateAsync(prompt, maxTokens: 8, ct).ConfigureAwait(false);
        if (raw is null) return null;

        var parsed = ParseLeadingNumber(raw);
        if (!parsed.HasValue)
        {
            _log.LogDebug("EI-reg {Emotion}: unparseable response {Raw}", emotion, Truncate(raw, 80));
            return null;
        }
        return Math.Clamp(parsed.Value, 0.0, 1.0);
    }

    private async Task<HashSet<string>?> CallEcAsync(string text, CancellationToken ct)
    {
        var prompt =
            "Human:\n" +
            "Task: Categorize the text's emotional tone as either 'neutral or no emotion' or identify the presence of one or more of the given emotions (anger, anticipation, disgust, fear, joy, love, optimism, pessimism, sadness, surprise, trust).\n" +
            $"Text: {text}\n" +
            "This text contains emotions:";

        var raw = await SafeGenerateAsync(prompt, maxTokens: 48, ct).ConfigureAwait(false);
        if (raw is null) return null;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lower = raw.ToLowerInvariant();
        foreach (var emotion in EcEmotions)
        {
            // Word-boundary check so "anger" doesn't substring-match "danger" etc.
            var idx = 0;
            while ((idx = lower.IndexOf(emotion, idx, StringComparison.Ordinal)) >= 0)
            {
                var before = idx == 0 || !char.IsLetter(lower[idx - 1]);
                var after = idx + emotion.Length == lower.Length
                            || !char.IsLetter(lower[idx + emotion.Length]);
                if (before && after)
                {
                    present.Add(emotion);
                    break;
                }
                idx += emotion.Length;
            }
        }
        return present;
    }

    private async Task<double?> CallVRegAsync(string text, CancellationToken ct)
    {
        // V-reg per the EmoLLM paper conventions — output is on Russell's
        // [-1, +1] valence scale, NOT [0, 1]. Rescaling happens at the
        // caller so this method preserves the model's native semantics.
        var prompt =
            "Human:\n" +
            "Task: Assign a numerical value between 0 (most negative) and 1 (most positive) to represent the valence (sentiment) of the text.\n" +
            $"Text: {text}\n" +
            "Valence Score:";

        var raw = await SafeGenerateAsync(prompt, maxTokens: 8, ct).ConfigureAwait(false);
        if (raw is null) return null;

        var parsed = ParseLeadingNumber(raw);
        if (!parsed.HasValue)
        {
            _log.LogDebug("V-reg: unparseable response {Raw}", Truncate(raw, 80));
            return null;
        }
        // Empirical: the model emits values in [-1, +1] despite the
        // template asking for [0, 1] — match the paper's training scale.
        // Clamp to the [-1, +1] range before the caller rescales.
        return Math.Clamp(parsed.Value, -1.0, 1.0);
    }

    private async Task<string?> SafeGenerateAsync(string prompt, int maxTokens, CancellationToken ct)
    {
        try
        {
            return await _ollama.GenerateAsync(
                _opts.SubstrateModel, prompt, ct,
                temperature: 0.0f, maxTokens: maxTokens).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "EmoLLaMA /api/generate call failed");
            return null;
        }
    }

    private static double? ParseLeadingNumber(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Find the first numeric token (handles leading whitespace, optional
        // sign, decimal point). Model output looks like " 0.83" or " -0.32".
        var span = raw.AsSpan().Trim();
        var len = 0;
        if (len < span.Length && (span[len] == '+' || span[len] == '-')) len++;
        var sawDigit = false;
        var sawDot = false;
        while (len < span.Length)
        {
            var c = span[len];
            if (char.IsDigit(c)) { sawDigit = true; len++; continue; }
            if (c == '.' && !sawDot) { sawDot = true; len++; continue; }
            break;
        }
        if (!sawDigit) return null;

        if (double.TryParse(span[..len], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return null;
    }

    private static EmotionVector BuildNeutralVector()
    {
        var components = new Dictionary<string, double>();
        foreach (var e in EiEmotions) components[$"ei.{e}"] = 0.0;
        foreach (var e in EcEmotions) components[$"ec.{e}"] = 0.0;
        components["dim.valence"] = 0.5;
        return new EmotionVector(components, SchemaId);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
