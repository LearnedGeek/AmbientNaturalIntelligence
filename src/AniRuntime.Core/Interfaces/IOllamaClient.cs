using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

public interface IOllamaClient
{
    Task<string>  ChatAsync(string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default);
    Task<string>  ChatJsonAsync(string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default);
    Task<string>  InnerMonologueChatAsync(string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
