using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.LLM;

public class OllamaClient : IOllamaClient
{
    private readonly HttpClient            _http;
    private readonly OllamaOptions         _options;
    private readonly ILogger<OllamaClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public OllamaClient(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaClient> log)
    {
        _http    = http;
        _options = options.Value;
        _log     = log;
    }

    public Task<string> ChatAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default)
        => SendChatAsync(_options.ChatModel, systemPrompt, history, userMessage, format: null, ct);

    public Task<string> ChatJsonAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default)
        => SendChatAsync(_options.ChatModel, systemPrompt, history, userMessage, format: "json", ct);

    public Task<string> InnerMonologueChatAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default)
        => SendChatAsync(_options.ResolvedInnerMonologueModel, systemPrompt, history, userMessage, format: null, ct);

    private async Task<string> SendChatAsync(
        string model, string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        string? format, CancellationToken ct)
    {
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        foreach (var m in history)
            messages.Add(new { role = m.Role, content = m.Content });

        messages.Add(new { role = "user", content = userMessage });

        // keep_alive controls how long the model stays loaded in VRAM after this request.
        // 5 minutes keeps the model warm between cognitive cycles without squatting on VRAM forever.
        object request = format is not null
            ? new { model, messages, stream = false, format, keep_alive = "5m" }
            : new { model, messages, stream = false, keep_alive = "5m" };

        var response = await _http.PostAsJsonAsync(
            "/api/chat", request, JsonOpts, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct)
                                 .ConfigureAwait(false);

        var content = body?.Message?.Content ?? string.Empty;
        _log.LogDebug("Ollama [{Model}] response ({Chars} chars)", model, content.Length);
        return content;
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

    private record EmbedResponse(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
