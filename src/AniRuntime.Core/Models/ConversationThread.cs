using AniRuntime.Core;
using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

public class ConversationThread
{
    public Guid                      Id            { get; set; } = Guid.NewGuid();
    public DateTimeOffset            StartedAt     { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset            LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
    public bool                      IsActive      { get; set; } = true;
    public string                    InitiatedBy   { get; set; } = Roles.Mark; // "ani" | "mark"
    public List<ConversationMessage> Messages      { get; set; } = new();

    /// <summary>
    /// Conversation Mode Phase 3: Structured conversation state.
    /// Maintained incrementally from each exchange — no LLM summarization.
    /// Tracks topic, register, commitments, key facts, and shared imagery.
    /// </summary>
    public ConversationState State { get; set; } = new();

    /// <summary>
    /// Feature 34 (MemGPT): Cached summary of compressed older messages.
    /// Not persisted to DB — regenerated when needed during conversation.
    /// </summary>
    public string? CompressedSummary { get; set; }

    /// <summary>
    /// Feature 34: Number of messages the cached summary covers.
    /// If new messages push beyond this, the summary needs regeneration.
    /// </summary>
    public int CompressedSummaryUpToIndex { get; set; }
}

public class ConversationMessage
{
    public string         Role    { get; set; } = Roles.Mark; // "ani" | "mark"
    public string         Content { get; set; } = string.Empty;
    public DateTimeOffset SentAt  { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Cached embedding for echo guard — computed once, reused across checks.
    /// Not persisted to DB; populated lazily during conversation reply processing.
    /// </summary>
    public float[]? CachedEmbedding { get; set; }

    /// <summary>
    /// Foundation Unified Surface (F-3) U9 (2026-08-24) — optional
    /// composer-emission envelope for Ani-authored reply messages.
    /// Populated by the SMS conversation-reply composer at emission
    /// time (<c>ConversationReplyPipeline</c> just after the LLM call
    /// returns), consumed by <c>SqliteConversationService.AddMessageAsync</c>
    /// when persisting the message as an Episodic memory record.
    ///
    /// <para>
    /// When non-null, the persistence layer projects attribution from
    /// the emission via <c>ToAttributionTriple</c> rather than
    /// re-deriving from <see cref="Role"/>. This closes the last
    /// role-switch reconstruction site in the SMS conversation
    /// composer path; the composer's identity + trust marker come
    /// from where they were actually known (the composer boundary),
    /// not from a re-derivation at persist time.
    /// </para>
    ///
    /// <para>
    /// Null on the Mark-inbound path (Twilio perception source
    /// constructs the message without an emission — Mark isn't a
    /// composer). The persistence layer's role-switch handles that
    /// case unchanged, so this field is additive with zero churn
    /// for the twelve other <c>new ConversationMessage</c> sites.
    /// Ephemeral: not persisted to the <c>conversation_messages</c>
    /// row — same pattern as <see cref="CachedEmbedding"/>.
    /// </para>
    /// </summary>
    public IComposerEmission<string>? ComposerEmission { get; set; }
}
