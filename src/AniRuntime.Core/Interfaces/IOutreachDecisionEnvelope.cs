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
/// <see cref="IProvenancedContent{T}.SourceType"/>, means downstream audit
/// (dispatch logs, telemetry, dashboards) can distinguish the LLM-driven
/// outreach path from admin-meta / reply / reactive-share without inspecting
/// the record's <see cref="OutreachDecision.IsAdminMeta"/> field or the
/// <see cref="OutreachDecision.Reasoning"/> free-text.
///
/// <para>
/// <b>Consumer contract in this phase:</b> the envelope is producer-side
/// provenance only. All four producers currently wrap-then-unwrap
/// immediately at the <c>DispatchAsync</c> call site — dispatcher and
/// <c>IAniAction.ExecuteAsync</c> signatures continue to accept the bare
/// <see cref="OutreachDecision"/> record. A follow-up phase could migrate
/// the dispatcher to consume the envelope so provenance is logged at the
/// routing boundary; kept out of scope here to keep blast radius aligned
/// with the one-producer-surface-per-PR cadence established by Phase
/// 8a/8b/8c.
/// </para>
///
/// <para>
/// Passthrough properties forward the identity + gate-relevant fields
/// (<see cref="ShouldReach"/> / <see cref="Confidence"/> / <see cref="IsAdminMeta"/>)
/// so consumers reading only the routing signals don't need to unwrap.
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
