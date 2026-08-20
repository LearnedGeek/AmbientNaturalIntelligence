using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Canonical <see cref="IEmotionalContextEnvelope"/> implementation
/// wrapping the pre-existing <see cref="EmotionalContextResult"/> record
/// with F-1 producer-boundary provenance (Phase 8f, P14 wrap).
///
/// <para>
/// Class (not record) so implementing the envelope with an
/// explicit-interface <see cref="IProvenancedContent{T}.CreatedAt"/>
/// auto-property doesn't affect equality — the Phase 4 sibling-impl
/// discipline. The wrapped <see cref="Result"/> record retains its
/// value-equality semantics unchanged (consumers pattern-matching or
/// comparing the wrapped record continue to work).
/// </para>
///
/// <para>
/// The <see cref="Result"/> property is the construction-shorthand
/// surface (<c>new EmotionalContextEnvelope { Result = ... }</c>) and
/// mirrors the pattern used by <c>OutreachFrameEnvelope.Frame</c>. The
/// canonical read path for downstream consumers is
/// <see cref="IProvenancedContent{T}.Content"/>; <c>Result</c> and
/// <c>Content</c> point at the same object.
/// </para>
/// </summary>
public sealed class EmotionalContextEnvelope : IEmotionalContextEnvelope
{
    /// <summary>
    /// The wrapped result. Construction-only surface: production readers
    /// should access the wrapped payload through the envelope interface
    /// (<see cref="IProvenancedContent{T}.Content"/>). <c>Result</c> is
    /// exposed publicly for readable construction and test-fixture
    /// wrapping; it points at the same object as <c>Content</c>.
    /// </summary>
    public required EmotionalContextResult Result { get; init; }

    // ── IProvenancedContent<EmotionalContextResult> ──────────────────
    /// <inheritdoc />
    EmotionalContextResult IProvenancedContent<EmotionalContextResult>.Content => Result;

    /// <inheritdoc />
    /// <remarks>
    /// Single-producer surface: hardcoded SourceType, no source enum.
    /// Kebab-case per sibling-envelope convention.
    /// </remarks>
    string IProvenancedContent<EmotionalContextResult>.SourceType => "emotional-context.per-cycle";

    /// <inheritdoc />
    string IProvenancedContent<EmotionalContextResult>.Producer => "EmotionalContextBuilder";

    /// <inheritdoc />
    /// <remarks>
    /// Captured once at construction per <c>IProvenancedContent&lt;T&gt;.CreatedAt</c>
    /// contract (sibling-impl discipline from PR #112). Class not record
    /// so this doesn't affect equality.
    /// </remarks>
    DateTimeOffset IProvenancedContent<EmotionalContextResult>.CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    float[]? IProvenancedContent<EmotionalContextResult>.SemanticKey => null;
}
