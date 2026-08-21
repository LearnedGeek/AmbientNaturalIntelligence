namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P1 (2026-08-21) — sibling interface
/// to <see cref="IProvenancedContent{T}"/> that carries attribution
/// (who authored the content within a record). Opt-in per record type
/// that has utterance semantics.
///
/// <para>
/// <b>Sibling to <see cref="IProvenancedContent{T}"/>, NOT an extension.</b>
/// Per SOLID Interface Segregation (design plan D1/§SOLID): records that
/// don't have utterance semantics (WorldSeedEnvelope, EmotionalContextEnvelope,
/// aggregate metrics, routing decisions) do NOT implement this interface.
/// Extending <see cref="IProvenancedContent{T}"/> instead would violate
/// Open/Closed against the 12 existing implementers and Interface
/// Segregation against records where attribution isn't meaningful.
/// </para>
///
/// <para>
/// <b>What "attribution" means here:</b> the identity of the utterer of
/// the content the record CONTAINS — not the identity of the producer
/// that emitted the record. E.g., an Interior <c>MemoryRecord</c> is
/// AUTHORED by Ani (Ani's inner life), so <see cref="AttributedTo"/> = Ani.
/// A Facts record with SourceName="twilio-inbound" is AUTHORED by Mark
/// (his SMS text), so <see cref="AttributedTo"/> = Mark.
/// </para>
///
/// <para>
/// <b>Empirical trigger (2026-08-20 substrate-feedback loop):</b> Ani's
/// 11:29 SMS reply "mmm… baby, you're back…" re-appeared verbatim as a
/// 14:03 outreach via a 12:04 misattribution inner thought that claimed
/// "I keep replaying how <b>you said</b> 'mmm baby...'" — attributing
/// her own words to Mark. Nothing in the schema recorded who actually
/// uttered the quoted content, so subsequent cycles treated it as
/// canonical Mark utterance. Attribution as a first-class field on the
/// record schema is what closes that gap. See
/// <c>ani-docs/research/ANI-Attribution-Audit-Input-Side-2026-08-21.md</c>
/// for the full loop trace.
/// </para>
///
/// <para>
/// <b>The three attribution values are a triple, not independent fields:</b>
/// <see cref="AttributedTo"/> + <see cref="AttributedAt"/> +
/// <see cref="AttributedSourceRecordId"/>/<see cref="AttributedSourceDescriptor"/>.
/// Without the source link, "Mark said X" content can still fabricate
/// occurrences at unspecified times. The <see cref="AttributionTriple"/>
/// value type wraps these for convenient producer-side construction.
/// </para>
/// </summary>
/// <typeparam name="T">The wrapped payload type.</typeparam>
public interface IAttributedContent<out T>
{
    /// <summary>The wrapped payload (typically same reference as
    /// <see cref="IProvenancedContent{T}.Content"/> when both interfaces
    /// are implemented on the same record).</summary>
    T Content { get; }

    /// <summary>Who authored the content within the record.</summary>
    AttributedTo AttributedTo { get; }

    /// <summary>
    /// When the utterance happened (UTC). Null when attribution is
    /// canonical/timeless (character-seed content — Mark asserted it once
    /// as canonical, no specific occurrence time).
    /// </summary>
    DateTimeOffset? AttributedAt { get; }

    /// <summary>
    /// FK-like reference to the source record when the source is another
    /// persisted <c>MemoryRecord</c>. Null when the source is ephemeral
    /// (chat-history turn) or canonical (character-seed) — in which case
    /// <see cref="AttributedSourceDescriptor"/> carries the stable
    /// descriptor.
    ///
    /// <para>
    /// Convention: <see cref="AttributedSourceRecordId"/> and
    /// <see cref="AttributedSourceDescriptor"/> should not both be
    /// non-null; producers pick one shape per record.
    /// </para>
    /// </summary>
    Guid? AttributedSourceRecordId { get; }

    /// <summary>
    /// Free-text descriptor when the source is ephemeral or non-record
    /// (e.g., <c>"chat-history-turn:2026-08-20T16:29"</c>,
    /// <c>"twilio-inbound:SM<sid>"</c>,
    /// <c>"character-seed:mark.profile"</c>).
    /// Null when <see cref="AttributedSourceRecordId"/> carries the
    /// reference instead.
    /// </summary>
    string? AttributedSourceDescriptor { get; }

    /// <summary>
    /// Verification state of the attribution. Values:
    /// <list type="bullet">
    ///   <item><c>"verified"</c> — attribution is trusted (source is
    ///         canonical or FK/descriptor resolves to a known utterance).</item>
    ///   <item><c>"unverified"</c> — attribution is inferred but the source
    ///         cannot be checked (typically new records without source-linking
    ///         infrastructure yet).</item>
    ///   <item><c>"unverified-historical"</c> — pre-F-2 record backfilled
    ///         with heuristic attribution; internal content claims (e.g.,
    ///         embedded "you said X" prose) cannot be retroactively verified.
    ///         Retrieval-time rendering flags these explicitly so composer
    ///         LLMs can weight accordingly.</item>
    /// </list>
    /// String rather than enum for forward-compat with future trust categories
    /// without schema migration.
    /// </summary>
    string AttributionTrust { get; }
}
