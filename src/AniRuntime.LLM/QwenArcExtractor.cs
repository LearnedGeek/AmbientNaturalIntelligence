using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// F-4 Phase 7 (2026-10-23) — canonical implementation of
/// <see cref="IArcExtractor"/> using qwen3:14b. One prompt, strict
/// JSON output, fail-open on transport/parse errors.
///
/// <para>
/// Prompt anchors the taxonomy in-prompt: an arc is a multi-week
/// narrative thread (project, book, pending interaction), not a mood
/// or a sensation. Extractor returns at most 5 mentions per call; the
/// mutator applies them to character state under the usual discipline.
/// </para>
/// </summary>
public sealed class QwenArcExtractor : IArcExtractor
{
    private readonly IOllamaClient                 _ollama;
    private readonly AniOptions                    _options;
    private readonly ILogger<QwenArcExtractor>     _log;

    public QwenArcExtractor(
        IOllamaClient               ollama,
        IOptions<AniOptions>        options,
        ILogger<QwenArcExtractor>   log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<IReadOnlyList<ArcMention>> ExtractAsync(
        IReadOnlyList<MemoryRecord> recentThoughts,
        IReadOnlyList<string>       existingArcNames,
        CancellationToken           ct)
    {
        if (recentThoughts is null || recentThoughts.Count == 0)
            return Array.Empty<ArcMention>();

        var model = string.IsNullOrWhiteSpace(_options.ArcExtractorModel)
            ? "qwen3:14b"
            : _options.ArcExtractorModel;

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                model:        model,
                systemPrompt: SystemPrompt,
                history:      Array.Empty<ChatMessage>(),
                userMessage:  BuildUserPrompt(recentThoughts, existingArcNames),
                ct:           ct,
                keepAlive:    "0")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "F4_ARC_EXTRACT transport failure — returning empty mention list");
            return Array.Empty<ArcMention>();
        }

        return ParseMentions(raw, _log);
    }

    /// <summary>Internal so unit tests pin the parse surface without an Ollama hop.</summary>
    internal static IReadOnlyList<ArcMention> ParseMentions(string raw, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<ArcMention>();
        try
        {
            using var doc = JsonDocument.Parse(raw.Trim());
            if (!doc.RootElement.TryGetProperty("arcs", out var arcs)
                || arcs.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ArcMention>();
            }

            var result = new List<ArcMention>();
            foreach (var el in arcs.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? (n.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var summary = el.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                    ? (s.GetString() ?? string.Empty).Trim()
                    : string.Empty;

                string? match = null;
                if (el.TryGetProperty("existing_match", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    var v = (m.GetString() ?? string.Empty).Trim();
                    if (v.Length > 0) match = v;
                }

                var isCompletion = el.TryGetProperty("is_completion", out var c)
                                   && c.ValueKind is JsonValueKind.True;

                result.Add(new ArcMention(name, summary, match, isCompletion));
                if (result.Count >= 5) break;
            }
            return result;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex,
                "F4_ARC_EXTRACT JSON parse failure — raw: {Raw}",
                raw.Length > 200 ? raw[..200] : raw);
            return Array.Empty<ArcMention>();
        }
    }

    private const string SystemPrompt = """
        You are an arc extractor.

        INPUT: a short list of someone's recent inner thoughts, plus the names of narrative arcs she is already tracking.

        TASK: identify multi-week narrative threads referenced in the thoughts. A valid arc is a project, a book-in-progress, a pending interaction, a creative work, or a decision in motion — something with a beginning, a middle, and (eventually) an end. NOT valid: moods, sensations, abstract preferences, one-off observations.

        For each arc you find:
          - "name": short canonical name (e.g. "the shelf", "Peru cookbook", "conversation with Lila")
          - "summary": one sentence capturing the current state ("almost done", "stuck on the ending", "waiting for her to text back")
          - "existing_match": if this is a reference to an arc in the EXISTING list, name that exact string. Otherwise null.
          - "is_completion": true only if the thought explicitly frames the arc as finished ("finally finished", "done with", "it's over"). Default false.

        Return at most 5 arcs. Prefer "existing_match" over a near-duplicate new name.

        FORMAT: strict JSON of the form:
        {"arcs": [{"name": "...", "summary": "...", "existing_match": "..." or null, "is_completion": false}, ...]}

        No prose outside the JSON. No markdown fences. If no arcs are present, return {"arcs": []}.
        """;

    private static string BuildUserPrompt(
        IReadOnlyList<MemoryRecord> thoughts,
        IReadOnlyList<string>       existingArcNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("EXISTING ARCS she is tracking:");
        if (existingArcNames is null || existingArcNames.Count == 0)
        {
            sb.AppendLine("  (none yet)");
        }
        else
        {
            foreach (var a in existingArcNames)
                sb.AppendLine($"  - {a}");
        }
        sb.AppendLine();
        sb.AppendLine("RECENT INNER THOUGHTS:");
        for (var i = 0; i < thoughts.Count; i++)
        {
            var content = thoughts[i].Content ?? string.Empty;
            if (content.Length > 400) content = content[..400] + "…";
            sb.AppendLine($"  [{i + 1}] {content}");
        }
        sb.AppendLine();
        sb.AppendLine("Extract arc mentions per the taxonomy above. Return JSON only.");
        return sb.ToString();
    }
}
