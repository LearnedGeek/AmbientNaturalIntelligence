using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

public interface IOllamaClient
{
    Task<string>  ChatAsync(string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default, float? temperature = null);
    Task<string>  ChatJsonAsync(string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default);
    Task<string>  InnerMonologueChatAsync(string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default, string? keepAlive = null);

    /// <summary>
    /// Phase 6 v1.2 R3 (May 17, 2026) — JSON-format variant of the
    /// inner-monologue chat for reflection synthesis. Forces the model to
    /// output structured JSON matching the requested schema. Used by
    /// <c>ReflectionPhase</c> to produce register-tagged affective summaries
    /// instead of free-prose first-person observations. See
    /// docs/spec/design/ANI-MemoryReform-Design.md v1.2 Refinement 3.
    /// </summary>
    Task<string>  InnerMonologueChatJsonAsync(string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default, string? keepAlive = null);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Stream LLM tokens as they are generated. Each yielded string is one token.
    /// Used by the streaming voice pipeline to start TTS before the full reply is complete.
    /// </summary>
    IAsyncEnumerable<string> ChatStreamAsync(string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default);

    /// <summary>
    /// Pre-load a model into VRAM so the first real request doesn't pay cold-start latency.
    /// </summary>
    Task WarmModelAsync(string model, CancellationToken ct = default);

    /// <summary>
    /// Describe an image using a vision model (LLaVA). Returns a text description
    /// of the image content. Used to give Ani the ability to "see" images sent via MMS.
    /// </summary>
    Task<string> DescribeImageAsync(byte[] imageBytes, string? prompt = null, CancellationToken ct = default);
}
