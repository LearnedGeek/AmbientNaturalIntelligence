using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Canonical <see cref="IClosedConversationEnvelope"/> implementation
/// wrapping the pre-existing <see cref="ClosedConversationRecord"/>
/// class with F-1 producer-boundary provenance (Phase 8c, P3 wrap).
///
/// <para>
/// The <see cref="Record"/> property is the construction-shorthand
/// surface (<c>new ClosedConversationEnvelope { Record = ... }</c>) and
/// mirrors the pattern used by <c>OutreachFrameEnvelope.Frame</c>. The
/// canonical read path for downstream consumers is
/// <see cref="IProvenancedContent{T}.Content"/>; <c>Record</c> and
/// <c>Content</c> point at the same object.
/// </para>
///
/// <para>
/// <b>SourceType composition:</b> derived from the wrapped record's
/// <see cref="ClosedConversationRecord.Validity"/> so downstream audit
/// dashboards can distinguish quarantined-fabrication records from
/// valid ones at the boundary tag without unwrapping. Kebab-case per
/// the sibling-envelope naming convention (Phase 8a/8b).
/// </para>
/// </summary>
public sealed class ClosedConversationEnvelope : IClosedConversationEnvelope
{
    /// <summary>
    /// The wrapped record. Construction-only surface: production readers
    /// should access the wrapped payload through the envelope interface
    /// (<see cref="IProvenancedContent{T}.Content"/>). <c>Record</c> is
    /// exposed publicly for readable construction and test-fixture
    /// wrapping; it points at the same object as <c>Content</c>.
    /// </summary>
    public required ClosedConversationRecord Record { get; init; }

    // ── IClosedConversationEnvelope passthroughs ──────────────────────
    /// <inheritdoc />
    public Guid ThreadId => Record.ThreadId;
    /// <inheritdoc />
    public string Gist => Record.Gist;
    /// <inheritdoc />
    public string Validity => Record.Validity;

    // ── IProvenancedContent<ClosedConversationRecord> ─────────────────
    /// <inheritdoc />
    ClosedConversationRecord IProvenancedContent<ClosedConversationRecord>.Content => Record;

    /// <inheritdoc />
    /// <remarks>
    /// SourceType tags the record's validity state so downstream audit
    /// consumers can filter without unwrapping. Values match the
    /// <see cref="ClosedConversationRecord.Validity"/> open-enum plus
    /// the <c>closed-conversation.</c> producer prefix.
    /// </remarks>
    string IProvenancedContent<ClosedConversationRecord>.SourceType => Record.Validity switch
    {
        "valid"                => "closed-conversation.valid",
        "invalid_fabrication"  => "closed-conversation.invalid-fabrication",
        "invalid_other"        => "closed-conversation.invalid-other",
        _                      => "closed-conversation.unknown",
    };

    /// <inheritdoc />
    string IProvenancedContent<ClosedConversationRecord>.Producer => "ClosedConversationSummarizer";

    /// <inheritdoc />
    /// <remarks>
    /// Captured once at construction per <c>IProvenancedContent&lt;T&gt;.CreatedAt</c>
    /// contract (sibling-impl discipline from PR #112). Class not record
    /// so this doesn't affect equality.
    /// </remarks>
    DateTimeOffset IProvenancedContent<ClosedConversationRecord>.CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    /// <remarks>
    /// The wrapped record carries its own <c>Embedding</c> field for
    /// cosine-similarity retrieval, but that lives on the record for
    /// downstream store queries — not exposed here to avoid duplicating
    /// the surface. Null at the envelope layer.
    /// </remarks>
    float[]? IProvenancedContent<ClosedConversationRecord>.SemanticKey => null;
}
