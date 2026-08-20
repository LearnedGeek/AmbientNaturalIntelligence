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
    /// <summary>
    /// The wrapped record. Construction-only surface: production readers
    /// should access the wrapped payload through the envelope interface
    /// (<see cref="IProvenancedContent{T}.Content"/>) — that's the
    /// canonical read path (documented on <see cref="IOutreachFrameEnvelope"/>).
    /// <c>Frame</c> is exposed publicly for readable construction
    /// (<c>new OutreachFrameEnvelope { Frame = ... }</c>) and test-fixture
    /// wrapping; it points at the same object as <c>Content</c>. Reviewer
    /// note (PR #119 Devin): two idioms for the same read — prod uses
    /// <c>envelope.Content</c>; tests use <c>envelope.Frame</c>.
    /// </summary>
    public required OutreachFrame Frame { get; init; }

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
    /// PR #119 review-fix (Devin BUG): explicit switch to kebab-case
    /// tags matching the sibling envelope convention
    /// (<c>InnerThoughtResult.thought.third-person-frame</c>,
    /// <c>WorldSeedEnvelope.world-seed.associative-anchor</c>). The
    /// pre-fix `.ToString().ToLowerInvariant()` produced
    /// <c>frame.aniinterior</c> because enum names have no separators —
    /// audit dashboards / greps following the documented naming would
    /// miss those entries.
    /// </remarks>
    string IProvenancedContent<OutreachFrame>.SourceType => Frame.FrameType switch
    {
        OutreachFrameType.None            => "frame.none",
        OutreachFrameType.Shared          => "frame.shared",
        OutreachFrameType.AniDomain       => "frame.ani-domain",
        OutreachFrameType.AniInterior     => "frame.ani-interior",
        OutreachFrameType.WorldPerception => "frame.world-perception",
        _                                 => "frame.unknown",
    };

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
