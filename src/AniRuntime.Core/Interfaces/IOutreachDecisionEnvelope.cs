using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 8d (2026-08-19) — producer-boundary
/// envelope for the <see cref="OutreachDecision"/> record. Four
/// semantically-distinct producers construct an <c>OutreachDecision</c>
/// today and hand it to <c>AniActionDispatcher.DispatchAsync</c>:
/// <list type="bullet">
///   <item><c>OutreachPipeline.ParseOutreachDecision</c> — LLM outreach-decision
///         JSON parse. The desire-driven spontaneous outreach path.</item>
///   <item><c>AdminCommandHandler.SendAdminReplyAsync</c> — administrative
///         meta-dispatch (///tag confirmations, ///help output). Flagged
///         <c>IsAdminMeta = true</c> so dispatch skips voice/image enrichment.</item>
///   <item><c>SmsReplyChannel.SendReplyAsync</c> — conversation-reply
///         dispatch via SMS after an inbound webhook.</item>
///   <item><c>ReactiveShareService</c> — reactive-share flow, sharing a
///         desire-driven observation with the contact.</item>
/// </list>
/// Wrapping all four in the same envelope surface, distinguished by
/// <see cref="IProvenancedContent{T}.SourceType"/>, is the mechanism by
/// which downstream audit surfaces (dispatch logs, telemetry, dashboards)
/// will eventually be able to distinguish the LLM-driven outreach path
/// from admin-meta / reply / reactive-share without inspecting the
/// record's <see cref="OutreachDecision.IsAdminMeta"/> field or the
/// <see cref="OutreachDecision.Reasoning"/> free-text.
///
/// <para>
/// <b>Consumer contract in this phase (2026-08-19):</b> this phase
/// establishes the producer-boundary surface only. There is NO active
/// production consumer of the envelope's provenance fields
/// (<see cref="IProvenancedContent{T}.SourceType"/>,
/// <see cref="IProvenancedContent{T}.Producer"/>,
/// <see cref="IProvenancedContent{T}.CreatedAt"/>) — every producer wraps
/// and immediately unwraps back to <c>.Content</c> before calling
/// <c>DispatchAsync</c>. Dispatcher and <c>IAniAction.ExecuteAsync</c>
/// signatures continue to accept the bare <see cref="OutreachDecision"/>
/// record. Reviewer-noted (Devin) on PR #121: the audit-consumer benefit
/// is aspirational until a follow-up phase migrates the dispatcher to
/// consume the envelope (or wires provenance logging at the routing
/// boundary). This phase's shipped value is the drift-free producer-side
/// surface across all four production sites — mirroring the Phase
/// 8a/8b/8c pattern.
/// </para>
///
/// <para>
/// Passthrough properties forward the identity + gate-relevant fields
/// (<see cref="ShouldReach"/> / <see cref="Confidence"/> / <see cref="IsAdminMeta"/>)
/// so consumers reading only the routing signals don't need to unwrap.
/// <b>These are LIVE reads of the mutable wrapped record, not snapshots
/// at wrap time.</b> The wrapped <see cref="OutreachDecision"/> is a
/// mutable class and <c>OutreachPipeline</c> mutates it post-wrap
/// (setting <c>Message</c> + <c>ActionType</c> after gate + composition
/// steps). Consumers wanting a stable snapshot of the fields at producer
/// time should defensively copy from the wrapped record; the envelope
/// itself does not snapshot. <c>Message</c> is deliberately NOT exposed
/// as a passthrough — post-composition mutation of that field would make
/// the passthrough race with pipeline state.
/// </para>
///
/// <para>
/// <b>Equality contract:</b> envelopes are classes with reference equality
/// by default AND per-instance <c>CreatedAt</c>. Consumers who want value
/// equality compare the wrapped record via
/// <see cref="IProvenancedContent{T}.Content"/>. Same convention as prior
/// Phase 8 envelopes.
/// </para>
/// </summary>
public interface IOutreachDecisionEnvelope : IProvenancedContent<OutreachDecision>
{
    /// <summary>Passthrough — <c>Content.ShouldReach</c>.</summary>
    bool ShouldReach { get; }

    /// <summary>Passthrough — <c>Content.Confidence</c>.</summary>
    float Confidence { get; }

    /// <summary>Passthrough — <c>Content.IsAdminMeta</c>.</summary>
    bool IsAdminMeta { get; }
}
