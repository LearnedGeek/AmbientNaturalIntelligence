using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using AniRuntime.Voice.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice.Internal;

/// <summary>
/// Owns the conversation-model side of a voice turn — warming, prompt
/// building, the chat call, and voice-shaping (clean + 2-sentence
/// truncation). Returns <c>null</c> for empty replies so the orchestrator
/// can choose a fallback prompt instead of "saying nothing."
/// </summary>
public sealed class VoiceReplyGenerator : IVoiceReplyGenerator
{
    private const int MaxHistoryMessages   = 10;
    private const int MaxReplySentences    = 2;

    private readonly IOllamaClient _ollama;
    private readonly OllamaOptions _ollamaOptions;
    private readonly AniOptions _aniOptions;
    private readonly IEpistemicSubstrateRenderer? _epistemicRenderer;
    private readonly ILogger<VoiceReplyGenerator> _log;

    public VoiceReplyGenerator(
        IOllamaClient ollama,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<VoiceReplyGenerator> log,
        IOptions<AniOptions>? aniOptions = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
    {
        _ollama            = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _ollamaOptions     = ollamaOptions.Value;
        _aniOptions        = aniOptions?.Value ?? new AniOptions();
        _epistemicRenderer = epistemicRenderer;
        _log               = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task WarmAsync(CancellationToken ct) =>
        _ollama.WarmModelAsync(_ollamaOptions.ChatModel, ct);

    public async Task<string?> GenerateAsync(
        ContextSnapshot snapshot,
        ConversationThread thread,
        IReadOnlyList<ConversationMessage> bufferedMessages,
        CancellationToken ct)
    {
        var rendererForPrompt = _aniOptions.EpistemicFramingEnabled ? _epistemicRenderer : null;
        // 2026-05-20 — voice path mirrors the May 18 SMS-path role-flip fix.
        // Reuses LeanConversationPromptDirectiveInSystem flag so both reply
        // paths flip together. See D-005 in ANI-Decisions-Pending.md (resolved).
        var prompt = PromptBuilder.BuildVoiceReplyPrompt(
            snapshot, thread, rendererForPrompt,
            directiveInSystem: _aniOptions.LeanConversationPromptDirectiveInSystem);

        var history = bufferedMessages
            .TakeLast(MaxHistoryMessages)
            .Select(m => new ChatMessage(
                m.Role == Roles.Mark ? "user" : "assistant",
                m.Content));

        var reply = await _ollama.ChatAsync(prompt.System, history, prompt.User, ct).ConfigureAwait(false);

        reply = MessageCleaner.Clean(reply) ?? string.Empty;
        reply = TruncateForVoice(reply);

        if (string.IsNullOrWhiteSpace(reply))
        {
            _log.LogWarning("Voice: LLM returned empty reply");
            return null;
        }

        return reply;
    }

    /// <summary>
    /// Voice models don't reliably respect word-count instructions — truncate
    /// to two terminating punctuation marks so the TTS budget isn't blown
    /// by a multi-paragraph reply.
    /// </summary>
    internal static string TruncateForVoice(string reply)
    {
        var count = 0;
        for (var i = 0; i < reply.Length; i++)
        {
            if (reply[i] is '.' or '!' or '?')
            {
                if (reply[i] == '.' && i + 1 < reply.Length && reply[i + 1] == '.')
                    continue;
                count++;
                if (count >= MaxReplySentences)
                    return reply[..(i + 1)].Trim();
            }
        }
        return reply;
    }
}
