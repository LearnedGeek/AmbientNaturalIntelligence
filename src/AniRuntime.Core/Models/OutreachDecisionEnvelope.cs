using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Canonical <see cref="IOutreachDecisionEnvelope"/> implementation used
/// by the four producer sites that construct an <see cref="OutreachDecision"/>
/// today. Distinguished by <see cref="Source"/> — see the
/// <see cref="OutreachDecisionSource"/> enum values.
///
/// <para>
/// Class (not record) so implementing <see cref="IOutreachDecisionEnvelope"/>
/// with an explicit-interface <see cref="IProvenancedContent{T}.CreatedAt"/>
/// auto-property doesn't affect equality (Phase 4 sibling-impl discipline).
/// The wrapped <see cref="Decision"/> record's own equality semantics are
/// unchanged — consumers unwrapping to <see cref="IProvenancedContent{T}.Content"/>
/// see the same object.
/// </para>
/// </summary>
public sealed class OutreachDecisionEnvelope : IOutreachDecisionEnvelope
{
    /// <summary>
    /// The wrapped decision. Construction-only surface: production readers
    /// should access the wrapped payload through the envelope interface
    /// (<see cref="IProvenancedContent{T}.Content"/>) — that's the canonical
    /// read path. <c>Decision</c> is exposed publicly for readable
    /// construction and test-fixture wrapping; it points at the same
    /// object as <c>Content</c>.
    /// </summary>
    public required OutreachDecision Decision { get; init; }

    /// <summary>
    /// Which producer emitted this envelope. Determines the SourceType tag
    /// and Producer name so downstream audit can distinguish LLM-driven
    /// outreach from admin-meta / reply / reactive-share without inspecting
    /// the record's <see cref="OutreachDecision.IsAdminMeta"/> field or the
    /// <see cref="OutreachDecision.Reasoning"/> free-text.
    /// </summary>
    public required OutreachDecisionSource Source { get; init; }

    // ── IOutreachDecisionEnvelope passthroughs ──────────────────────────
    /// <inheritdoc />
    public bool ShouldReach => Decision.ShouldReach;
    /// <inheritdoc />
    public float Confidence => Decision.Confidence;
    /// <inheritdoc />
    public bool IsAdminMeta => Decision.IsAdminMeta;

    // ── IProvenancedContent<OutreachDecision> ───────────────────────────
    /// <inheritdoc />
    OutreachDecision IProvenancedContent<OutreachDecision>.Content => Decision;

    /// <inheritdoc />
    /// <remarks>
    /// Kebab-case tags matching the sibling envelope convention
    /// (<c>frame.ani-interior</c>, <c>world-seed.circadian</c>,
    /// <c>closed-conversation.valid</c>). Producer prefix
    /// <c>outreach-decision.</c> plus per-producer suffix.
    /// </remarks>
    string IProvenancedContent<OutreachDecision>.SourceType => Source switch
    {
        OutreachDecisionSource.LlmParsed        => "outreach-decision.llm-parsed",
        OutreachDecisionSource.LlmParseFailure  => "outreach-decision.llm-parse-failure",
        OutreachDecisionSource.AdminMeta        => "outreach-decision.admin-meta",
        OutreachDecisionSource.SmsReply         => "outreach-decision.sms-reply",
        OutreachDecisionSource.ReactiveShare    => "outreach-decision.reactive-share",
        _                                       => "outreach-decision.unknown",
    };

    /// <inheritdoc />
    string IProvenancedContent<OutreachDecision>.Producer => Source switch
    {
        OutreachDecisionSource.LlmParsed        => "OutreachPipeline",
        OutreachDecisionSource.LlmParseFailure  => "OutreachPipeline",
        OutreachDecisionSource.AdminMeta        => "AdminCommandHandler",
        OutreachDecisionSource.SmsReply         => "SmsReplyChannel",
        OutreachDecisionSource.ReactiveShare    => "ReactiveShareService",
        _                                       => "unknown",
    };

    /// <inheritdoc />
    /// <remarks>
    /// Captured once at construction per <c>IProvenancedContent&lt;T&gt;.CreatedAt</c>
    /// contract (sibling-impl discipline from PR #112). Class not record
    /// so this doesn't affect equality.
    /// </remarks>
    DateTimeOffset IProvenancedContent<OutreachDecision>.CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    float[]? IProvenancedContent<OutreachDecision>.SemanticKey => null;
}

/// <summary>
/// The four production sites that construct an <see cref="OutreachDecision"/>
/// today. See <see cref="IOutreachDecisionEnvelope"/> XML doc for the
/// producer census.
/// </summary>
public enum OutreachDecisionSource
{
    /// <summary>LLM outreach-decision JSON parse (desire-driven spontaneous outreach).</summary>
    LlmParsed = 0,

    /// <summary>Administrative meta-dispatch (///tag confirmations, ///help output).</summary>
    AdminMeta = 1,

    /// <summary>Conversation-reply dispatch via SMS after an inbound webhook.</summary>
    SmsReply = 2,

    /// <summary>Reactive-share flow — sharing a desire-driven observation with the contact.</summary>
    ReactiveShare = 3,

    /// <summary>
    /// PR #121 review-fix (Devin): LLM outreach-decision JSON parse threw and
    /// the pipeline substituted a suppress-outreach fallback. Distinguishes
    /// audit dashboards' "LLM said no" (LlmParsed with ShouldReach=false)
    /// from "LLM output was malformed" (this) without inspecting the
    /// <see cref="OutreachDecision.Reasoning"/> free-text.
    /// </summary>
    LlmParseFailure = 4,
}
