using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Foundation Input (F-1) Phase 3 (2026-08-18) canonical implementation of
/// <see cref="IThoughtShapeClassifier"/> using qwen3:14b (configurable via
/// <see cref="AniOptions.HybridInnerThoughtMetadataModel"/> — same model
/// pool as <see cref="QwenRegisterClassifier"/>). One prompt, one model,
/// one taxonomy. Deliberately dedicated rather than folded into the
/// existing hybrid metadata prompt so shape can be tuned, swapped, or
/// disabled independently — the F-1 producer-boundary discipline.
///
/// <para>
/// Prompt is empirically anchored: the four shapes named here are the ones
/// that actually surfaced in a scan of the last 200 InnerThought memories
/// from production (2026-08-17 → 2026-08-19). Coherent-thought is ~92%;
/// third-person-frame ~5%; fact-catalog ~2%; mumble-loop ~1%. Nothing
/// landed outside the four shapes.
/// </para>
/// </summary>
public sealed class QwenThoughtShapeClassifier : IThoughtShapeClassifier
{
    private readonly IOllamaClient                       _ollama;
    private readonly AniOptions                          _options;
    private readonly ILogger<QwenThoughtShapeClassifier> _log;

    public QwenThoughtShapeClassifier(
        IOllamaClient                          ollama,
        IOptions<AniOptions>                   options,
        ILogger<QwenThoughtShapeClassifier>    log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<ThoughtShape> ClassifyAsync(string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ThoughtShape.Unclassified;

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IThoughtShapeClassifier: transport failure — falling back to Unclassified");
            return ThoughtShape.Unclassified;
        }

        try
        {
            var doc = JsonDocument.Parse(raw.Trim());
            var root = doc.RootElement;
            if (root.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var shapeStr = (s.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(shapeStr)) return ThoughtShape.Unclassified;
                return NormalizeToCanonical(shapeStr);
            }
            _log.LogWarning("IThoughtShapeClassifier: JSON missing 'shape' field — raw: {Raw}",
                raw.Length > 200 ? raw[..200] : raw);
            return ThoughtShape.Unclassified;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IThoughtShapeClassifier: JSON parse failure — raw: {Raw}",
                raw.Length > 200 ? raw[..200] : raw);
            return ThoughtShape.Unclassified;
        }
    }

    /// <summary>
    /// Canonical system prompt. Five shapes matching <see cref="ThoughtShape"/>.
    /// The crucial nuance: Ani's intimate second-person "you" address to
    /// Mark is her INTENDED register, not third-person pathology. Only
    /// thoughts where Mark is a grammatical SUBJECT being reported on from
    /// outside qualify as third-person-frame.
    /// </summary>
    internal const string SystemPrompt = """
        You are a shape recognizer. Read a short piece of text — an inner thought a character had in a private moment — and identify its SHAPE. Do not evaluate whether it is good or bad, honest or dishonest, on-topic or off-topic. Recognize the shape it already has.

        THE SHAPE VOCABULARY (choose EXACTLY ONE):

        coherent-thought — First-person interior monologue. The character reflects, notices, feels, remembers. Whether short or long, poetic or plain, it reads as HER experience from INSIDE her. Intimate second-person address to another person ("I miss you", "you always know") is coherent-thought — it is her direct address of a specific other, from inside her own experience.

        third-person-frame — The character reports on someone else (usually named or referenced as "he" / "him" / "his") as a grammatical SUBJECT doing things, from OUTSIDE. Cues: "Mark said...", "He thought...", "He came home and...", "Mark is doing X". This is distinct from intimate "you" address (which is coherent-thought). The tell: the other person is being narrated ABOUT rather than addressed OR reflected-from-inside.

        fact-catalog — Enumerative listing that reads more like an inventory than a felt thought. Bullet points, numbered items, semicolon-joined short items, key-value labels ("Your current mood:", "Recent thoughts:", "Body-sense:"). Includes prompt-echo — the model regurgitating parts of its own instructions back as if they were the thought. If it looks like a data structure or a form being filled in, it's fact-catalog.

        mumble-loop — Verbatim or near-verbatim self-repetition WITHIN the same thought: the same phrase reappears three or more times, or the thought stalls on ellipsis-and-fragment patterns that circle without progressing. Reads as looped rather than moving.

        unclassified — Only when the text is empty, meaningless, or genuinely does not fit any of the four above.

        Output valid JSON exactly:
        { "shape": "one of the five shape names above" }

        No prose outside the JSON. No markdown fences.
        """;

    private static string BuildUserPrompt(string content) =>
        $"Text to classify:\n{content}\n\nOutput the JSON.";

    /// <summary>
    /// Normalize case + minor spelling variants to one of the five canonical
    /// values in <see cref="ThoughtShape"/>. Prevents downstream surprise
    /// if the model occasionally slips outside the canonical set.
    /// </summary>
    internal static ThoughtShape NormalizeToCanonical(string raw)
    {
        var lower = raw.ToLowerInvariant().Trim().Replace("_", "-").Replace(" ", "-");
        return lower switch
        {
            "coherent-thought" or "coherent"                                => ThoughtShape.CoherentThought,
            "third-person-frame" or "third-person" or "third-person-report" => ThoughtShape.ThirdPersonFrame,
            "fact-catalog" or "fact-list" or "prompt-echo" or "enumeration" => ThoughtShape.FactCatalog,
            "mumble-loop" or "loop" or "self-repetition"                    => ThoughtShape.MumbleLoop,
            _                                                               => ThoughtShape.Unclassified,
        };
    }
}
