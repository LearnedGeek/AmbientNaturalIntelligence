using AniRuntime.Core.Models;

namespace AniRuntime.Voice.Abstractions;

/// <summary>
/// Owns the LLM-side of a voice turn: warming the conversation model on call
/// start, generating a reply from a context snapshot + thread + buffered
/// messages, and shaping the reply for voice playback (clean + truncate to
/// ~2 sentences). Returns <c>null</c> if the model produces an empty reply
/// after cleaning — the orchestrator chooses the fallback prompt.
/// </summary>
public interface IVoiceReplyGenerator
{
    /// <summary>
    /// Warm the conversation model. Cognitive cycles may evict it from VRAM,
    /// so this runs concurrently with greeting TTS on call start.
    /// </summary>
    Task WarmAsync(CancellationToken ct);

    /// <summary>
    /// Generate the spoken reply text. Returns <c>null</c> when the model
    /// produces an empty reply after cleaning.
    /// </summary>
    Task<string?> GenerateAsync(
        ContextSnapshot snapshot,
        ConversationThread thread,
        IReadOnlyList<ConversationMessage> bufferedMessages,
        CancellationToken ct);
}
