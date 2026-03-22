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

    private async Task<string> SendChatAsync(
        string model, string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        string? format, CancellationToken ct, float? temperature = null, string? keepAlive = null)
    {
        var alive = keepAlive ?? "5m";

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        foreach (var m in history)
            messages.Add(new { role = m.Role, content = m.Content });

        messages.Add(new { role = "user", content = userMessage });

        // keep_alive controls how long the model stays loaded in VRAM after this request.
        // "0" unloads immediately (used by intent extraction to free VRAM for conversation model).
        // "5m" keeps warm between cognitive cycles without squatting on VRAM forever.
        // AC4: Temperature splitting — when provided, override Ollama's default (0.8).
        object request;
        if (format is not null && temperature.HasValue)
            request = new { model, messages, stream = false, format, keep_alive = alive, options = new { temperature = temperature.Value } };
        else if (format is not null)
            request = new { model, messages, stream = false, format, keep_alive = alive };
        else if (temperature.HasValue)
            request = new { model, messages, stream = false, keep_alive = alive, options = new { temperature = temperature.Value } };
        else
            request = new { model, messages, stream = false, keep_alive = alive };

        // Retry with backoff for transient Ollama failures (500s during model swaps).
        // One retry after 2 seconds handles most swap-related timeouts without
        // blocking the cognitive cycle for the full cooldown period.
        const int maxRetries = 2;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var response = await _http.PostAsJsonAsync(
                "/api/chat", request, JsonOpts, ct).ConfigureAwait(false);

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
            _log.LogDebug("Ollama [{Model}] response ({Chars} chars)", model, content.Length);
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
