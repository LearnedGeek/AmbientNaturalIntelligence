using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P1 (2026-08-21) — value type
/// bundling the four <see cref="IAttributedContent{T}"/> attribution
/// fields for convenient producer-side construction.
///
/// <para>
/// Producers infer attribution at record emit time and hand off the
/// triple (technically a quintuple, but "triple" as shorthand for the
/// conceptual grouping of who + when + source + trust) to the persistence
/// layer without threading five parameters. Trust is included in the
/// same value type because it's inseparable from the attribution claim —
/// asserting attribution without asserting trust would let unverified
/// heuristic guesses silently become "verified" in downstream reads.
/// </para>
///
/// <para>
/// Common construction patterns:
/// <code>
/// // Ani-authored interior thought at a specific time, no source link
/// AttributionTriple.AniAt(DateTimeOffset.UtcNow)
///
/// // Mark's canonical character-seed content, no specific time
/// AttributionTriple.MarkCanonical("character-seed:mark.profile")
///
/// // Mark's inbound SMS
/// AttributionTriple.MarkAt(sentAt, "twilio-inbound:" + sid)
///
/// // Unknown attribution (backfill couldn't infer)
/// AttributionTriple.UnknownHistorical()
/// </code>
/// </para>
/// </summary>
public readonly record struct AttributionTriple(
    AttributedTo    AttributedTo,
    DateTimeOffset? AttributedAt,
    Guid?           SourceRecordId,
    string?         SourceDescriptor,
    string          Trust)
{
    /// <summary>
    /// Ani authored the content at the given time. No source link
    /// (interior thoughts, outbound compositions — Ani IS the source).
    /// </summary>
    public static AttributionTriple AniAt(DateTimeOffset at) =>
        new(Interfaces.AttributedTo.Ani, at, null, null, "verified");

    /// <summary>
    /// Ani authored the content, sourced from another persisted record
    /// (e.g., a reflection synthesized from earlier memories).
    /// </summary>
    public static AttributionTriple AniFromRecord(DateTimeOffset at, Guid sourceRecordId) =>
        new(Interfaces.AttributedTo.Ani, at, sourceRecordId, null, "verified");

    /// <summary>
    /// Mark uttered the content at the given time, sourced from a
    /// specific event (Twilio inbound, chat-history turn).
    /// </summary>
    public static AttributionTriple MarkAt(DateTimeOffset at, string sourceDescriptor) =>
        new(Interfaces.AttributedTo.Mark, at, null, sourceDescriptor, "verified");

    /// <summary>
    /// Mark asserted the content as canonical (character-seed). No
    /// specific utterance time — treated as timeless truth.
    /// </summary>
    public static AttributionTriple MarkCanonical(string sourceDescriptor) =>
        new(Interfaces.AttributedTo.Mark, null, null, sourceDescriptor, "verified");

    /// <summary>
    /// Non-utterance world event (RSS, weather, time-of-day, contact
    /// state). No specific utterer — the world "produced" it.
    /// </summary>
    public static AttributionTriple WorldAt(DateTimeOffset at, string sourceDescriptor) =>
        new(Interfaces.AttributedTo.World, at, null, sourceDescriptor, "verified");

    /// <summary>
    /// Historical record that couldn't be attributed by backfill heuristic.
    /// Author explicitly Unknown, trust flagged as unverified-historical
    /// so retrieval-render surfaces the untrustworthiness.
    /// </summary>
    public static AttributionTriple UnknownHistorical() =>
        new(Interfaces.AttributedTo.Unknown, null, null, null, "unverified-historical");

    /// <summary>
    /// Ani authored the record, but internal content claims (e.g.,
    /// embedded "you said X" prose in a pre-F-2 Interior record) cannot
    /// be retroactively verified. Used by the Phase 3 backfill for Interior
    /// records — record author is trivially Ani, but the content may
    /// contain misattribution claims from pre-attribution-fix cycles.
    /// </summary>
    public static AttributionTriple AniUnverifiedHistorical() =>
        new(Interfaces.AttributedTo.Ani, null, null, null, "unverified-historical");
}
