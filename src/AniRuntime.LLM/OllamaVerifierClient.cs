using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

/// <summary>
/// Gate-stack reduction Step 3 (2026-05-15) — local Ollama-backed
/// implementation of <see cref="IFrontierVerifierClient"/>. Replaces the
/// Anthropic Sonnet cloud verifier with a local LLM (default Qwen 14B
/// via Ollama on the same host as the runtime).
///
/// **Why local.** The verifier task is classification, not generation —
/// a 14-20B local model handles it at ~80-90% of Sonnet's quality with
/// zero per-call cost, low latency (no round-trip), no cloud dependency,
/// and Ani's content never leaves the box. Per Mark's 2026-05-15
/// directive: trust the model + correct context; the cloud verifier was
/// a specific implementation choice that's no longer load-bearing.
///
/// **Prompt sourcing.** Reuses
/// <see cref="AnthropicVerifierClient.BuildSystemPrompt"/> and
/// <see cref="AnthropicVerifierClient.BuildUserPrompt"/> verbatim — the
/// prompt structure including the Theme M three-axis-rule slice
/// injection (when <c>EpistemicFramingEnabled</c>) is identical across
/// both implementations. The verdict-parsing helper
/// (<see cref="AnthropicVerifierClient.ParseVerdict"/>) is also reused.
///
/// **Error contract.** Same as Anthropic implementation: throws on
/// transport / parse / timeout error; the <c>FrontierVerifierHandler</c>
/// catches the throw and returns <c>Continue</c> with a
/// <c>P_VERIFIER_FALLBACK</c> log line. Graceful degradation: verifier
/// unreachable does NOT break dispatch.
///
/// **Ollama endpoint.** Uses Ollama's <c>POST /api/chat</c> with
/// <c>format=json</c> and <c>stream=false</c>. The model is configured
/// via <see cref="AniOptions.LocalVerifierModelTag"/> (default
/// <c>qwen3:14b</c>) and must be pulled separately on the host (e.g.
/// <c>ollama pull qwen3:14b</c>).
///
/// See: docs/spec/ANI-Gate-Stack-Reduction-Plan.md §3 Step 3.
/// </summary>
public sealed class OllamaVerifierClient : IFrontierVerifierClient
{
    private readonly HttpClient                      _http;
    private readonly OllamaOptions                   _ollamaOptions;
    private readonly AniOptions                      _aniOptions;
    private readonly IEpistemicSubstrateRenderer?    _epistemicRenderer;
    private readonly ILogger<OllamaVerifierClient>   _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public OllamaVerifierClient(
        HttpClient                       http,
        IOptions<OllamaOptions>          ollamaOptions,
        IOptions<AniOptions>             aniOptions,
        ILogger<OllamaVerifierClient>    log,
        IEpistemicSubstrateRenderer?     epistemicRenderer = null)
    {
        _http              = http ?? throw new ArgumentNullException(nameof(http));
        _ollamaOptions     = ollamaOptions?.Value ?? throw new ArgumentNullException(nameof(ollamaOptions));
        _aniOptions        = aniOptions?.Value ?? throw new ArgumentNullException(nameof(aniOptions));
        _log               = log ?? throw new ArgumentNullException(nameof(log));
        _epistemicRenderer = epistemicRenderer;

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_ollamaOptions.BaseUrl))
            _http.BaseAddress = new Uri(_ollamaOptions.BaseUrl);
    }

    public async Task<FrontierVerifierResult> VerifyAsync(
        FrontierVerifierRequest request,
        CancellationToken       ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var model = string.IsNullOrWhiteSpace(_aniOptions.LocalVerifierModelTag)
            ? "qwen3:14b"
            : _aniOptions.LocalVerifierModelTag;

        _log.LogDebug(
            "OllamaVerifierClient: dispatching verification (model={Model}, composed_chars={Chars}, " +
            "mark_substrate_chars={MarkChars}, canonical_substrate_chars={CanonicalChars})",
            model,
            request.ComposedMessage?.Length ?? 0,
            request.MarkAssertedSubstrate?.Length ?? 0,
            request.CanonicalSubstrate?.Length ?? 0);

        // Data-provision audit (2026-05-15): the 14:47 SafeAck remediated on
        // q5 with "user-asserted substrate is empty" reasoning. The retrieval
        // log at the same timestamp showed 11 caregiver records + 211 anchored
        // in the composition pool, but the verifier does its OWN post-hoc
        // retrieval at cosine ≥ MinCosineThresholdVerifier (default 0.75)
        // against the composed reply text, which for self-world content
        // ("shelving romance novels, brain needed the fluff") finds nothing
        // semantically similar in Mark-asserted substrate. Logging both
        // substrate previews so the next trace shows the empirical content
        // of each field — confirms whether CanonicalSubstrate carries the
        // bookstore-world anchored seeds (which it should, after P.3.1) or
        // is also empty.
        _log.LogDebug(
            "OllamaVerifierClient: mark_substrate_preview={MarkPreview}",
            Truncate(request.MarkAssertedSubstrate ?? "(null)", 300));
        _log.LogDebug(
            "OllamaVerifierClient: canonical_substrate_preview={CanonicalPreview}",
            Truncate(request.CanonicalSubstrate ?? "(null)", 300));

        // Prompt-caching restructure (2026-07-01): three-axis rule slice +
        // [QUESTIONS] + reply schema moved from user prompt to system prompt.
        // Ollama has no prompt-cache equivalent, so this is a semantic parity
        // move — same tokens sent, just under different role labels. Behavior
        // unchanged.
        var rendererForPrompt = _aniOptions.EpistemicFramingEnabled ? _epistemicRenderer : null;
        var system = AnthropicVerifierClient.BuildSystemPrompt(rendererForPrompt, request.AddresseeCanonicalName);
        var user   = AnthropicVerifierClient.BuildUserPrompt(request);

        // Ollama /api/chat payload — format=json forces structured output
        // matching the verdict schema; stream=false returns the full
        // response in one body.
        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user",   content = user   },
            },
            format = "json",
            stream = false,
            options = new
            {
                // Verifier is a classification task — low temperature
                // for determinism, modest top_p for stability.
                temperature = 0.1,
                top_p       = 0.9,
            },
        };

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requestCts.CancelAfter(TimeSpan.FromMilliseconds(_aniOptions.LocalVerifierTimeoutMs));

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(
                "/api/chat", payload, JsonOpts, requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Ollama verification call exceeded {_aniOptions.LocalVerifierTimeoutMs}ms timeout.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Ollama /api/chat returned {(int)response.StatusCode}: {Truncate(body, 500)}");
        }

        var apiResponse = await response.Content
            .ReadFromJsonAsync<OllamaChatResponse>(JsonOpts, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Ollama /api/chat returned empty body.");

        var rawText = apiResponse.Message?.Content;
        if (string.IsNullOrWhiteSpace(rawText))
            throw new InvalidOperationException(
                "Ollama /api/chat returned a response with no message content.");

        return AnthropicVerifierClient.ParseVerdict(rawText);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    // ── Ollama /api/chat response DTOs ─────────────────────────────────

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")] public OllamaMessage? Message { get; set; }
        [JsonPropertyName("done")]    public bool Done { get; set; }
    }

    private sealed class OllamaMessage
    {
        [JsonPropertyName("role")]    public string? Role    { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }
}
