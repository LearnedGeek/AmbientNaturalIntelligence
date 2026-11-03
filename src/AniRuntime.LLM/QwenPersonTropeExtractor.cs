using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// F-4 Phase 6 (2026-11-02) — canonical implementation of
/// <see cref="IPersonTropeExtractor"/> using qwen3:14b. One prompt,
/// strict JSON output, fail-open on transport/parse errors.
///
/// <para>
/// The system prompt anchors two load-bearing rules:
/// (1) extract ONLY from the canonical cast handed in — never a
/// fabricated name; (2) emit tropes as short general PATTERNS, never
/// specific events. Caller filters emitted names against a derived
/// allowlist as defense-in-depth; see <see cref="IPersonTropeExtractor"/>.
/// </para>
/// </summary>
public sealed class QwenPersonTropeExtractor : IPersonTropeExtractor
{
    private readonly IOllamaClient                        _ollama;
    private readonly AniOptions                           _options;
    private readonly ILogger<QwenPersonTropeExtractor>    _log;

    public QwenPersonTropeExtractor(
        IOllamaClient                       ollama,
        IOptions<AniOptions>                options,
        ILogger<QwenPersonTropeExtractor>   log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<IReadOnlyList<PersonTrope>> ExtractAsync(
        IReadOnlyList<MemoryRecord> recentThoughts,
        IReadOnlyList<string>       canonicalCast,
        string                      primaryContactName,
        IReadOnlyList<string>       existingPeopleNames,
        CancellationToken           ct)
    {
        if (recentThoughts is null || recentThoughts.Count == 0)
            return Array.Empty<PersonTrope>();
        if (canonicalCast is null || canonicalCast.Count == 0)
            return Array.Empty<PersonTrope>();

        var model = string.IsNullOrWhiteSpace(_options.KnownPeopleExtractorModel)
            ? "qwen3:14b"
            : _options.KnownPeopleExtractorModel;

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                model:        model,
                systemPrompt: SystemPrompt,
                history:      Array.Empty<ChatMessage>(),
                userMessage:  BuildUserPrompt(recentThoughts, canonicalCast, primaryContactName, existingPeopleNames),
                ct:           ct,
                keepAlive:    "0")
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // CLAUDE.md §10: HttpClient.Timeout surfaces as TaskCanceledException
            // with a null token (not the caller's). Treat as transport fail-open.
            _log.LogWarning(
                "F4_PERSON_TROPE_EXTRACT HTTP timeout — returning empty trope list");
            return Array.Empty<PersonTrope>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || ex is InvalidOperationException)
        {
            // Narrowed per CLAUDE.md §4: canonical LLM transport/state faults
            // from the Ollama path. Any other exception propagates so a bug
            // doesn't get silently swallowed. Matches the discipline of
            // Phase 5a's TemporalContextProvider.
            _log.LogWarning(ex,
                "F4_PERSON_TROPE_EXTRACT transport failure — returning empty trope list");
            return Array.Empty<PersonTrope>();
        }

        return ParseTropes(raw, _log);
    }

    /// <summary>Internal so unit tests pin the parse surface without an Ollama hop.</summary>
    internal static IReadOnlyList<PersonTrope> ParseTropes(string raw, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<PersonTrope>();
        try
        {
            using var doc = JsonDocument.Parse(raw.Trim());
            // Guard non-object roots ("[]", "null", "\"...\"", "42"): valid
            // JSON that would throw InvalidOperationException on
            // TryGetProperty — the JsonException catch below wouldn't fire.
            // Caught by Copilot review on PR #189.
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<PersonTrope>();

            if (!doc.RootElement.TryGetProperty("people", out var people)
                || people.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<PersonTrope>();
            }

            var result = new List<PersonTrope>();
            foreach (var el in people.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? (n.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var trope = el.TryGetProperty("trope", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? string.Empty).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(trope)) continue;

                result.Add(new PersonTrope(name, trope));
                if (result.Count >= 10) break;
            }
            return result;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex,
                "F4_PERSON_TROPE_EXTRACT JSON parse failure — raw: {Raw}",
                raw.Length > 200 ? raw[..200] : raw);
            return Array.Empty<PersonTrope>();
        }
    }

    private const string SystemPrompt = """
        You are a shared-trope extractor.

        INPUT: a short list of someone's recent inner thoughts, the canonical cast of people in her world, the primary contact's name to EXCLUDE, and names of people already tracked.

        TASK: for each thought, identify if it mentions anyone from the canonical cast. For each match, emit {name, trope} where:
          - "name" is a canonical name drawn from the canonical cast (never a name not in the cast; never the primary contact).
          - "trope" is a short general PATTERN about this person — a trait, a care, a repeated behavior. NEVER a specific event.

        Correct trope: "Lila cares about her students"
        Correct trope: "Dominus is comforting when thunder scares her"
        Incorrect trope: "Lila texted Mark yesterday about her trip" (that's an event)
        Incorrect trope: "the customer at the shop" (not canonical cast)

        Prefer an existing tracked name (case-insensitive) over a casing variant.

        FORMAT: strict JSON of the form:
        {"people": [{"name": "...", "trope": "..."}, ...]}

        No prose outside the JSON. No markdown fences. If no canonical-cast person is mentioned, return {"people": []}.
        """;

    private static string BuildUserPrompt(
        IReadOnlyList<MemoryRecord> thoughts,
        IReadOnlyList<string>       canonicalCast,
        string                      primaryContactName,
        IReadOnlyList<string>       existingPeopleNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CANONICAL CAST (only extract people found here):");
        foreach (var c in canonicalCast.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            sb.AppendLine($"  - {c}");
        }
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(primaryContactName))
        {
            sb.AppendLine($"EXCLUDE: {primaryContactName} (she is this character's primary contact; never emit her/his name).");
            sb.AppendLine();
        }

        sb.AppendLine("ALREADY TRACKED (prefer these spellings on match):");
        if (existingPeopleNames is null || existingPeopleNames.Count == 0)
        {
            sb.AppendLine("  (none yet)");
        }
        else
        {
            foreach (var n in existingPeopleNames)
                sb.AppendLine($"  - {n}");
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
        sb.AppendLine("Extract canonical-cast tropes per the taxonomy above. Return JSON only.");
        return sb.ToString();
    }
}
