using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 8c (2026-08-19) — producer-boundary
/// envelope for the P3 <see cref="IClosedConversationSummarizer"/> output.
/// Wraps the pre-existing <see cref="ClosedConversationRecord"/> class
/// with provenance metadata (SourceType / Producer / CreatedAt) so the
/// close-thread persistence path and any downstream audit surfaces can
/// trace where a closed-conversation record came from without inspecting
/// the record's internal state.
///
/// <para>
/// <see cref="ClosedConversationRecord"/> is already a mutable class
/// (not a record), so the Phase 4 record-equality-preservation motive
/// doesn't apply here. The envelope still exists for two structural
/// reasons: (1) uniform producer/consumer surface across all F-1 wraps
/// so downstream audit code can treat every producer identically; and
/// (2) provenance carries the <c>Validity</c>-aware SourceType tag —
/// <c>closed-conversation.valid</c> vs <c>closed-conversation.invalid-fabrication</c>
/// vs <c>closed-conversation.invalid-other</c> — letting later
/// substrate-selection audits filter on the boundary tag without
/// re-reading the record's fields.
/// </para>
///
/// <para>
/// Passthrough properties (<see cref="ThreadId"/> / <see cref="Gist"/>
/// / <see cref="Validity"/>) forward to the wrapped record so consumers
/// reading only the identity + primary-substrate fields don't need to
/// unwrap.
/// </para>
///
/// <para>
/// <b>Equality contract:</b> envelopes are classes with reference equality
/// by default AND per-instance <c>CreatedAt</c>. Consumers who want value
/// equality compare the wrapped record via
/// <see cref="IProvenancedContent{T}.Content"/>. Same convention as
/// prior Phase 8 envelopes (WorldSeed, OutreachFrame).
/// </para>
/// </summary>
public interface IClosedConversationEnvelope : IProvenancedContent<ClosedConversationRecord>
{
    /// <summary>Passthrough — <c>Content.ThreadId</c>.</summary>
    Guid ThreadId { get; }

    /// <summary>Passthrough — <c>Content.Gist</c>.</summary>
    string Gist { get; }

    /// <summary>Passthrough — <c>Content.Validity</c>.</summary>
    string Validity { get; }
}
