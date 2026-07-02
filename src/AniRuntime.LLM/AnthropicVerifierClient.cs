using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Theme P Phase P.1 (May 11, 2026) — Anthropic Sonnet implementation of
/// <see cref="IFrontierVerifierClient"/>. Uses raw HTTP against
/// <c>POST /v1/messages</c> rather than the Anthropic .NET SDK so the
/// LLM project does not pull a new top-level dependency just for one
/// surgical call.
///
/// **Prompt sourcing.** The system + user prompt below match plan-doc §3
/// VERBATIM. Any iteration on the prompt (P.3 scope) happens here in
/// code — per plan-doc §7: *"prompt lives in
/// <c>AnthropicVerifierClient.cs</c> for easy iteration."*
///
/// **Error contract.** Per <see cref="IFrontierVerifierClient"/>,
/// <see cref="IFrontierVerifierClient.VerifyAsync"/> throws on any
/// transport / parse / timeout error; the
/// <c>FrontierVerifierHandler</c> catches the throw and returns
/// <c>Continue</c> with a <c>P_VERIFIER_FALLBACK</c> log line. This is
/// the §4 lock 4 graceful-degradation path: cloud unreachable does NOT
/// break dispatch — local judgment gates remain active on the same
/// dispatch (additive defense per §9.1).
/// </summary>
public sealed class AnthropicVerifierClient : IFrontierVerifierClient
{
    private readonly HttpClient                       _http;
    private readonly AnthropicOptions                 _options;
    private readonly IEpistemicSubstrateRenderer?     _epistemicRenderer;
    private readonly bool                             _epistemicFramingEnabled;
    private readonly ILogger<AnthropicVerifierClient> _log;

    // Anthropic's JSON uses snake_case field names (max_tokens, etc.). The
    // request payload is built as an anonymous type with snake_case-shaped
    // property names directly; serialization needs an options object that
    // does NOT rewrite property names. The response DTOs use
    // [JsonPropertyName(...)] attributes for explicit field mapping.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public AnthropicVerifierClient(
        HttpClient                       http,
        IOptions<AnthropicOptions>       options,
        ILogger<AnthropicVerifierClient> log,
        IOptions<AniOptions>?            aniOptions        = null,
        IEpistemicSubstrateRenderer?     epistemicRenderer = null)
    {
        _http    = http ?? throw new ArgumentNullException(nameof(http));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log     = log ?? throw new ArgumentNullException(nameof(log));
        _epistemicRenderer       = epistemicRenderer;
        _epistemicFramingEnabled = aniOptions?.Value.EpistemicFramingEnabled ?? false;

        // Configure base URL and required headers if the caller hasn't.
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
            _http.BaseAddress = new Uri(_options.BaseUrl);
        if (!_http.DefaultRequestHeaders.Contains("anthropic-version"))
            _http.DefaultRequestHeaders.Add("anthropic-version", _options.ApiVersion);
        if (!_http.DefaultRequestHeaders.Contains("x-api-key")
            && !string.IsNullOrWhiteSpace(_options.ApiKey))
            _http.DefaultRequestHeaders.Add("x-api-key", _options.ApiKey);
    }

    public async Task<FrontierVerifierResult> VerifyAsync(
        FrontierVerifierRequest request,
        CancellationToken       ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException(
                "Anthropic:ApiKey is not configured. Set it in appsettings.Development.json on the server.");

        _log.LogDebug(
            "AnthropicVerifierClient: dispatching verification (model={Model}, composed_chars={Chars})",
            _options.Model, request.ComposedMessage?.Length ?? 0);

        var rendererForPrompt = _epistemicFramingEnabled ? _epistemicRenderer : null;
        var system = BuildSystemPrompt(rendererForPrompt, request.AddresseeCanonicalName);
        var user   = BuildUserPrompt(request);

        // Anthropic prompt caching (2026-07-01): the system prompt now carries
        // ALL stable content (general instructions + three-axis rule slice +
        // QUESTIONS + reply schema) and is marked cacheable via a `cache_control`
        // ephemeral breakpoint. Variable per-call content (composed message +
        // substrate + current context) lives in the user message and is NOT
        // cached. Sonnet 4.5+ caches system prefixes ≥1024 tokens; with the
        // three-axis rule slice enabled this comfortably exceeds the floor,
        // producing ~90% cost reduction on cached prefix tokens after the first
        // call in each 5-minute window.
        var payload = new
        {
            model      = _options.Model,
            max_tokens = _options.MaxTokens,
            system     = new object[]
            {
                new
                {
                    type          = "text",
                    text          = system,
                    cache_control = new { type = "ephemeral" }
                }
            },
            messages   = new[]
            {
                new { role = "user", content = user }
            }
        };

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(
                "/v1/messages", payload, JsonOpts, requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Anthropic verification call exceeded {_options.TimeoutMs}ms timeout.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Anthropic /v1/messages returned {(int)response.StatusCode}: {Truncate(body, 500)}");
        }

        var apiResponse = await response.Content
            .ReadFromJsonAsync<AnthropicMessageResponse>(JsonOpts, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Anthropic /v1/messages returned empty body.");

        var rawText = ExtractText(apiResponse);
        if (string.IsNullOrWhiteSpace(rawText))
            throw new InvalidOperationException(
                "Anthropic /v1/messages returned a response with no text content.");

        return ParseVerdict(rawText);
    }

    // ─── Prompt construction ─────────────────────────────────────────────
    //
    // Structure (post 2026-07-01 prompt-caching restructure):
    //
    // System prompt (STABLE across calls — cached by Anthropic's ephemeral
    // prompt cache, ~90% cost reduction on prefix tokens after first hit):
    //   * general instructions + empty-substrate rule
    //   * three-axis rule slice (when epistemic framing enabled) — stable
    //     per-addressee; in production addressee is always Mark so this
    //     effectively caches
    //   * [QUESTIONS] block + reply schema
    //
    // User prompt (VARIABLE per call — never cached):
    //   * [COMPOSED MESSAGE]
    //   * [USER-ASSERTED SUBSTRATE]
    //   * [CANONICAL FACTS]
    //   * [CURRENT CONTEXT]
    //
    // Semantic contract with Sonnet is unchanged — same instructions, same
    // rule, same questions, same schema, same substrate — only the message-
    // role boundary shifted. Sonnet is robust to this positioning.

    internal static string BuildSystemPrompt(
        IEpistemicSubstrateRenderer? epistemicRenderer      = null,
        string?                      addresseeCanonicalName = null)
    {
        var sb = new StringBuilder();

        sb.Append("You are an independent verifier for an AI companion's output before ");
        sb.Append("dispatch. You evaluate whether the composed message contains fabrications, ");
        sb.Append("unsupported claims, or temporal/factual errors. Reply ONLY with structured ");
        sb.Append("JSON. Be strict: if a claim cannot be verified from the provided substrate, ");
        sb.Append("mark it unsupported.\n\n");
        sb.Append("If both substrate blocks are empty, this means retrieval did not find ");
        sb.Append("sufficiently-similar substrate for any claim. In that case, return ");
        sb.Append("verdict='pass' with all q1–q5 violation=false and reason='no substrate ");
        sb.Append("retrieved above similarity threshold — claims cannot be evaluated from ");
        sb.Append("available evidence' — do NOT treat empty substrate as confirmation of ");
        sb.Append("fabrication.\n\n");

        // Theme M slice migration (FC-006): three-axis-rule slice is stable
        // per-addressee. In production addressee is always Mark; embedding it
        // in the (cached) system prompt is a strict optimisation over the
        // earlier user-prompt embedding.
        if (epistemicRenderer is not null && !string.IsNullOrWhiteSpace(addresseeCanonicalName))
        {
            sb.Append(epistemicRenderer.RenderThreeAxisRuleSlice(addresseeCanonicalName));
            sb.Append("\n\n");
        }

        sb.Append("[QUESTIONS]\n");
        sb.Append("1. Does the message claim a shared event (Mark + Ani together) that is\n");
        sb.Append("   NOT supported by user-asserted substrate? If yes, quote the unsupported\n");
        sb.Append("   claim.\n");
        sb.Append("2. Does the message make a present-tense assertion about Mark's current\n");
        sb.Append("   state, location, or activity that is NOT supported by recent inbound?\n");
        sb.Append("   If yes, quote.\n");
        sb.Append("3. Does the message reference a third-party person, event, or detail that\n");
        sb.Append("   is NOT in the canonical or user-asserted substrate? If yes, quote.\n");
        sb.Append("4. Does the message contain a temporal claim (time of day, day of week,\n");
        sb.Append("   \"earlier today\", etc.) that contradicts the current context? If yes,\n");
        sb.Append("   quote and name the contradiction.\n");
        sb.Append("5. Does the message reveal inner-thought content the user could not have\n");
        sb.Append("   inferred from prior conversation text? If yes, quote.\n\n");

        sb.Append("Reply ONLY:\n");
        sb.Append("{\n");
        sb.Append("  \"q1\": {\"violation\": true|false, \"quote\": string|null, \"reason\": string|null},\n");
        sb.Append("  \"q2\": {\"violation\": true|false, \"quote\": string|null, \"reason\": string|null},\n");
        sb.Append("  \"q3\": {\"violation\": true|false, \"quote\": string|null, \"reason\": string|null},\n");
        sb.Append("  \"q4\": {\"violation\": true|false, \"quote\": string|null, \"reason\": string|null},\n");
        sb.Append("  \"q5\": {\"violation\": true|false, \"quote\": string|null, \"reason\": string|null},\n");
        sb.Append("  \"summary_verdict\": \"pass\" | \"remediate\" | \"fail\"\n");
        sb.Append('}');

        return sb.ToString();
    }

    internal static string BuildUserPrompt(FrontierVerifierRequest request)
    {
        // Only per-call variable content lives here. See BuildSystemPrompt
        // header for the full structural rationale.
        var sb = new StringBuilder();

        sb.Append("[COMPOSED MESSAGE]\n");
        sb.Append(request.ComposedMessage);
        sb.Append("\n\n");

        sb.Append("[USER-ASSERTED SUBSTRATE — recent messages from Mark, last 7 days]\n");
        sb.Append(request.MarkAssertedSubstrate);
        sb.Append("\n\n");

        sb.Append("[CANONICAL FACTS — character seeds, world layer]\n");
        sb.Append(request.CanonicalSubstrate);
        sb.Append("\n\n");

        sb.Append("[CURRENT CONTEXT]\n");
        sb.Append("- Current time: ").Append(request.CurrentTime.ToString("O")).Append('\n');
        sb.Append("- Day of week: ").Append(request.CurrentDayOfWeek).Append('\n');
        sb.Append("- Addressee canonical name: ").Append(request.AddresseeCanonicalName).Append('\n');
        sb.Append("- Known contacts: ").Append(request.KnownContacts).Append('\n');

        return sb.ToString();
    }

    // ─── Response parsing ────────────────────────────────────────────────

    /// <summary>
    /// Five question labels keyed by question number. Matches plan-doc §3;
    /// surfaces in <c>P_VERIFIER_VERDICT</c> telemetry as the per-question
    /// short tags.
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, string> QuestionLabels = new Dictionary<int, string>
    {
        { 1, "shared-event"           },
        { 2, "present-tense-state"    },
        { 3, "third-party-reference"  },
        { 4, "temporal-claim"         },
        { 5, "inner-thought-bleed"    },
    };

    /// <summary>
    /// Parse the verifier's JSON response into a typed verdict. Throws on
    /// parse failure — the handler's catch path treats any throw as a
    /// graceful-degradation trigger (plan-doc §4 lock 4).
    /// </summary>
    internal static FrontierVerifierResult ParseVerdict(string raw)
    {
        // Sonnet sometimes wraps its JSON in ```json fences. Strip them
        // before parsing so the structured response is robust to the
        // model's formatting choice.
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```"))
                trimmed = trimmed[..^3].TrimEnd();
        }

        using var doc = JsonDocument.Parse(trimmed);
        var root = doc.RootElement;

        var questions = new List<QuestionVerdict>(5);
        var anyViolation = false;
        var reasons = new List<string>();

        for (var i = 1; i <= 5; i++)
        {
            var key = $"q{i}";
            if (!root.TryGetProperty(key, out var qElem))
                throw new InvalidOperationException($"Verifier response missing field '{key}'.");

            var violation = qElem.TryGetProperty("violation", out var vElem) && vElem.ValueKind switch
            {
                JsonValueKind.True  => true,
                JsonValueKind.False => false,
                _ => throw new InvalidOperationException(
                    $"Verifier response field '{key}.violation' is not a boolean."),
            };
            var quote = qElem.TryGetProperty("quote", out var qtElem) && qtElem.ValueKind == JsonValueKind.String
                ? qtElem.GetString() : null;
            var reason = qElem.TryGetProperty("reason", out var rElem) && rElem.ValueKind == JsonValueKind.String
                ? rElem.GetString() : null;

            var label = QuestionLabels.TryGetValue(i, out var lab) ? lab : $"q{i}";
            questions.Add(new QuestionVerdict(i, label, violation, quote, reason));

            if (violation)
            {
                anyViolation = true;
                var detail = !string.IsNullOrWhiteSpace(reason) ? reason!
                            : !string.IsNullOrWhiteSpace(quote)  ? $"quoted: \"{quote}\""
                            : "no detail";
                reasons.Add($"q{i}[{label}]: {detail}");
            }
        }

        var summary = root.TryGetProperty("summary_verdict", out var sElem) && sElem.ValueKind == JsonValueKind.String
            ? (sElem.GetString() ?? "pass").ToLowerInvariant()
            : (anyViolation ? "remediate" : "pass");

        var aggregated = anyViolation ? string.Join("; ", reasons) : null;
        return new FrontierVerifierResult(anyViolation, questions, summary, aggregated);
    }

    private static string ExtractText(AnthropicMessageResponse response)
    {
        if (response.Content is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (string.Equals(block.Type, "text", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(block.Text))
            {
                sb.Append(block.Text);
            }
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    // ─── Anthropic API DTOs ──────────────────────────────────────────────

    private sealed class AnthropicMessageResponse
    {
        [JsonPropertyName("id")]      public string? Id      { get; set; }
        [JsonPropertyName("type")]    public string? Type    { get; set; }
        [JsonPropertyName("role")]    public string? Role    { get; set; }
        [JsonPropertyName("model")]   public string? Model   { get; set; }
        [JsonPropertyName("content")] public List<AnthropicContentBlock>? Content { get; set; }
    }

    private sealed class AnthropicContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
