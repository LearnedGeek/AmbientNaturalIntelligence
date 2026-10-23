using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// F-4 Phase 4a.1b (2026-10-23) — canonical implementation of
/// <see cref="ISignificanceClassifier"/> using qwen3:14b (configurable via
/// <see cref="AniOptions.SignificanceClassifierModel"/>). Same model and
/// transport shape as <see cref="QwenRegisterClassifier"/>; fail-open on
/// transport/parse errors, returning
/// <see cref="AniOptions.SignificanceDefault"/>.
///
/// <para>
/// Taxonomy pinned in-prompt: war / mass violence / major political
/// transition rank ≥ 0.7; recipes / celebrity gossip / minor sports rank
/// ≤ 0.3; routine news lands near 0.5. See the system prompt below for
/// the exact framing. Classification runs once per RSS item at ingest;
/// batch amortization is a future optimization if cost warrants.
/// </para>
/// </summary>
public sealed class QwenSignificanceClassifier : ISignificanceClassifier
{
    private readonly IOllamaClient                        _ollama;
    private readonly AniOptions                           _options;
    private readonly ILogger<QwenSignificanceClassifier>  _log;

    public QwenSignificanceClassifier(
        IOllamaClient                           ollama,
        IOptions<AniOptions>                    options,
        ILogger<QwenSignificanceClassifier>     log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<float> ClassifyAsync(string eventSummary, CancellationToken ct)
    {
        var fallback = Math.Clamp(_options.SignificanceDefault, 0f, 1f);

        if (string.IsNullOrWhiteSpace(eventSummary))
            return fallback;

        var model = string.IsNullOrWhiteSpace(_options.SignificanceClassifierModel)
            ? "qwen3:14b"
            : _options.SignificanceClassifierModel;

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                model:        model,
                systemPrompt: SystemPrompt,
                history:      Array.Empty<ChatMessage>(),
                userMessage:  BuildUserPrompt(eventSummary),
                ct:           ct,
                keepAlive:    "0")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "F4_SIGNIFICANCE_CLASSIFY transport failure — falling back to default {Default}",
                fallback);
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        return ParseSignificance(raw, fallback, _log);
    }

    /// <summary>
    /// Internal so unit tests exercise the parse surface without an Ollama hop.
    /// Clamps to [0.0, 1.0]; returns <paramref name="fallback"/> on any parse
    /// miss (missing field, wrong type, invalid JSON).
    /// </summary>
    internal static float ParseSignificance(
        string raw, float fallback, ILogger log)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw.Trim());
            if (doc.RootElement.TryGetProperty("significance", out var s)
                && s.ValueKind is JsonValueKind.Number)
            {
                return Math.Clamp((float)s.GetDouble(), 0f, 1f);
            }

            log.LogWarning(
                "F4_SIGNIFICANCE_CLASSIFY JSON missing numeric 'significance' field — raw: {Raw}",
                raw.Length > 200 ? raw[..200] : raw);
            return fallback;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex,
                "F4_SIGNIFICANCE_CLASSIFY JSON parse failure — raw: {Raw}",
                raw.Length > 200 ? raw[..200] : raw);
            return fallback;
        }
    }

    private const string SystemPrompt = """
        You are a news-significance classifier.

        INPUT: a short headline, title, or summary describing one event in the world.

        TASK: output a single number between 0.0 and 1.0 reflecting the OBJECTIVE SIGNIFICANCE of this event — how consequential it is in the world at large, independent of any particular user's interests.

        TAXONOMY (anchor your scale here):
        - 0.9-1.0: existential / planetary (nuclear event, pandemic, mass-casualty war escalation, major political transition in a major power)
        - 0.7-0.8: serious world event (regional war, major policy change, mass violence, major disaster, significant economic shock)
        - 0.4-0.6: notable but not world-shaping (routine politics, business news, cultural event, moderate disaster, obituary of a public figure)
        - 0.2-0.3: minor / interest-dependent (celebrity gossip, sports results, product launches, local color, lifestyle)
        - 0.0-0.1: trivia / entertainment-only (recipes, memes, horoscopes, listicles)

        FORMAT: return STRICT JSON of exactly one field:
        {"significance": 0.0}

        No prose outside the JSON. No markdown fences.
        """;

    private static string BuildUserPrompt(string summary)
    {
        var trimmed = summary.Trim();
        if (trimmed.Length > 500) trimmed = trimmed[..500] + "…";
        return $"EVENT: {trimmed}\n\nReturn the JSON object with the significance score.";
    }
}
