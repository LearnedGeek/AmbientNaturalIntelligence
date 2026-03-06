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

    public async Task<string> ChatAsync(
        string systemPrompt, IEnumerable<ChatMessage> history, string userMessage,
        CancellationToken ct = default)
    {
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        foreach (var m in history)
            messages.Add(new { role = m.Role, content = m.Content });

        messages.Add(new { role = "user", content = userMessage });

        var request = new
        {
            model    = _options.ChatModel,
            messages = messages,
            stream   = false,
        };

        var response = await _http.PostAsJsonAsync(
            "/api/chat", request, JsonOpts, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct)
                                 .ConfigureAwait(false);

        var content = body?.Message?.Content ?? string.Empty;
        _log.LogDebug("Ollama response ({Chars} chars)", content.Length);
        return content;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request  = new { model = _options.EmbedModel, prompt = text };
        var response = await _http.PostAsJsonAsync(
            "/api/embeddings", request, JsonOpts, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(JsonOpts, ct)
                                 .ConfigureAwait(false);

        return body?.Embedding ?? Array.Empty<float>();
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
