using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Canonical <see cref="IClosedConversationEnvelope"/> implementation
/// wrapping the pre-existing <see cref="ClosedConversationRecord"/>
/// class with F-1 producer-boundary provenance (Phase 8c, P3 wrap) and
/// F-3 composer-emission attribution (U8, 2026-08-24).
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
///
/// <para>
/// <b>F-3 U8 timestamp sharing:</b> the F-1 <c>CreatedAt</c> and F-3
/// <c>EmittedAt</c> are backed by a single field captured at construction.
/// The two surfaces describe the same instant (envelope-emit time is
/// composer-emit time for this producer — the summarizer builds the
/// envelope immediately after the LLM returns), so one capture serves
/// both contracts.
/// </para>
/// </summary>
public sealed class ClosedConversationEnvelope : IClosedConversationEnvelope
{
    /// <summary>
    /// F-3 U8 shared timestamp backing both F-1 <c>CreatedAt</c> and F-3
    /// <c>EmittedAt</c>. Captured once at construction — the two surfaces
    /// describe the same instant for this producer so a single field
    /// satisfies both contracts and prevents the surfaces from drifting.
    /// </summary>
    private readonly DateTimeOffset _timestamp = DateTimeOffset.UtcNow;

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

    // ── Content (F-1 IProvenancedContent + F-3 IComposerEmission) ────
    //
    // Single public implementation satisfies three interface members
    // that all describe the same payload:
    //   - IClosedConversationEnvelope.Content (promoted via `new` to
    //     disambiguate the two inherited members)
    //   - IProvenancedContent<ClosedConversationRecord>.Content (F-1)
    //   - IComposerEmission<ClosedConversationRecord>.Content (F-3 U8)
    //
    // Points at the same object as Record — the construction shorthand
    // and the interface read path are two names for the same instance.

    /// <inheritdoc />
    public ClosedConversationRecord Content => Record;

    /// <inheritdoc />
    /// <remarks>
    /// SourceType tags the record's validity state so downstream audit
    /// consumers can filter without unwrapping. Values match the
    /// <see cref="ClosedConversationRecord.Validity"/> open-enum plus
    /// the <c>closed-conversation.</c> producer prefix.
    ///
    /// PR #120 review-fix (Devin): matching is case-insensitive and
    /// treats blank as <c>"valid"</c> — mirrors the downstream retrieval
    /// filter (<c>ConsciousSubstrateGistComposer.cs:411</c> uses
    /// <c>StringComparison.OrdinalIgnoreCase</c>) and the store's blank
    /// normalization (<c>SqliteClosedConversationStore.cs:162</c>). Without
    /// this, a <c>"Valid"</c> or <c>""</c> would tag <c>unknown</c> at the
    /// envelope while still passing the downstream valid-filter, so
    /// audit-dashboard joins on the tag would silently omit the record.
    /// </remarks>
    string IProvenancedContent<ClosedConversationRecord>.SourceType =>
        (string.IsNullOrWhiteSpace(Record.Validity) ? "valid" : Record.Validity) switch
        {
            var v when string.Equals(v, "valid",               StringComparison.OrdinalIgnoreCase) => "closed-conversation.valid",
            var v when string.Equals(v, "invalid_fabrication", StringComparison.OrdinalIgnoreCase) => "closed-conversation.invalid-fabrication",
            var v when string.Equals(v, "invalid_other",       StringComparison.OrdinalIgnoreCase) => "closed-conversation.invalid-other",
            _                                                                                     => "closed-conversation.unknown",
        };

    /// <inheritdoc />
    string IProvenancedContent<ClosedConversationRecord>.Producer => "ClosedConversationSummarizer";

    /// <inheritdoc />
    /// <remarks>
    /// Backed by the shared <c>_timestamp</c> field captured once at
    /// construction per <c>IProvenancedContent&lt;T&gt;.CreatedAt</c>
    /// contract (sibling-impl discipline from PR #112). F-3 U8 shares
    /// this instant with <c>IComposerEmission&lt;T&gt;.EmittedAt</c>.
    /// Class not record so this doesn't affect equality.
    /// </remarks>
    DateTimeOffset IProvenancedContent<ClosedConversationRecord>.CreatedAt => _timestamp;

    /// <inheritdoc />
    /// <remarks>
    /// PR #120 review-fix (Devin): forward the record's already-computed
    /// gist <c>Embedding</c> so the <c>IProvenancedContent&lt;T&gt;.SemanticKey</c>
    /// dedup contract is honored. The summarizer embeds the gist at wrap
    /// time; producing null here would silently opt out of any future
    /// generic near-duplicate detection over envelopes even though the
    /// vector is already available. May be null if the summarizer's
    /// embedding call failed (best-effort; see
    /// <c>ClosedConversationSummarizer</c> catch/log).
    /// </remarks>
    float[]? IProvenancedContent<ClosedConversationRecord>.SemanticKey => Record.Embedding;

    // ── IComposerEmission<ClosedConversationRecord> (F-3 U8) ──────────
    //
    // The Content member is satisfied by the public property above (one
    // impl for all three inherited/promoted Content members). The
    // remaining IComposerEmission members are explicit — they don't
    // collide with F-1 and there's no consumer-side benefit to exposing
    // them publicly, so keep them behind the interface to reserve the
    // public class surface for construction (Record).

    /// <inheritdoc />
    /// <remarks>
    /// The thread-close summarizer is the <c>ClosedThreadSummary</c>
    /// composer per <see cref="CognitiveProducerKind"/>. This identifies
    /// the producer to downstream consumers reading the emission surface
    /// (attribution logging, dashboard producer breakdowns) without
    /// re-parsing the F-1 <c>Producer</c> string tag.
    /// </remarks>
    CognitiveProducerKind IComposerEmission<ClosedConversationRecord>.ComposerRole =>
        CognitiveProducerKind.ClosedThreadSummary;

    /// <inheritdoc />
    DateTimeOffset IComposerEmission<ClosedConversationRecord>.EmittedAt => _timestamp;

    /// <inheritdoc />
    /// <remarks>
    /// The summarizer is an Ani-authored composer — Ani's LLM produces
    /// the paraphrased gist over her own thread-history substrate.
    /// Matches the ten other composer wrap sites migrated in F-3 U3–U7.
    /// </remarks>
    AttributedTo IComposerEmission<ClosedConversationRecord>.AttributedTo => AttributedTo.Ani;

    /// <inheritdoc />
    /// <remarks>
    /// Verified: the composer knows it authored the content (the LLM call
    /// is the emission point). Fallback paths that reconstruct an envelope
    /// from a raw string with defensive defaults would use
    /// <c>"unverified"</c>, but this producer has no such path — the
    /// envelope construction site sits immediately after the successful
    /// LLM call (or, on LLM failure, after the heuristic gist which is
    /// also composer-authored).
    /// </remarks>
    string IComposerEmission<ClosedConversationRecord>.AttributionTrust => "verified";

    /// <inheritdoc />
    /// <remarks>
    /// Null — the emission-side scaffolding descriptor (prompt-template
    /// ID, model name, session identifier) is not tracked at this wrap
    /// site. Downstream consumers reading composer attribution rely on
    /// <see cref="IComposerEmission{T}.ComposerRole"/> and the F-1
    /// <c>Producer</c> tag for identity; the descriptor field is reserved
    /// for future emission-scaffolding grounding without back-filling
    /// every historical envelope.
    /// </remarks>
    string? IComposerEmission<ClosedConversationRecord>.AttributedSourceDescriptor => null;
}
