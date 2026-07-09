using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Issue #93 Phase 2 (2026-07-06) — Ollama-backed implementation of
/// <see cref="ITagIntentClassifier"/>. Replaces the regex intent-sniff at
/// <c>TagCommand.cs:109</c> (which only fired on <c>confab / fabricat /
/// walk back / made up / hallucination</c> and was inert against Mark's
/// actual vocabulary — "broken output" / "weather confusion" / "json
/// response?") with a structured-JSON single-shot classification call
/// against the local verifier model (<c>qwen3:14b</c> by default).
///
/// **Prompt shape.** Mirrors <see cref="AnthropicVerifierClient"/>'s
/// verifier prompt: strict "Reply ONLY with structured JSON", schema
/// spelled out in the system prompt, per-call variables in the user
/// prompt. Chose the verifier's shape over
/// <see cref="OllamaRoutingClassifier"/>'s single-letter shape because
/// the tag intent needs a richer verdict (intent + confidence + reason)
/// and a single letter can't carry the confidence scalar the caller
/// needs to guard against low-confidence substrate mutations.
///
/// **Transport.** Uses <see cref="IOllamaClient.ChatJsonWithModelAsync"/> —
/// the same seam <c>ReflectionPhase</c> uses to reach the verifier model
/// with <c>format=json</c>. No new HTTP plumbing.
///
/// **Failure contract.** Any transport / parse / timeout throw is caught
/// and returned as <see cref="TagIntent.Unknown"/> with a WARN log. Caller
/// (<c>TagCommand</c>) treats Unknown as fail-open: save the flag for
/// audit, apply no substrate mutation. Matches the verifier fallback shape.
/// </summary>
public sealed class OllamaTagIntentClassifier : ITagIntentClassifier
{
    private readonly IOllamaClient                         _ollama;
    private readonly AniOptions                            _options;
    private readonly ILogger<OllamaTagIntentClassifier>    _log;

    public OllamaTagIntentClassifier(
        IOllamaClient                          ollama,
        IOptions<AniOptions>                   options,
        ILogger<OllamaTagIntentClassifier>     log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<TagIntentVerdict> ClassifyAsync(
        string            tagNote,
        string            aniReply,
        string            markPriorMessage,
        CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(_options.LocalVerifierModelTag)
            ? "qwen3:14b"
            : _options.LocalVerifierModelTag;

        var system = BuildSystemPrompt();
        var user   = BuildUserPrompt(tagNote, aniReply, markPriorMessage);

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
                "TAG_INTENT_FAILURE — classifier call failed; returning Unknown (caller fails open)");
            return new TagIntentVerdict(TagIntent.Unknown, 0f, "classifier call failed");
        }

        var verdict = ParseVerdict(raw);
        _log.LogInformation(
            "TAG_INTENT_VERDICT intent={Intent} confidence={Confidence:F2} " +
            "tagNoteChars={NoteLen} aniReplyChars={ReplyLen}",
            verdict.Intent, verdict.Confidence, tagNote.Length, aniReply.Length);
        return verdict;
    }

    internal static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();

        sb.Append("You classify short one-line research tags written by the operator (\"Mark\") ");
        sb.Append("against an AI companion's reply (\"Ani\"). The tag was written after Ani ");
        sb.Append("replied to Mark, and it is Mark's after-the-fact judgment on the reply. ");
        sb.Append("Your job is to identify which of four intents the tag expresses. ");
        sb.Append("Reply ONLY with structured JSON matching the schema below — no preamble, ");
        sb.Append("no explanation outside the JSON.\n\n");

        sb.Append("[INTENTS]\n");
        sb.Append("- \"confirm\": Mark endorses the reply as correct / true / on-target. ");
        sb.Append("Examples: \"yes\", \"that's right\", \"exactly\", \"correct\", \"good one\", ");
        sb.Append("\"nailed it\", \"true\".\n");
        sb.Append("- \"invalidate\": Mark calls out the reply as fabricated, incorrect, wrong, ");
        sb.Append("confused, or hallucinated. Examples: \"confab\", \"fabrication\", \"walk back\", ");
        sb.Append("\"made up\", \"hallucination\", \"broken output\", \"weather confusion\", ");
        sb.Append("\"json response?\", \"this is wrong\", \"never happened\".\n");
        sb.Append("- \"clarify\": Mark asks a clarifying question or expresses ambiguous ");
        sb.Append("reaction — not a truth verdict. Examples: \"why\", \"hmm\", \"interesting\", ");
        sb.Append("\"explain\", \"what did you mean\".\n");
        sb.Append("- \"unrelated\": The tag content is off-topic to the reply — a note-to-self, ");
        sb.Append("test tag, or accidental content. Examples: \"test\", \"reminder\", \"todo\".\n\n");

        sb.Append("[GUIDANCE]\n");
        sb.Append("- Prefer \"invalidate\" when the tag surfaces a symptom (\"broken\", ");
        sb.Append("\"weather confusion\", \"json response?\") — these are Mark's shorthand for ");
        sb.Append("\"the reply was wrong in this specific way\".\n");
        sb.Append("- Prefer \"clarify\" when uncertainty is expressed without a verdict.\n");
        sb.Append("- Confidence should be 0.90+ for canonical exemplars, 0.60–0.85 for ");
        sb.Append("plausible-but-ambiguous, below 0.50 when the tag is genuinely unclear.\n");
        sb.Append("- Never split the difference across intents — pick one and stake the ");
        sb.Append("confidence on it.\n\n");

        sb.Append("Reply ONLY:\n");
        sb.Append("{\n");
        sb.Append("  \"intent\": \"confirm\" | \"invalidate\" | \"clarify\" | \"unrelated\",\n");
        sb.Append("  \"confidence\": <number in [0.0, 1.0]>,\n");
        sb.Append("  \"reason\": <one-sentence justification>\n");
        sb.Append("}");

        return sb.ToString();
    }

    internal static string BuildUserPrompt(string tagNote, string aniReply, string markPriorMessage)
    {
        var sb = new StringBuilder();

        sb.Append("[MARK'S TAG NOTE]\n");
        sb.Append(tagNote);
        sb.Append("\n\n");

        sb.Append("[ANI REPLY THE TAG ANCHORS AGAINST]\n");
        sb.Append(aniReply);
        sb.Append("\n\n");

        sb.Append("[MARK'S PRIOR MESSAGE (context)]\n");
        sb.Append(string.IsNullOrWhiteSpace(markPriorMessage) ? "(no prior message)" : markPriorMessage);

        return sb.ToString();
    }

    /// <summary>
    /// Parse the model's JSON verdict into a typed <see cref="TagIntentVerdict"/>.
    /// Tolerant of ```json fences (models sometimes wrap even under format=json).
    /// Any parse failure returns <see cref="TagIntent.Unknown"/> so the caller
    /// can fail open — same discipline as
    /// <see cref="AnthropicVerifierClient.ParseVerdict"/>.
    /// </summary>
    internal static TagIntentVerdict ParseVerdict(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new TagIntentVerdict(TagIntent.Unknown, 0f, "empty response");

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

            var intentStr = root.TryGetProperty("intent", out var iElem) && iElem.ValueKind == JsonValueKind.String
                ? iElem.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                : string.Empty;

            var intent = intentStr switch
            {
                "confirm"    => TagIntent.Confirm,
                "invalidate" => TagIntent.Invalidate,
                "clarify"    => TagIntent.Clarify,
                "unrelated"  => TagIntent.Unrelated,
                _            => TagIntent.Unknown,
            };

            var confidence = root.TryGetProperty("confidence", out var cElem) && cElem.ValueKind == JsonValueKind.Number
                ? (float)cElem.GetDouble()
                : 0f;
            if (confidence < 0f) confidence = 0f;
            if (confidence > 1f) confidence = 1f;

            var reason = root.TryGetProperty("reason", out var rElem) && rElem.ValueKind == JsonValueKind.String
                ? rElem.GetString()
                : null;

            return new TagIntentVerdict(intent, confidence, reason);
        }
        catch (JsonException)
        {
            return new TagIntentVerdict(TagIntent.Unknown, 0f, "unparseable JSON");
        }
    }
}
