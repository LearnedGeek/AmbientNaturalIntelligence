using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Canonical <see cref="IOutreachFrameEnvelope"/> implementation. Wraps
/// the pre-existing <see cref="OutreachFrame"/> record with
/// producer-boundary provenance for F-1 Phase 8b (P10 wrap).
///
/// <para>
/// Class (not record) so implementing the envelope with an
/// explicit-interface <see cref="IProvenancedContent{T}.CreatedAt"/>
/// auto-property doesn't affect equality — the Phase 4 sibling-impl
/// discipline. The wrapped <see cref="Frame"/> record retains its
/// value-equality semantics unchanged (consumers that pattern-match on
/// the record continue to work).
/// </para>
///
/// <para>
/// Serialization note: <see cref="OutreachFrameEnvelope"/> is not
/// currently JSON- or EF-serialized (it flows in-memory through the
/// outreach pipeline gate + coherence check). A future generic
/// serializer for <see cref="IProvenancedContent{T}"/> would need to
/// handle the wrapper's non-interface members (<see cref="Frame"/>) and
/// the fact that <see cref="IProvenancedContent{T}.Content"/> points at
/// the same record.
/// </para>
/// </summary>
public sealed class OutreachFrameEnvelope : IOutreachFrameEnvelope
{
    public required OutreachFrame Frame { get; init; }

    /// <summary>Canonical "no frame; suppress" envelope wrapping <see cref="OutreachFrame.None"/>.</summary>
    public static OutreachFrameEnvelope None { get; } = new() { Frame = OutreachFrame.None };

    // ── IOutreachFrameEnvelope passthroughs ────────────────────────────
    /// <inheritdoc />
    public OutreachFrameType FrameType => Frame.FrameType;
    /// <inheritdoc />
    public string Anchor => Frame.Anchor;
    /// <inheritdoc />
    public float Confidence => Frame.Confidence;

    // ── IProvenancedContent<OutreachFrame> ─────────────────────────────
    /// <inheritdoc />
    OutreachFrame IProvenancedContent<OutreachFrame>.Content => Frame;

    /// <inheritdoc />
    /// <remarks>
    /// Composed from <see cref="OutreachFrame.FrameType"/> so downstream
    /// audit / telemetry can distinguish <c>frame.shared</c> from
    /// <c>frame.ani-interior</c> etc. without a switch statement at the
    /// consumer.
    /// </remarks>
    string IProvenancedContent<OutreachFrame>.SourceType
        => $"frame.{Frame.FrameType.ToString().ToLowerInvariant()}";

    /// <inheritdoc />
    string IProvenancedContent<OutreachFrame>.Producer => "OutreachFrameSelector";

    /// <inheritdoc />
    /// <remarks>
    /// Captured once at construction per <c>IProvenancedContent&lt;T&gt;.CreatedAt</c>
    /// contract (sibling-impl discipline from PR #112). Class not record
    /// so this doesn't affect equality.
    /// </remarks>
    DateTimeOffset IProvenancedContent<OutreachFrame>.CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    float[]? IProvenancedContent<OutreachFrame>.SemanticKey => null;
}
