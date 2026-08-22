using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// A single turn in a chat-history array passed to an LLM. Role is the
/// chat-completion role (<c>"user"</c> / <c>"assistant"</c> / <c>"system"</c>);
/// Content is the turn text.
///
/// <para>
/// F-2 Phase 1 P5 (2026-08-22) — added per-turn attribution metadata so
/// the chat-history injection path (Hop 3 of the substrate-feedback loop
/// per the F-2 audit doc) can surface who authored each turn beyond the
/// raw role signal. The invocation-time framing block reads these fields
/// and prepends a system-prompt segment that competes with the raw-role
/// signal, addressing the load-bearing gap in the 2026-08-20 substrate
/// feedback finding where Ani's own 11:29 SMS reply flowed unlabeled
/// into the 11:56 inner-thought composer as a raw <c>role=assistant</c>
/// turn and got misattributed by 12:04 into a Mark-utterance claim.
/// </para>
///
/// <para>
/// Defaults preserve backward compatibility: existing <c>new ChatMessage(role, content)</c>
/// calls get <c>AttributedTo=Unknown</c> + <c>AttributionTrust="unverified"</c>
/// (the same schema-default state P2 gives to MemoryRecord). Callers that
/// have attribution info populate the init-only properties explicitly.
/// </para>
/// </summary>
public record ChatMessage(string Role, string Content)
{
    /// <summary>
    /// Who authored the turn's content. Default <see cref="AttributedTo.Unknown"/>
    /// — construction sites that can determine attribution from the source
    /// (e.g. <c>ConversationMessage.Role</c> = <c>"mark"</c> → <see cref="AttributedTo.Mark"/>)
    /// populate this explicitly.
    /// </summary>
    public AttributedTo AttributedTo { get; init; } = AttributedTo.Unknown;

    /// <summary>
    /// Attribution trust — same values as <see cref="MemoryRecord.AttributionTrust"/>:
    /// <c>"verified"</c> / <c>"unverified"</c> / <c>"unverified-historical"</c>.
    /// Default <c>"unverified"</c> — construction sites with high-confidence
    /// source (Twilio inbound with role=mark; Ani's own composed reply)
    /// upgrade to <c>"verified"</c> explicitly.
    /// </summary>
    public string AttributionTrust { get; init; } = "unverified";

    /// <summary>
    /// FK-like reference to the source <see cref="MemoryRecord.Id"/> when
    /// the turn was persisted as a MemoryRecord. Null when the turn is
    /// ephemeral (transient conversation-history reconstruction, test
    /// fixture, etc.).
    /// </summary>
    public Guid? AttributedSourceRecordId { get; init; }
}
