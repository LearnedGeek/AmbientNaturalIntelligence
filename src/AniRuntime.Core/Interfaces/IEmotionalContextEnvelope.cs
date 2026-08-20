using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 8f (2026-08-20) — producer-boundary
/// envelope for the P14 <see cref="EmotionalContextResult"/> produced by
/// <see cref="IEmotionalContextBuilder.BuildAsync"/> once per cognitive
/// cycle. Carries relationship health, emotional drift, pattern
/// awareness, processed themes, and contribution trajectory into
/// <c>ContextSnapshot</c>.
///
/// <para>
/// <see cref="EmotionalContextResult"/> is a <c>sealed record</c>.
/// Wrapping in a separate envelope class (rather than making the record
/// itself implement <see cref="IProvenancedContent{T}"/> directly) is
/// the Phase 4 sibling-impl discipline — records with a stored
/// <c>CreatedAt</c> break value equality across per-instance timestamps;
/// classes are exempt. Same shape as Phase 8b's
/// <c>OutreachFrameEnvelope</c>.
/// </para>
///
/// <para>
/// This envelope wraps the record only at the producer boundary; the
/// immediate caller (<c>ContextBuilder</c>) unwraps via
/// <see cref="IProvenancedContent{T}.Content"/> and stashes the five
/// component fields into <c>ContextSnapshot</c>. Downstream consumers
/// (composers reading snapshot.RelationshipHealth /
/// snapshot.EmotionalDrift / etc.) are unchanged — no interface
/// signature migration.
/// </para>
///
/// <para>
/// <b>Consumer contract in this phase (2026-08-20):</b> per the F-1
/// pattern established through Phase 8a–8e, this phase establishes the
/// producer-boundary surface only. Provenance fields
/// (<see cref="IProvenancedContent{T}.SourceType"/> /
/// <see cref="IProvenancedContent{T}.Producer"/> /
/// <see cref="IProvenancedContent{T}.CreatedAt"/>) are unread by any
/// production consumer today; the shipped value is the drift-free
/// producer-side wrap. Phase 8f closes the producer-side surface work
/// for F-1 — the consumer-wire PR that migrates dispatcher / composer /
/// audit-log paths to consume envelope provenance is the next work.
/// </para>
///
/// <para>
/// <b>Equality contract:</b> envelopes are classes with reference equality
/// by default AND per-instance <c>CreatedAt</c> — two envelopes wrapping
/// the same-value record will NOT compare equal via <c>Equals</c> /
/// <c>GetHashCode</c>. Consumers who want value equality compare the
/// wrapped <see cref="IProvenancedContent{T}.Content"/> record, which
/// retains its record value-equality semantics unchanged. Same convention
/// as prior Phase 8 record-wrap envelopes (OutreachFrameEnvelope,
/// ClosedConversationEnvelope).
/// </para>
/// </summary>
public interface IEmotionalContextEnvelope : IProvenancedContent<EmotionalContextResult>
{
}
