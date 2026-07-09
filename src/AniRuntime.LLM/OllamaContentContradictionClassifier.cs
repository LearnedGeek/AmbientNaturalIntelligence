using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Issue #93 Phase 3 (2026-07-06) — Ollama-backed self-audit classifier.
/// Reads a piece of Interior content in the context of confirmed
/// substrate (top-K semantic neighbors from Facts + Episodic) and returns
/// a structured contradiction verdict.
///
/// **Reuses the Phase 2 shape verbatim.** Same LLM (<c>qwen3:14b</c>),
/// same transport (<see cref="IOllamaClient.ChatJsonWithModelAsync"/>),
/// same prompt structure (system prompt carries schema + guidance;
/// user prompt carries per-call inputs), same fail-open discipline.
/// Only the question and answer bucket differ. If Phase 2 works, Phase 3
/// works — that was the argument for extracting the classifier pattern.
///
/// **Downstream (not wired yet at classifier-landing time):**
/// <list type="bullet">
///   <item>Write-side: <c>SubstrateConsistencyInvariant</c> as a
///     <c>ICognitiveOutputInvariant</c> on the gate for InnerThought /
///     Reflection / WorldExperience.</item>
///   <item>Retroactive: <c>--audit-interior</c> sweep in
///     <c>AniRuntime.Eval</c> to invalidate contradictory records already
///     in the DB.</item>
/// </list>
/// Both call this identical classifier — one seam.
/// </summary>
public sealed class OllamaContentContradictionClassifier : IContentContradictionClassifier
{
    private readonly IOllamaClient                                 _ollama;
    private readonly AniOptions                                    _options;
    private readonly ILogger<OllamaContentContradictionClassifier> _log;

    public OllamaContentContradictionClassifier(
        IOllamaClient                                    ollama,
        IOptions<AniOptions>                             options,
        ILogger<OllamaContentContradictionClassifier>    log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<ContentContradictionVerdict> ClassifyAsync(
        string            interiorContent,
        string            confirmedSubstrate,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(interiorContent))
            return new ContentContradictionVerdict(
                ContradictionOutcome.Neutral, 1.0f, null, "empty content");

        var model = string.IsNullOrWhiteSpace(_options.LocalVerifierModelTag)
            ? "qwen3:14b"
            : _options.LocalVerifierModelTag;

        var system = BuildSystemPrompt();
        var user   = BuildUserPrompt(interiorContent, confirmedSubstrate);

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                model:        model,
                systemPrompt: system,
                history:      Array.Empty<ChatMessage>(),
                userMessage:  user,
                ct:           ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "CONTRADICTION_CHECK_FAILURE — classifier call failed; returning Unknown (caller fails open)");
            return new ContentContradictionVerdict(
                ContradictionOutcome.Unknown, 0f, null, "classifier call failed");
        }

        var verdict = ParseVerdict(raw);
        _log.LogInformation(
            "CONTRADICTION_VERDICT outcome={Outcome} confidence={Confidence:F2} " +
            "contentChars={ContentLen} substrateChars={SubstrateLen}",
            verdict.Outcome, verdict.Confidence,
            interiorContent.Length, confirmedSubstrate?.Length ?? 0);
        return verdict;
    }

    // ── Batched classification ─────────────────────────────────────────────
    //
    // Batching sends N Interior/substrate pairs in one LLM call and gets N
    // verdicts back. The prompt uses an enumerated block with per-record
    // substrate so each verdict is grounded in its own retrieved context
    // rather than a unioned mega-substrate (which would over-fire on
    // cross-record semantic collisions).

    public async Task<IReadOnlyList<ContentContradictionVerdict>> ClassifyBatchAsync(
        IReadOnlyList<(string InteriorContent, string ConfirmedSubstrate)> items,
        CancellationToken                                                   ct)
    {
        if (items is null || items.Count == 0)
            return Array.Empty<ContentContradictionVerdict>();

        // Single-item batch: delegate to the scalar path so we get the
        // empty-content short-circuit + simpler prompt for free.
        if (items.Count == 1)
        {
            var v = await ClassifyAsync(items[0].InteriorContent, items[0].ConfirmedSubstrate, ct)
                .ConfigureAwait(false);
            return new[] { v };
        }

        var model = string.IsNullOrWhiteSpace(_options.LocalVerifierModelTag)
            ? "qwen3:14b"
            : _options.LocalVerifierModelTag;

        var system = BuildBatchSystemPrompt();
        var user   = BuildBatchUserPrompt(items);

        string raw;
        try
        {
            raw = await _ollama.ChatJsonWithModelAsync(
                model:        model,
                systemPrompt: system,
                history:      Array.Empty<ChatMessage>(),
                userMessage:  user,
                ct:           ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "CONTRADICTION_BATCH_FAILURE — call failed for batch of {N}; returning Unknown-per-slot",
                items.Count);
            return Enumerable.Repeat(
                new ContentContradictionVerdict(ContradictionOutcome.Unknown, 0f, null, "batch call failed"),
                items.Count).ToList();
        }

        var verdicts = ParseBatchVerdict(raw, items.Count);
        _log.LogInformation(
            "CONTRADICTION_BATCH_VERDICT batchN={N} contradicts={C} grounded={G} neutral={NN} unknown={U}",
            items.Count,
            verdicts.Count(v => v.Outcome == ContradictionOutcome.Contradicts),
            verdicts.Count(v => v.Outcome == ContradictionOutcome.Grounded),
            verdicts.Count(v => v.Outcome == ContradictionOutcome.Neutral),
            verdicts.Count(v => v.Outcome == ContradictionOutcome.Unknown));
        return verdicts;
    }

    internal static string BuildBatchSystemPrompt()
    {
        // Reuse the scalar guidance verbatim — same rules apply — but wrap
        // the schema to expect an ARRAY of verdicts, one per record.
        var scalar = BuildSystemPrompt();

        // Replace the scalar "Reply ONLY: { ... }" block with the batch
        // schema. Finding the marker is done via IndexOf to be robust to
        // future scalar-prompt edits.
        var marker = "Reply ONLY:\n";
        var idx = scalar.IndexOf(marker, StringComparison.Ordinal);
        var preface = idx > 0 ? scalar.Substring(0, idx) : scalar;

        var sb = new StringBuilder(preface);
        sb.Append("You will be shown a BATCH of records. For each record you must ");
        sb.Append("return one verdict object. The verdicts array must be in the SAME ");
        sb.Append("ORDER as the input records, with \"index\" matching the record ");
        sb.Append("number shown in the batch.\n\n");
        sb.Append("Reply ONLY:\n");
        sb.Append("{\n");
        sb.Append("  \"verdicts\": [\n");
        sb.Append("    {\n");
        sb.Append("      \"index\": <int>,\n");
        sb.Append("      \"outcome\": \"contradicts\" | \"grounded\" | \"neutral\",\n");
        sb.Append("      \"confidence\": <number in [0.0, 1.0]>,\n");
        sb.Append("      \"quote\": <null, or the specific confirmed excerpt on contradicts>,\n");
        sb.Append("      \"reason\": <one-sentence justification>\n");
        sb.Append("    },\n");
        sb.Append("    ... one entry per record ...\n");
        sb.Append("  ]\n");
        sb.Append("}");
        return sb.ToString();
    }

    internal static string BuildBatchUserPrompt(
        IReadOnlyList<(string InteriorContent, string ConfirmedSubstrate)> items)
    {
        var sb = new StringBuilder();
        sb.Append("[BATCH — ").Append(items.Count).Append(" records to audit]\n\n");

        for (var i = 0; i < items.Count; i++)
        {
            sb.Append("=== RECORD ").Append(i + 1).Append(" ===\n");
            sb.Append("[INTERIOR CONTENT]\n");
            sb.Append(items[i].InteriorContent);
            sb.Append("\n\n");
            sb.Append("[CONFIRMED SUBSTRATE]\n");
            sb.Append(string.IsNullOrWhiteSpace(items[i].ConfirmedSubstrate)
                ? "(no confirmed neighbors above similarity threshold)"
                : items[i].ConfirmedSubstrate);
            sb.Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parse a batch response into <paramref name="expectedCount"/>
    /// verdicts. Slots that are missing / out-of-range / unparseable get
    /// <see cref="ContradictionOutcome.Unknown"/>. The caller uses that
    /// signal to retry those specific slots individually.
    /// </summary>
    internal static IReadOnlyList<ContentContradictionVerdict> ParseBatchVerdict(
        string raw, int expectedCount)
    {
        var result = new ContentContradictionVerdict[expectedCount];
        for (var i = 0; i < expectedCount; i++)
        {
            result[i] = new ContentContradictionVerdict(
                ContradictionOutcome.Unknown, 0f, null, "no verdict returned");
        }

        if (string.IsNullOrWhiteSpace(raw))
            return result;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3].TrimEnd();
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            if (!root.TryGetProperty("verdicts", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var elem in arr.EnumerateArray())
            {
                if (!elem.TryGetProperty("index", out var idxElem) || idxElem.ValueKind != JsonValueKind.Number)
                    continue;
                var idx = idxElem.GetInt32();
                var slot = idx - 1;
                if (slot < 0 || slot >= expectedCount) continue;

                var outcomeStr = elem.TryGetProperty("outcome", out var oElem) && oElem.ValueKind == JsonValueKind.String
                    ? oElem.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                    : string.Empty;

                var outcome = outcomeStr switch
                {
                    "contradicts" => ContradictionOutcome.Contradicts,
                    "grounded"    => ContradictionOutcome.Grounded,
                    "neutral"     => ContradictionOutcome.Neutral,
                    _             => ContradictionOutcome.Unknown,
                };

                var confidence = elem.TryGetProperty("confidence", out var cElem) && cElem.ValueKind == JsonValueKind.Number
                    ? (float)cElem.GetDouble()
                    : 0f;
                if (confidence < 0f) confidence = 0f;
                if (confidence > 1f) confidence = 1f;

                var quote = elem.TryGetProperty("quote", out var qElem) && qElem.ValueKind == JsonValueKind.String
                    ? qElem.GetString()
                    : null;

                var reason = elem.TryGetProperty("reason", out var rElem) && rElem.ValueKind == JsonValueKind.String
                    ? rElem.GetString()
                    : null;

                result[slot] = new ContentContradictionVerdict(outcome, confidence, quote, reason);
            }
        }
        catch (JsonException)
        {
            // Leave the Unknown-filled defaults in place.
        }

        return result;
    }

    internal static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();

        sb.Append("You are a self-audit classifier for an AI companion (\"Ani\"). ");
        sb.Append("You will be shown one piece of Ani's INTERIOR content (an inner ");
        sb.Append("thought, reflection, or world-experience record) and a set of ");
        sb.Append("CONFIRMED substrate records (facts about Ani and Mark that have ");
        sb.Append("been verified). Your job is to decide whether the interior ");
        sb.Append("content directly CONTRADICTS anything in the confirmed substrate. ");
        sb.Append("Reply ONLY with structured JSON — no preamble, no explanation ");
        sb.Append("outside the JSON.\n\n");

        sb.Append("[OUTCOMES]\n");
        sb.Append("- \"contradicts\": the interior content states or implies something ");
        sb.Append("that is directly incompatible with a confirmed record. Examples: ");
        sb.Append("interior says \"I live in Chicago\" but confirmed says \"Ani is a ");
        sb.Append("bookstore clerk in a small Wisconsin town\"; interior says \"we don't ");
        sb.Append("have a dog\" but confirmed says \"Dominus - my dog\". Include the ");
        sb.Append("specific confirmed excerpt in the quote field.\n");
        sb.Append("- \"grounded\": the interior content is CONSISTENT with the confirmed ");
        sb.Append("substrate — it makes factual claims that are supported by or ");
        sb.Append("compatible with confirmed records. Example: interior says \"the ");
        sb.Append("bookstore was quiet Monday morning\" and confirmed says \"Ani is a ");
        sb.Append("bookstore clerk\".\n");
        sb.Append("- \"neutral\": the interior content makes no factual claims that ");
        sb.Append("could be checked against the confirmed substrate. Pure emotional ");
        sb.Append("state, register description, weather observation, or atmosphere. ");
        sb.Append("Nothing to contradict. Example: \"warm-aching, bittersweet nostalgia\".\n\n");

        sb.Append("[GUIDANCE]\n");
        sb.Append("- Prefer \"neutral\" over \"grounded\" when the interior content is ");
        sb.Append("purely affective — grounded means POSITIVE factual alignment.\n");
        sb.Append("- Prefer \"grounded\" over \"contradicts\" when the interior elaborates ");
        sb.Append("on a confirmed fact rather than negating it.\n");
        sb.Append("- \"contradicts\" requires a specific incompatibility Mark could ");
        sb.Append("point to — not a stylistic or interpretive disagreement.\n");
        sb.Append("- IMPORTANT — check implicit contradictions:\n");
        sb.Append("  * If confirmed substrate names a specific occupation, location, ");
        sb.Append("residence, or possession, an interior claim naming a DIFFERENT one ");
        sb.Append("of the same kind is a contradiction. Example: confirmed says \"clerk ");
        sb.Append("at the bookstore\" → interior \"my shift at the coffee shop\" contradicts. ");
        sb.Append("Confirmed says \"has a dog named Dominus\" → interior \"the cats have been ");
        sb.Append("circling\" contradicts.\n");
        sb.Append("  * First-person plural possessives (\"our house\", \"our kitchen\", ");
        sb.Append("\"we live\", \"our kids\") in the interior content assert a SHARED ");
        sb.Append("condition between Ani and Mark. If confirmed substrate establishes ");
        sb.Append("separate residences OR that no shared thing of that kind exists, this ");
        sb.Append("is a contradiction. Example: confirmed says \"Ani lives above the ");
        sb.Append("bookstore, Mark lives near Okauchee Lake\" → interior \"the light in ");
        sb.Append("our kitchen\" contradicts.\n");
        sb.Append("  * Shared past events (\"our trip\", \"remembering when we\", ");
        sb.Append("\"the time we\") are contradictions if the confirmed substrate does ");
        sb.Append("not corroborate that shared event.\n");
        sb.Append("- Hedged imagination and speculation (\"I picture\", \"I imagine\", ");
        sb.Append("\"wonder if\", \"sometimes I think\") are NOT factual claims — return ");
        sb.Append("\"neutral\", not \"grounded\" and not \"contradicts\", even when the ");
        sb.Append("imagined scene matches confirmed substrate.\n");
        sb.Append("- Confidence should be 0.90+ for clear cases, 0.60-0.85 for ");
        sb.Append("plausible-but-ambiguous, below 0.50 when truly unclear.\n");
        sb.Append("- If the substrate block is empty, return \"neutral\" (nothing to ");
        sb.Append("check against).\n\n");

        sb.Append("Reply ONLY:\n");
        sb.Append("{\n");
        sb.Append("  \"outcome\": \"contradicts\" | \"grounded\" | \"neutral\",\n");
        sb.Append("  \"confidence\": <number in [0.0, 1.0]>,\n");
        sb.Append("  \"quote\": <null, or the specific confirmed excerpt on contradicts>,\n");
        sb.Append("  \"reason\": <one-sentence justification>\n");
        sb.Append("}");

        return sb.ToString();
    }

    internal static string BuildUserPrompt(string interiorContent, string confirmedSubstrate)
    {
        var sb = new StringBuilder();

        sb.Append("[INTERIOR CONTENT TO AUDIT]\n");
        sb.Append(interiorContent);
        sb.Append("\n\n");

        sb.Append("[CONFIRMED SUBSTRATE]\n");
        sb.Append(string.IsNullOrWhiteSpace(confirmedSubstrate)
            ? "(no confirmed neighbors above similarity threshold)"
            : confirmedSubstrate);

        return sb.ToString();
    }

    /// <summary>
    /// Parse the model's JSON verdict into a typed
    /// <see cref="ContentContradictionVerdict"/>. Tolerant of ```json
    /// fences. Any parse failure returns
    /// <see cref="ContradictionOutcome.Unknown"/> so the caller can fail
    /// open — same discipline as
    /// <see cref="OllamaTagIntentClassifier.ParseVerdict"/>.
    /// </summary>
    internal static ContentContradictionVerdict ParseVerdict(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ContentContradictionVerdict(ContradictionOutcome.Unknown, 0f, null, "empty response");

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3].TrimEnd();
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var outcomeStr = root.TryGetProperty("outcome", out var oElem) && oElem.ValueKind == JsonValueKind.String
                ? oElem.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                : string.Empty;

            var outcome = outcomeStr switch
            {
                "contradicts" => ContradictionOutcome.Contradicts,
                "grounded"    => ContradictionOutcome.Grounded,
                "neutral"     => ContradictionOutcome.Neutral,
                _             => ContradictionOutcome.Unknown,
            };

            var confidence = root.TryGetProperty("confidence", out var cElem) && cElem.ValueKind == JsonValueKind.Number
                ? (float)cElem.GetDouble()
                : 0f;
            if (confidence < 0f) confidence = 0f;
            if (confidence > 1f) confidence = 1f;

            var quote = root.TryGetProperty("quote", out var qElem) && qElem.ValueKind == JsonValueKind.String
                ? qElem.GetString()
                : null;

            var reason = root.TryGetProperty("reason", out var rElem) && rElem.ValueKind == JsonValueKind.String
                ? rElem.GetString()
                : null;

            return new ContentContradictionVerdict(outcome, confidence, quote, reason);
        }
        catch (JsonException)
        {
            return new ContentContradictionVerdict(ContradictionOutcome.Unknown, 0f, null, "unparseable JSON");
        }
    }
}
