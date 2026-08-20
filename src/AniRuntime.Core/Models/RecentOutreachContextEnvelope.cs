using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Canonical <see cref="IRecentOutreachContextEnvelope"/> implementation
/// wrapping the pre-existing <see cref="RecentOutreachContext"/> class
/// with F-1 producer-boundary provenance (Phase 8e, P13 wrap).
///
/// <para>
/// The <see cref="Context"/> property is the construction-shorthand
/// surface (<c>new RecentOutreachContextEnvelope { Context = ... }</c>)
/// and mirrors the pattern used by <c>ClosedConversationEnvelope.Record</c>.
/// The canonical read path for downstream consumers is
/// <see cref="IProvenancedContent{T}.Content"/>; <c>Context</c> and
/// <c>Content</c> point at the same object.
/// </para>
///
/// <para>
/// Class (not record) so implementing <see cref="IRecentOutreachContextEnvelope"/>
/// with an explicit-interface <see cref="IProvenancedContent{T}.CreatedAt"/>
/// auto-property doesn't affect equality (Phase 4 sibling-impl discipline).
/// The wrapped <see cref="RecentOutreachContext"/> is already a mutable
/// class — this envelope's structural role is uniform producer/consumer
/// surface across F-1 wraps, not equality preservation.
/// </para>
///
/// <para>
/// <b>Passthrough is a live read:</b> <see cref="UnansweredCount"/>
/// reflects the wrapped record's current field value, not a snapshot at
/// wrap time. In current code the record is not mutated post-wrap (the
/// producer constructs and hands off in one statement), but same
/// convention as sibling envelopes wrapping mutable records — consumers
/// wanting snapshot semantics defensively copy from the wrapped record.
/// </para>
/// </summary>
public sealed class RecentOutreachContextEnvelope : IRecentOutreachContextEnvelope
{
    /// <summary>
    /// The wrapped context. Construction-only surface: production readers
    /// should access the wrapped payload through the envelope interface
    /// (<see cref="IProvenancedContent{T}.Content"/>). <c>Context</c> is
    /// exposed publicly for readable construction and test-fixture
    /// wrapping; it points at the same object as <c>Content</c>.
    /// </summary>
    public required RecentOutreachContext Context { get; init; }

    // ── IRecentOutreachContextEnvelope passthroughs ──────────────────
    /// <inheritdoc />
    public int UnansweredCount => Context.UnansweredCount;

    // ── IProvenancedContent<RecentOutreachContext> ───────────────────
    /// <inheritdoc />
    RecentOutreachContext IProvenancedContent<RecentOutreachContext>.Content => Context;

    /// <inheritdoc />
    string IProvenancedContent<RecentOutreachContext>.SourceType => "recent-outreach-context.recent-episodic";

    /// <inheritdoc />
    string IProvenancedContent<RecentOutreachContext>.Producer => "StateContextBuilder";

    /// <inheritdoc />
    /// <remarks>
    /// Captured once at construction per <c>IProvenancedContent&lt;T&gt;.CreatedAt</c>
    /// contract (sibling-impl discipline from PR #112). Class not record
    /// so this doesn't affect equality.
    /// </remarks>
    DateTimeOffset IProvenancedContent<RecentOutreachContext>.CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    float[]? IProvenancedContent<RecentOutreachContext>.SemanticKey => null;
}
