using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 8b (2026-08-19) — producer-boundary
/// envelope for the P10 <see cref="IOutreachFrameSelector"/> output.
/// Wraps the pre-existing <see cref="OutreachFrame"/> record with
/// provenance metadata (SourceType / Producer / CreatedAt) so downstream
/// pipeline branches and audit surfaces can trace where a frame decision
/// came from without inspecting the record's internal state.
///
/// <para>
/// <see cref="OutreachFrame"/> is a <c>sealed record</c>. Wrapping in a
/// separate envelope class (rather than making the record itself
/// implement <see cref="IProvenancedContent{T}"/> directly) is the Phase
/// 4 sibling-impl discipline — records with a stored <c>CreatedAt</c>
/// break value equality across per-instance timestamps; classes are
/// exempt. Same shape as Phase 8a's <c>WorldSeedEnvelope</c>.
/// </para>
///
/// <para>
/// Passthrough properties (<see cref="FrameType"/> / <see cref="Anchor"/>
/// / <see cref="Confidence"/>) forward to the wrapped record so
/// consumers doing <c>envelope.FrameType</c> read as clearly as the
/// pre-Phase-8b <c>frame.FrameType</c>.
/// </para>
///
/// <para>
/// <b>Equality contract:</b> envelopes are classes with reference
/// equality by default AND per-instance <c>CreatedAt</c> — two envelopes
/// wrapping the same-value record will NOT compare equal via
/// <c>Equals</c> / <c>GetHashCode</c>. Consumers who want value equality
/// (e.g. determinism assertions, dedup, memoization) compare the wrapped
/// <see cref="IProvenancedContent{T}.Content"/> record, which retains
/// its record value-equality semantics unchanged. Reviewer-noted (Devin)
/// on PR #119 so future consumers don't silently see every selector
/// result as distinct.
/// </para>
/// </summary>
public interface IOutreachFrameEnvelope : IProvenancedContent<OutreachFrame>
{
    /// <summary>Passthrough — <c>Content.FrameType</c>.</summary>
    OutreachFrameType FrameType { get; }

    /// <summary>Passthrough — <c>Content.Anchor</c>.</summary>
    string Anchor { get; }

    /// <summary>Passthrough — <c>Content.Confidence</c>.</summary>
    float Confidence { get; }
}
