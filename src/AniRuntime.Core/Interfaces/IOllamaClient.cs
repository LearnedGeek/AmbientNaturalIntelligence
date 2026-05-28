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

    /// <summary>
    /// Phase 6 v1.2 R3.1 (May 17, 2026) — general-purpose JSON-mode chat
    /// against an explicitly-named model. Used by <c>ReflectionPhase</c> to
    /// call the local utility/verifier model (e.g. qwen3:14b) for synthesis
    /// tasks that need structured output without the fine-tuned-character
    /// register bias of <c>ani-v7-inner</c>.
    ///
    /// Empirical motivation: the 2026-05-17 out-of-band R3 probe showed
    /// <c>ani-v7-inner</c> producing malformed JSON (multiple <c>"summaries"</c>
    /// keys, one per source memory) and echoing verbatim source content into
    /// the summary "shape" field — fighting the training. Switching the
    /// synthesis call to the non-fine-tuned local model (Qwen 14B) is the
    /// structural fix: the right tool for compression-style summarization.
    /// </summary>
    Task<string>  ChatJsonWithModelAsync(string model, string systemPrompt, IEnumerable<ChatMessage> history, string userMessage, CancellationToken ct = default, string? keepAlive = null);

    /// <summary>
    /// Contribution 9 PR-3 (Issue #68, May 28, 2026) — raw text-completion
    /// against an explicitly-named model via Ollama's <c>/api/generate</c>
    /// endpoint. Required for models that were fine-tuned with a
    /// non-chat-format instruction template (e.g. EmoLLaMA's
    /// Human:/Task:/Text:/Emotion:/Intensity Score: shape, where the
    /// chat-API role split silently breaks the trained behavior).
    /// </summary>
    /// <param name="model">Explicit Ollama model name.</param>
    /// <param name="prompt">Verbatim prompt — caller composes the full
    /// template; no role-split, no implicit system message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="temperature">Optional sampling temperature. Default 0.0
    /// for deterministic task output.</param>
    /// <param name="maxTokens">Optional cap on generated tokens. Default 16
    /// — task outputs (single number or short emotion list) need very few
    /// tokens; capping prevents the model from drifting into narrative
    /// completion.</param>
    /// <param name="keepAlive">Optional keep-alive override.</param>
    Task<string> GenerateAsync(
        string model,
        string prompt,
        CancellationToken ct = default,
        float? temperature = null,
        int? maxTokens = null,
        string? keepAlive = null);

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
