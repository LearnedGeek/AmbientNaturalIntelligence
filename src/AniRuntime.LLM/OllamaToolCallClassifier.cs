using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Issue #96 (2026-07-15) — Ollama-backed implementation of
/// <see cref="IToolCallClassifier"/>. Single-shot structured-JSON call to
/// the local verifier model (<c>qwen3:14b</c> by default) that returns
/// either a tool selection (name + arguments) or a "no tool" verdict.
///
/// **Prompt shape.** Mirrors <see cref="OllamaTagIntentClassifier"/>: strict
/// "Reply ONLY with structured JSON", schema spelled out in the system
/// prompt, per-call variables in the user prompt. The tool descriptors are
/// rendered into the system prompt so the classifier sees the full "world"
/// of choices on every call.
///
/// **Transport.** Uses <see cref="IOllamaClient.ChatJsonWithModelAsync"/> —
/// the same seam <see cref="OllamaTagIntentClassifier"/> and
/// <see cref="OllamaContentContradictionClassifier"/> use, both proven at
/// 96–100% on production fixtures.
///
/// **Failure contract.** Any transport / parse / timeout throw is caught
/// and returned as a "no tool" verdict with a WARN log so callers fail
/// open into the untooled conversational path.
/// </summary>
public sealed class OllamaToolCallClassifier : IToolCallClassifier
{
    private readonly IOllamaClient                     _ollama;
    private readonly AniOptions                        _options;
    private readonly ILogger<OllamaToolCallClassifier> _log;

    public OllamaToolCallClassifier(
        IOllamaClient                     ollama,
        IOptions<AniOptions>              options,
        ILogger<OllamaToolCallClassifier> log)
    {
        _ollama  = ollama  ?? throw new ArgumentNullException(nameof(ollama));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<ToolCallVerdict> ClassifyAsync(
        string                        userMessage,
        IReadOnlyList<ToolDescriptor> availableTools,
        string                        conversationContext,
        CancellationToken             ct)
    {
        if (availableTools is null || availableTools.Count == 0)
        {
            return new ToolCallVerdict(
                ShouldCallTool: false,
                ToolName:       null,
                Arguments:      null,
                Confidence:     1f,
                Reason:         "no tools available");
        }

        var model = string.IsNullOrWhiteSpace(_options.LocalVerifierModelTag)
            ? "qwen3:14b"
            : _options.LocalVerifierModelTag;

        var system = BuildSystemPrompt(availableTools);
        var user   = BuildUserPrompt(userMessage, conversationContext);

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
                "TOOL_CALL_FAILURE — classifier call failed; returning no-call verdict (caller fails open)");
            return new ToolCallVerdict(
                ShouldCallTool: false,
                ToolName:       null,
                Arguments:      null,
                Confidence:     0f,
                Reason:         "classifier call failed");
        }

        var verdict = ParseVerdict(raw, availableTools);
        _log.LogInformation(
            "TOOL_CALL_VERDICT shouldCall={ShouldCall} tool={Tool} confidence={Confidence:F2} " +
            "userMessageChars={UserLen} toolCount={ToolCount}",
            verdict.ShouldCallTool, verdict.ToolName ?? "(none)",
            verdict.Confidence, userMessage.Length, availableTools.Count);
        return verdict;
    }

    internal static string BuildSystemPrompt(IReadOnlyList<ToolDescriptor> tools)
    {
        var sb = new StringBuilder();

        sb.Append("You decide whether an AI companion (\"Ani\") should invoke a *tool* to answer ");
        sb.Append("the user's message, or whether she should just respond conversationally without ");
        sb.Append("looking anything up. Reply ONLY with structured JSON matching the schema below — ");
        sb.Append("no preamble, no explanation outside the JSON.\n\n");

        sb.Append("[TOOLS AVAILABLE]\n");
        for (var i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            sb.Append($"- name: \"{t.Name}\"\n");
            sb.Append($"  description: {t.Description}\n");
            sb.Append("  parameters:\n");
            if (t.ParameterSchema.Count == 0)
            {
                sb.Append("    (none)\n");
            }
            else
            {
                foreach (var kv in t.ParameterSchema)
                {
                    sb.Append($"    - {kv.Key}: {kv.Value}\n");
                }
            }
        }
        sb.Append('\n');

        sb.Append("[GUIDANCE]\n");
        sb.Append("- Pick a tool when the user's message clearly benefits from the structured ");
        sb.Append("lookup that tool performs. Casual conversation, greetings, feelings, jokes, and ");
        sb.Append("open-ended reflection do NOT need a tool.\n");
        sb.Append("- Tools exist to PROVIDE information Ani doesn't currently have in view. If the ");
        sb.Append("user asks about the past, a named person, a place, or a fact that might have been ");
        sb.Append("previously discussed, call the appropriate tool. The absence of that information in ");
        sb.Append("the conversation context shown here is a reason to CALL a tool, not a reason to skip ");
        sb.Append("it. Do NOT decline a tool call on the grounds that \"no prior context is shown\" — ");
        sb.Append("that is exactly when the tool is most useful.\n");
        sb.Append("- Do NOT read the tool description as a scope filter that excludes entities the ");
        sb.Append("user names. If the tool searches Ani's memory, it can find anything in Ani's ");
        sb.Append("memory — including people the user mentions by name (family, friends), places, ");
        sb.Append("and events, not only the named user.\n");
        sb.Append("- When picking a tool, extract arguments verbatim from the user's message where ");
        sb.Append("possible. Do NOT invent details the user did not provide.\n");
        sb.Append("- Confidence should be 0.90+ for clear-cut cases, 0.60–0.85 for plausible-but-");
        sb.Append("ambiguous, below 0.50 when the choice is genuinely unclear. Prefer no-tool when ");
        sb.Append("confidence would fall below 0.50.\n");
        sb.Append("- When no tool fits, set should_call_tool=false and tool_name=null.\n\n");

        sb.Append("Reply ONLY:\n");
        sb.Append("{\n");
        sb.Append("  \"should_call_tool\": <true|false>,\n");
        sb.Append("  \"tool_name\": <string matching one of the tool names above, or null>,\n");
        sb.Append("  \"arguments\": <object mapping parameter names to string values, or null>,\n");
        sb.Append("  \"confidence\": <number in [0.0, 1.0]>,\n");
        sb.Append("  \"reason\": <one-sentence justification>\n");
        sb.Append('}');

        return sb.ToString();
    }

    internal static string BuildUserPrompt(string userMessage, string conversationContext)
    {
        var sb = new StringBuilder();

        sb.Append("[USER MESSAGE]\n");
        sb.Append(userMessage);
        sb.Append("\n\n");

        sb.Append("[RECENT CONVERSATION CONTEXT]\n");
        sb.Append(string.IsNullOrWhiteSpace(conversationContext) ? "(no context)" : conversationContext);

        return sb.ToString();
    }

    /// <summary>
    /// Parse the model's JSON verdict. Tolerant of ```json fences (same
    /// discipline as <see cref="OllamaTagIntentClassifier.ParseVerdict"/>).
    /// Any parse failure or unknown tool name returns a no-call verdict so
    /// the caller can fail open.
    ///
    /// <paramref name="availableTools"/> is passed in so we can validate the
    /// model's <c>tool_name</c> against the descriptor set — an unknown
    /// name coerces to no-call rather than propagating a bogus tool
    /// identifier into the dispatcher.
    /// </summary>
    internal static ToolCallVerdict ParseVerdict(string raw, IReadOnlyList<ToolDescriptor> availableTools)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ToolCallVerdict(false, null, null, 0f, "empty response");

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

            var shouldCall = root.TryGetProperty("should_call_tool", out var scElem)
                             && scElem.ValueKind == JsonValueKind.True;

            var toolName = root.TryGetProperty("tool_name", out var tnElem) && tnElem.ValueKind == JsonValueKind.String
                ? tnElem.GetString()?.Trim()
                : null;

            var confidence = root.TryGetProperty("confidence", out var cElem) && cElem.ValueKind == JsonValueKind.Number
                ? (float)cElem.GetDouble()
                : 0f;
            if (confidence < 0f) confidence = 0f;
            if (confidence > 1f) confidence = 1f;

            var reason = root.TryGetProperty("reason", out var rElem) && rElem.ValueKind == JsonValueKind.String
                ? rElem.GetString()
                : null;

            // Coerce should_call=true with unknown / null tool name back to
            // a no-call verdict — the classifier picked a tool the runtime
            // does not know about, which is unsafe to dispatch.
            if (shouldCall)
            {
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return new ToolCallVerdict(false, null, null, confidence,
                        "should_call_tool=true but tool_name missing");
                }
                if (!availableTools.Any(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)))
                {
                    return new ToolCallVerdict(false, null, null, confidence,
                        $"unknown tool_name '{toolName}'");
                }
            }

            IReadOnlyDictionary<string, string>? arguments = null;
            if (shouldCall && root.TryGetProperty("arguments", out var aElem) && aElem.ValueKind == JsonValueKind.Object)
            {
                var argMap = new Dictionary<string, string>();
                foreach (var prop in aElem.EnumerateObject())
                {
                    // Stringify each argument value. LLMs emit primitives
                    // (string / number / bool); we normalize them all to
                    // string here so the action layer has one shape to parse.
                    argMap[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        JsonValueKind.Null   => string.Empty,
                        _                    => prop.Value.GetRawText(),
                    };
                }
                arguments = argMap;
            }

            return new ToolCallVerdict(shouldCall, shouldCall ? toolName : null, arguments, confidence, reason);
        }
        catch (JsonException)
        {
            return new ToolCallVerdict(false, null, null, 0f, "unparseable JSON");
        }
    }
}
