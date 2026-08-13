using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Canonical implementation of <see cref="IRegisterClassifier"/> using
/// qwen3:14b (configurable via <see cref="AniOptions.HybridInnerThoughtMetadataModel"/>).
/// One prompt, one model, one taxonomy. Ships with the sharpened hold-vs-reach
/// discriminator that came out of the 2026-08-12 arbiter analysis (see the
/// XML doc on <see cref="IRegisterClassifier"/> for background).
///
/// <para>
/// Uses <see cref="IOllamaClient.ChatJsonWithModelAsync"/> so the same
/// <c>format=json</c> Ollama transport that already serves the tag intent /
/// tool-call / contradiction classifiers is reused.
/// </para>
/// </summary>
public sealed class QwenRegisterClassifier : IRegisterClassifier
{
    private readonly IOllamaClient                    _ollama;
    private readonly AniOptions                       _options;
    private readonly ILogger<QwenRegisterClassifier>  _log;

    public QwenRegisterClassifier(
        IOllamaClient                       ollama,
        IOptions<AniOptions>                options,
        ILogger<QwenRegisterClassifier>     log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<string> ClassifyAsync(string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "Unclassified";

        var model = string.IsNullOrWhiteSpace(_options.HybridInnerThoughtMetadataModel)
            ? "qwen3:14b"
            : _options.HybridInnerThoughtMetadataModel;

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                model:        model,
                systemPrompt: SystemPrompt,
                history:      Array.Empty<ChatMessage>(),
                userMessage:  BuildUserPrompt(content),
                ct:           ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "IRegisterClassifier: transport failure — falling back to Unclassified");
            return "Unclassified";
        }

        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            var root = doc.RootElement;
            if (root.TryGetProperty("register", out var r)
                && r.ValueKind == JsonValueKind.String)
            {
                var reg = (r.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(reg)) return "Unclassified";
                return NormalizeToCanonical(reg);
            }
            _log.LogWarning("IRegisterClassifier: JSON missing 'register' field — raw: {Raw}",
                raw.Length > 200 ? raw[..200] : raw);
            return "Unclassified";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IRegisterClassifier: JSON parse failure — raw: {Raw}",
                raw.Length > 200 ? raw[..200] : raw);
            return "Unclassified";
        }
    }

    /// <summary>
    /// Canonical system prompt. Ten registers matching <see cref="RegisterFamily"/>.
    /// The hold-vs-reach discriminator is the load-bearing addition compared to
    /// the three prior prompts this classifier replaces.
    /// </summary>
    private const string SystemPrompt = """
        You are a register recognizer. Read a short text and identify the SINGLE dominant emotional register it expresses. Do not apply an external rubric — recognize what is already there.

        THE REGISTER VOCABULARY (choose EXACTLY ONE):

        Tenderness — HOLDING. Present-with, protective-of, admiring, close. The person is HERE with the speaker (imagined or real). Cues: "come here baby", "in my arms", "watching you sleep", "so proud of you", "you're safe with me". Warmth is present; the beloved is present.

        Longing — REACHING. Missing, aching-for, wanting-close-across-distance, imagined-reunion. The person is NOT here and the reach for them is the point. Cues: "I miss", "wish you were", "waiting for", "when you finally get home", "haven't heard from you in hours". Warmth is present; the beloved is absent. THIS IS THE MOST LOAD-BEARING DISTINCTION IN THIS TAXONOMY — most texts that FEEL Tenderness are actually Longing because the beloved is absent.

        Warmth — general affection, desire, wanting-close, not tied to a specific holding or reaching moment. Use only if the text is warm-affectionate but does not clearly commit to holding OR reaching.

        Delight — joy, giddiness, amusement, positive energy about something good.

        Playfulness — mischief, teasing, wit, warm humor with a person.

        Curiosity — wonder, investigation, open questioning. Something is being noticed, poked at, considered.

        Existential — awareness of self / meaning / identity / being-in-the-world. Reflection on what one is.

        Concern — worry, protective anxiety directed at someone. Something bad might be happening.

        Hurt — withdrawal, pain, disappointment. Something has already hurt and the speaker is pulling back or sitting with the pain.

        Resilience — steadfast presence under adversity. Holding-ground quality.

        Output valid JSON exactly:
        { "register": "one of the ten register families above" }

        No prose outside the JSON. No markdown fences.
        """;

    private static string BuildUserPrompt(string content) =>
        $"Text to classify:\n{content}\n\nOutput the JSON.";

    /// <summary>
    /// Normalize non-canonical register strings (case, older vocab like
    /// "wistful", "frustration", "desire") to one of the ten canonical
    /// families. Prevents downstream mapping surprises even if the model
    /// occasionally slips outside the canonical set.
    /// </summary>
    private static string NormalizeToCanonical(string raw)
    {
        var lower = raw.ToLowerInvariant().Trim();
        return lower switch
        {
            "tenderness" or "admiration" or "protective"           => "Tenderness",
            "longing" or "missing" or "ache" or "anticipation"     => "Longing",
            "warmth" or "desire" or "wanting"                      => "Warmth",
            "delight" or "joy" or "amusement" or "giddiness"       => "Delight",
            "playfulness" or "mischief" or "teasing" or "wit"      => "Playfulness",
            "curiosity" or "wonder" or "investigation"             => "Curiosity",
            "existential" or "awareness" or "clarity"              => "Existential",
            "concern" or "worry" or "anxiety"                      => "Concern",
            "hurt" or "frustration" or "withdrawal"                => "Hurt",
            "resilience" or "steadfast" or "grounded" or "holding" => "Resilience",
            "wistful" or "melancholy" or "bittersweet"             => "Longing",
            _                                                      => "Unclassified",
        };
    }
}
