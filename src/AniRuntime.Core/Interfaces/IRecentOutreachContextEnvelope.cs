using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 8e (2026-08-19) — producer-boundary
/// envelope for the P13 <see cref="RecentOutreachContext"/> produced by
/// <c>StateContextBuilder.BuildOutreachContext</c> once per cognitive
/// cycle (Feature 27 outreach continuity context).
///
/// <para>
/// <see cref="RecentOutreachContext"/> is a mutable class that carries
/// the last-N outreach records, unanswered-count, and time-since-last
/// signals. It's assembled inside <c>StateContextBuilder</c> and stashed
/// into <c>StateContextResult.OutreachContext</c> which flows into
/// <c>ContextSnapshot.OutreachContext</c>. Consumers (outreach + outreach
/// message prompts, cognitive-cycle pipeline) read the snapshot property
/// directly.
/// </para>
///
/// <para>
/// This envelope wraps the record only at the producer boundary; the
/// immediate caller (<c>StateContextBuilder.BuildAsync</c>) unwraps via
/// <see cref="IProvenancedContent{T}.Content"/> to construct the
/// <c>StateContextResult</c>. Downstream consumer contracts
/// (<c>StateContextResult</c>, <c>ContextSnapshot</c>, prompt commands)
/// continue to consume the bare <see cref="RecentOutreachContext"/>
/// record — same wrap-then-unwrap-at-first-caller pattern as
/// Phase 8c (<c>ClosedConversationEnvelope</c> unwrapped in
/// <c>SqliteConversationService.CloseThreadAsync</c>).
/// </para>
///
/// <para>
/// <b>Single-producer surface:</b> unlike Phase 8a/8d (multi-producer),
/// there is exactly one producer today — the recent-episodic scan in
/// <c>StateContextBuilder</c>. SourceType is therefore hardcoded to
/// <c>"recent-outreach-context.recent-episodic"</c> without a source
/// enum. If a future producer materializes (e.g., loaded from a
/// persisted continuity store), the SourceType tag can differentiate at
/// that point.
/// </para>
///
/// <para>
/// <b>Consumer contract in this phase (2026-08-19):</b> per the F-1
/// pattern established through Phase 8a/8b/8c/8d, this phase establishes
/// the producer-boundary surface only. Provenance fields
/// (<see cref="IProvenancedContent{T}.SourceType"/> /
/// <see cref="IProvenancedContent{T}.Producer"/> /
/// <see cref="IProvenancedContent{T}.CreatedAt"/>) are unread by any
/// production consumer today; the shipped value is the drift-free
/// producer-side wrap.
/// </para>
///
/// <para>
/// Passthrough <see cref="UnansweredCount"/> forwards the routing-relevant
/// field so consumers reading only the gate-relevant signal don't need
/// to unwrap.
/// </para>
///
/// <para>
/// <b>Equality contract:</b> envelopes are classes with reference
/// equality by default AND per-instance <c>CreatedAt</c>. Consumers who
/// want value equality compare the wrapped record via
/// <see cref="IProvenancedContent{T}.Content"/>. Same convention as prior
/// Phase 8 envelopes.
/// </para>
/// </summary>
public interface IRecentOutreachContextEnvelope : IProvenancedContent<RecentOutreachContext>
{
    /// <summary>Passthrough — <c>Content.UnansweredCount</c>.</summary>
    int UnansweredCount { get; }
}
