using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Builds the conversation-history portion of <see cref="ContextSnapshot"/>:
/// prose <c>RecentConversationSummary</c> (extracted from recent episodic
/// memory), J.2 <c>StructuredConversationSummary</c> (per-speaker per-turn
/// from <see cref="IConversationService"/>), and Vibe Loop V1.4
/// <c>RecentClosedConversation</c> gist (from
/// <see cref="IClosedConversationStore"/>).
///
/// Extracted 2026-05-19 as the third sub-builder of the ContextBuilder SRP
/// decomposition (`ANI-Testability-Architecture-Plan.md` §2). Production impl:
/// <c>ConversationContextBuilder</c> in <c>AniRuntime.Loops.Context</c>.
/// </summary>
public interface IConversationContextBuilder
{
    /// <summary>
    /// Build the conversation-context block. Each of the three concerns is
    /// independently null-guarded: missing upstream services produce null
    /// fields, retrieval failures log a warning and produce null fields.
    /// </summary>
    /// <param name="characterState">
    /// Used only for speaker-name mapping in the structured summary turns
    /// (Ani vs. contact-name). The orchestrator loads this once and passes it
    /// down to avoid duplicate state reads.
    /// </param>
    /// <param name="recentMemory">
    /// Recent memory pool (typically from <see cref="IRetrievalContextBuilder"/>).
    /// Scanned for an episodic record whose content starts with the conversation-
    /// summary prefix; that record's content becomes <c>RecentConversationSummary</c>.
    /// </param>
    Task<ConversationContextResult> BuildAsync(
        CharacterStateDoc               characterState,
        IReadOnlyList<MemoryRecord>     recentMemory,
        CancellationToken               ct);
}
