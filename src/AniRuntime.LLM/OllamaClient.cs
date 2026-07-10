using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Utilities;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

public class OllamaClient : IOllamaClient
{
    private readonly HttpClient            _http;
    private readonly OllamaOptions         _options;
    private readonly ILogger<OllamaClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = JsonDefaults.CamelCase;

    public OllamaClient(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaClient> log)
    {
        _http    = http;
        _options = options.Value;
        _log     = log;
    }

    public Task<string> ChatAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default, float? temperature = null)
        => SendChatAsync(_options.ChatModel, systemPrompt, history, userMessage, format: null, ct, temperature);

    public Task<string> ChatJsonAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default)
        => SendChatAsync(_options.ChatModel, systemPrompt, history, userMessage, format: "json", ct, temperature: null);

    public Task<string> InnerMonologueChatAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default, string? keepAlive = null)
        => SendChatAsync(_options.ResolvedInnerMonologueModel, systemPrompt, history, userMessage, format: null, ct, temperature: null, keepAlive: keepAlive);

    public Task<string> InnerMonologueChatJsonAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default, string? keepAlive = null)
        => SendChatAsync(_options.ResolvedInnerMonologueModel, systemPrompt, history, userMessage, format: "json", ct, temperature: null, keepAlive: keepAlive);

    public Task<string> ChatJsonWithModelAsync(
        string model, string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default, string? keepAlive = null)
        => SendChatAsync(model, systemPrompt, history, userMessage, format: "json", ct, temperature: null, keepAlive: keepAlive);

    private async Task<string> SendChatAsync(
        string model, string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        string? format, CancellationToken ct, float? temperature = null, string? keepAlive = null)
    {
        var alive = keepAlive ?? "5m";

        // Phase K.1 (2026-07-02): a null/whitespace systemPrompt is now the
        // canonical signal for "defer to the Modelfile's baked SYSTEM."
        // Ollama replaces the Modelfile SYSTEM if any system-role message
        // appears in the request — so to preserve the fine-tune's baked
        // persona (e.g. ani-v7-conversation's character prompt), we must
        // omit the system message entirely rather than send an empty one.
        // Prior callers all pass a real system prompt, so this is a
        // net-additive change: it enables a new mode without altering any
        // existing caller's behavior.
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });

        // Capture history in a concrete list so we can both iterate for the
        // request AND log the same sequence for the parrot-bug investigation
        // (Apr 23, 2026). Iterating the IEnumerable twice would be unsafe for
        // some underlying collections; materializing here is cheap.
        var historyList = history as IList<ChatMessage> ?? history.ToList();
        foreach (var m in historyList)
            messages.Add(new { role = m.Role, content = m.Content });

        // 2026-05-18 — role-flip support. Callers that fold the directive
        // into the system message pass an empty/whitespace userMessage to
        // avoid producing a trailing empty user-role turn. The model sees
        // Mark's actual text via the history list instead.
        if (!string.IsNullOrWhiteSpace(userMessage))
            messages.Add(new { role = "user", content = userMessage });

        // Parrot-bug investigation (Apr 23, 2026): log the full payload being
        // sent to the model. The Apr 23 raw-Ollama diagnostic showed that
        // ani-v7-conversation produces rich, non-parroting replies when given
        // raw turns directly. Parroting appears only through our pipeline's
        // prompt composition. To diagnose which pipeline transform is causing
        // the echo behavior, the journal needs a record of exactly what the
        // model received at the moment it produced the problematic reply.
        // Per-message content truncated to 200 chars to keep log lines
        // manageable; full content is still recoverable from upstream logs
        // (ContextCompressor dumps the summary, ConversationReplyPhase dumps
        // the user prompt, PromptBuilder code is the source of truth for the
        // system prompt shape).
        if (_log.IsEnabled(LogLevel.Debug))
        {
            // Phase K.1 (2026-07-02): systemPrompt can now be null/empty to
            // defer to the Modelfile SYSTEM. Guard the length + preview logs
            // so they don't NPE or spam empty previews on the lean path.
            var systemChars = systemPrompt?.Length ?? 0;
            var systemPresent = !string.IsNullOrWhiteSpace(systemPrompt);

            _log.LogDebug(
                "Ollama [{Model}] payload: {Messages} messages, system={SystemChars}c, history={HistoryCount}, user={UserChars}c",
                model, messages.Count, systemChars, historyList.Count, userMessage.Length);

            // System prompt preview (one line, truncated) — only when present.
            if (systemPresent)
            {
                _log.LogDebug("Ollama [{Model}] payload[0] system ({Chars}c): {Preview}",
                    model, systemChars, Truncate(systemPrompt!, 200));
            }
            else
            {
                _log.LogDebug("Ollama [{Model}] payload[0] system: (omitted — Modelfile SYSTEM in effect)",
                    model);
            }

            // Per-history-message preview. When the system message is present
            // it occupies payload[0], so history entries start at index 1;
            // when the system message is omitted (lean path), history starts
            // at index 0.
            var historyBase = systemPresent ? 1 : 0;
            for (int i = 0; i < historyList.Count; i++)
            {
                var m = historyList[i];
                _log.LogDebug("Ollama [{Model}] payload[{Idx}] {Role} ({Chars}c): {Preview}",
                    model, historyBase + i, m.Role, m.Content.Length, Truncate(m.Content, 200));
            }

            // User-prompt tail (the actual reply directive).
            _log.LogDebug("Ollama [{Model}] payload[{Idx}] user ({Chars}c): {Preview}",
                model, historyBase + historyList.Count, userMessage.Length, Truncate(userMessage, 200));

            // 2026-07-10 — full user-prompt dump per generation (Debug).
            // Rationale: 200-char previews above are load-bearing for readable
            // log-scanning ("what did the composer send this cycle?"), but they
            // hide the full content that matters when a SafeAck / verifier fire
            // needs diagnosis after the fact. Mark, 2026-07-10 12:14 CDT:
            // *"we need to dump it all, at least upon generation so we can see
            // it. it doesn't need to show in full every time but it needs to
            // be there at least one time."* Emitted as a SEPARATE line so log
            // scanners looking for the 200-char preview aren't disrupted, and
            // multiline log parsers can still find the full block via the
            // FULL_USER_PROMPT tag.
            _log.LogDebug(
                "Ollama [{Model}] FULL_USER_PROMPT ({Chars}c):\n{Content}",
                model, userMessage.Length, userMessage);

            // Also dump the full history payload once per generation. History
            // messages contain the composer scaffolding + summary + prior
            // turns that feed the model's context — needed to reconstruct
            // exactly what the model saw at reply time.
            for (int i = 0; i < historyList.Count; i++)
            {
                var m = historyList[i];
                _log.LogDebug(
                    "Ollama [{Model}] FULL_HISTORY[{Idx}] {Role} ({Chars}c):\n{Content}",
                    model, historyBase + i, m.Role, m.Content.Length, m.Content);
            }
        }

        // 2026-05-18: Full-prompt dump at Trace level for parrot-isolation
        // diagnostics. Enable by setting `Logging:LogLevel:AniRuntime.LLM.OllamaClient`
        // to "Trace" in appsettings.Development.json. The 200-char Debug
        // previews above are insufficient to reproduce the SafeAck-triggering
        // prompt out-of-band; this surface lets us dump the exact bytes the
        // model received without modifying the production hot path further.
        // Disabled in production by default (Trace is opt-in per category).
        if (_log.IsEnabled(LogLevel.Trace))
        {
            // Phase K.1 (2026-07-02): systemPrompt can be null on the lean
            // path — dump a "(omitted)" marker rather than NPE.
            _log.LogTrace("Ollama [{Model}] FULL system ({Chars}c):\n{Content}",
                model, systemPrompt?.Length ?? 0, systemPrompt ?? "(omitted — Modelfile SYSTEM in effect)");
            for (int i = 0; i < historyList.Count; i++)
            {
                var m = historyList[i];
                _log.LogTrace("Ollama [{Model}] FULL payload[{Idx}] {Role} ({Chars}c):\n{Content}",
                    model, i + 1, m.Role, m.Content.Length, m.Content);
            }
            _log.LogTrace("Ollama [{Model}] FULL user ({Chars}c):\n{Content}",
                model, userMessage.Length, userMessage);
        }

        // keep_alive controls how long the model stays loaded in VRAM after this request.
        // "0" unloads immediately (used by intent extraction to free VRAM for conversation model).
        // "5m" keeps warm between cognitive cycles without squatting on VRAM forever.
        // AC4: Temperature splitting — when provided, override Ollama's default (0.8).
        //
        // 2026-06-10: include `repeat_penalty` in every chat request. Ollama
        // applies model defaults when no `options` block is sent — qwen3:14b's
        // default (1.1) was too low and led to verbatim-recycling under emotional
        // inbounds (self-echo cascade 21:17 production). ChatRepeatPenalty in
        // AniOptions is the configurable surface; default 1.15.
        var penalty = _options.ChatRepeatPenalty;
        object request;
        if (format is not null && temperature.HasValue)
            request = new { model, messages, stream = false, format, keep_alive = alive, options = new { temperature = temperature.Value, repeat_penalty = penalty } };
        else if (format is not null)
            request = new { model, messages, stream = false, format, keep_alive = alive, options = new { repeat_penalty = penalty } };
        else if (temperature.HasValue)
            request = new { model, messages, stream = false, keep_alive = alive, options = new { temperature = temperature.Value, repeat_penalty = penalty } };
        else
            request = new { model, messages, stream = false, keep_alive = alive, options = new { repeat_penalty = penalty } };

        // Retry with backoff for transient Ollama failures (500s during model swaps, VRAM eviction).
        // Two retries with 3-second backoff handles most model reload scenarios.
        // Per-request timeout of 90 seconds prevents infinite hangs when VRAM is tight.
        const int maxRetries = 2;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestCts.CancelAfter(TimeSpan.FromSeconds(90));

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsJsonAsync(
                    "/api/chat", request, JsonOpts, requestCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-request timeout (90s) — not app shutdown
                if (attempt < maxRetries)
                {
                    _log.LogWarning("Ollama [{Model}] timed out after 90s — retrying in 3s (attempt {Attempt}/{Max})",
                        model, attempt, maxRetries);
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    continue;
                }
                _log.LogError("Ollama [{Model}] timed out after {MaxRetries} attempts — giving up", model, maxRetries);
                throw;
            }

            if (!response.IsSuccessStatusCode && attempt < maxRetries)
            {
                _log.LogWarning("Ollama [{Model}] returned {Status} — retrying in 2s (attempt {Attempt}/{Max})",
                    model, (int)response.StatusCode, attempt, maxRetries);
                await Task.Delay(2000, ct).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct)
                                     .ConfigureAwait(false);

            var content = body?.Message?.Content ?? string.Empty;
            // Parrot-bug investigation (Apr 23, 2026): include a truncated
            // preview of the response so per-cycle log inspection does not
            // require cross-referencing the "Conversation reply:" log line
            // to match up request and response. Still Debug-level.
            _log.LogDebug("Ollama [{Model}] response ({Chars} chars): {Preview}",
                model, content.Length, Truncate(content, 200));
            return content;
        }

        // Unreachable — loop always returns or throws
        throw new InvalidOperationException("Retry loop exited without result");
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var m in history)
            messages.Add(new { role = m.Role, content = m.Content });
        if (!string.IsNullOrWhiteSpace(userMessage))
            messages.Add(new { role = "user", content = userMessage });

        var request = new { model = _options.ChatModel, messages, stream = true, keep_alive = "5m" };

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request, options: JsonOpts),
        };

        var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line)) continue;

            var chunk = JsonSerializer.Deserialize<StreamChatChunk>(line, JsonOpts);
            if (chunk?.Done == true) break;

            var token = chunk?.Message?.Content;
            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }

    public async Task<string> GenerateAsync(
        string model, string prompt, CancellationToken ct = default,
        float? temperature = null, int? maxTokens = null, string? keepAlive = null)
    {
        // Contribution 9 PR-3 (Issue #68) — /api/generate for raw text
        // completion. EmoLLaMA and similar task-tuned base models were
        // fine-tuned on a specific instruction template that the chat-API
        // role split silently breaks. The caller composes the verbatim
        // prompt; we just pass it through.
        var options = new Dictionary<string, object>();
        if (temperature.HasValue) options["temperature"] = temperature.Value;
        if (maxTokens.HasValue)   options["num_predict"] = maxTokens.Value;

        var alive   = keepAlive ?? "5m";
        var request = options.Count > 0
            ? (object)new { model, prompt, stream = false, options, keep_alive = alive }
            : (object)new { model, prompt, stream = false, keep_alive = alive };

        var response = await _http.PostAsJsonAsync(
            "/api/generate", request, JsonOpts, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<GenerateResponse>(JsonOpts, ct)
            .ConfigureAwait(false);

        return body?.Response ?? string.Empty;
    }

    private sealed class GenerateResponse
    {
        [JsonPropertyName("response")] public string? Response { get; set; }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // Short keep_alive for embeddings — the model is small but adds up.
        // 10 seconds is enough for batch embedding, then it frees VRAM for the LLMs.
        var request  = new { model = _options.EmbedModel, prompt = text, keep_alive = "10s" };
        var response = await _http.PostAsJsonAsync(
            "/api/embeddings", request, JsonOpts, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(JsonOpts, ct)
                                 .ConfigureAwait(false);

        return body?.Embedding ?? Array.Empty<float>();
    }

    public async Task WarmModelAsync(string model, CancellationToken ct = default)
    {
        // Send a minimal chat request to force Ollama to load the model into VRAM.
        // The actual response doesn't matter — we just need the model warm.
        var request = new { model, messages = new[] { new { role = "user", content = "hi" } },
            stream = false, keep_alive = "30m" };
        var response = await _http.PostAsJsonAsync("/api/chat", request, JsonOpts, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _log.LogInformation("Model {Model} pre-warmed in VRAM (keep_alive=30m)", model);
    }

    public async Task<string> DescribeImageAsync(byte[] imageBytes, string? prompt = null, CancellationToken ct = default)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var userPrompt = prompt ?? "Describe what you see in this image in 2-3 sentences. Be specific about objects, people, colors, and mood. If there is any text or writing visible, read it exactly as written.";

        // LLaVA needs a dedicated HttpClient with longer timeout — model swap from
        // VRAM (evict conversation model + load 4.7GB LLaVA) can take 3-4 minutes on 6GB VRAM.
        using var llavaClient = new HttpClient
        {
            BaseAddress = _http.BaseAddress,
            Timeout = TimeSpan.FromMinutes(5),
        };

        var request = new
        {
            model = "llava",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = userPrompt,
                    images = new[] { base64 },
                }
            },
            stream = false,
            keep_alive = "0",  // Unload immediately — 6GB VRAM can't hold LLaVA + conversation models
        };

        var response = await llavaClient.PostAsJsonAsync("/api/chat", request, JsonOpts, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct)
            .ConfigureAwait(false);

        var description = body?.Message?.Content?.Trim() ?? "(could not describe image)";
        _log.LogInformation("LLaVA image description ({Bytes} bytes): {Description}",
            imageBytes.Length, description);

        return description;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a single-line preview of <paramref name="text"/> truncated to
    /// <paramref name="maxChars"/>. Newlines are collapsed to spaces so each
    /// log line stays on one line for grep-ability. Added for the Apr 23, 2026
    /// parrot-bug investigation instrumentation.
    /// </summary>
    private static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var collapsed = text.Replace('\n', ' ').Replace('\r', ' ');
        return collapsed.Length <= maxChars ? collapsed : collapsed[..maxChars] + "...";
    }

    // ── Response shapes ───────────────────────────────────────────────────────

    private record ChatResponse(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message);

    private record ChatResponseMessage(
        [property: JsonPropertyName("role")]    string Role,
        [property: JsonPropertyName("content")] string Content);

    private record StreamChatChunk(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message,
        [property: JsonPropertyName("done")]    bool Done);

    private record EmbedResponse(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
