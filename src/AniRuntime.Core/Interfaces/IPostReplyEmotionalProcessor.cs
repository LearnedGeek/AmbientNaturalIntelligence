using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Applies all emotional-state side effects after a conversation reply
/// has dispatched. Five concerns are bundled here because they all run
/// once per inbound message and share the same trigger point:
/// <list type="number">
///   <item>Emotional shift from the conversation context (the reply
///   itself is now substrate).</item>
///   <item>Feature 10 — care-giving intent detection ("are you okay?").</item>
///   <item>Feature 19 — lexical emotional anchors (warmth/hurt keyword set).</item>
///   <item>Feature 18 — hurt detection + reactive withdrawal trigger.</item>
///   <item>Hurt acknowledgment Interior-tier memory write.</item>
/// </list>
///
/// <para>
/// Extracted from <c>ConversationReplyPipeline</c> in §5.4c SOLID
/// refactor (2026-05-18). Owns its own EmotionalProcessor +
/// IMemoryAnalytics surface so the pipeline drops both as direct deps.
/// </para>
/// </summary>
public interface IPostReplyEmotionalProcessor
{
    /// <summary>
    /// Run all five post-reply emotional effects. Best-effort: each
    /// sub-step logs + continues on failure (consistent with the prior
    /// inline behaviour). Safe to call when <paramref name="emotionalState"/>
    /// is <c>null</c> — the method becomes a no-op.
    /// </summary>
    Task ProcessAsync(
        string lastMessage,
        string reply,
        ContextSnapshot snapshot,
        EmotionalState? emotionalState,
        CancellationToken ct);
}
