using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Unified Surface (F-3) U2 (2026-08-24) — extension methods
/// that project a composer's emission envelope into the attribution
/// fields a downstream producer needs to persist as a
/// <see cref="MemoryRecord"/>. The projection is deliberately narrow:
/// each wrap site retains full control over composer-specific fields
/// (<c>Type</c>, <c>Provenance</c>, <c>SourceName</c>, <c>Importance</c>,
/// etc.) and only pulls the attribution fields from the envelope.
///
/// <para>
/// <b>Why not project the whole MemoryRecord.</b> An earlier draft of
/// this helper wrote the entire <see cref="MemoryRecord"/> shape from
/// the envelope. That version required the envelope to carry
/// composer-specific fields (which epistemic tier, which source name,
/// etc.) or the helper had to know a per-composer table. Both push
/// composer-specific concerns onto a general-purpose type, violating
/// Single Responsibility. The narrower <see cref="ToAttributionTriple"/>
/// projection lets each wrap site say <c>var triple = emission.ToAttributionTriple();</c>
/// in place of the current <c>var triple = AttributionTriple.AniAt(now);</c>
/// — one-line swap, no other change to the wrap site.
/// </para>
///
/// <para>
/// <b>Migration path this enables.</b> Composers migrated in U3+ change
/// their signature from returning <c>Task&lt;string&gt;</c> to returning
/// <c>Task&lt;IComposerEmission&lt;string&gt;&gt;</c>. Each wrap site
/// then swaps one line — the <c>AttributionTriple.AniAt(now)</c> call
/// becomes <c>emission.ToAttributionTriple()</c>. Everything else about
/// the wrap stays identical. Ten near-duplicate reconstruction sites
/// collapse to one shared extension method + one composer-side line
/// that constructs the envelope at emission.
/// </para>
/// </summary>
public static class ComposerEmissionExtensions
{
    /// <summary>
    /// Project an emission's record-author attribution fields into an
    /// <see cref="AttributionTriple"/> ready to populate on a
    /// <see cref="MemoryRecord"/>. The source-record-id AND the source
    /// descriptor are both left null; composers that need to attach
    /// content-source grounding (unusual — composers produce Ani-authored
    /// content, they do not wrap inbound Mark-authored content) can set
    /// them on the returned triple with a <c>with</c> expression.
    ///
    /// <para>
    /// <b>Devin PR #137 review-fix (2026-08-24) — do NOT project the
    /// emission's <see cref="IComposerEmission{T}.AttributedSourceDescriptor"/>
    /// into the triple's <c>SourceDescriptor</c>.</b> The two fields share
    /// a name but describe different concepts:
    /// <list type="bullet">
    ///   <item>The emission's field is emission-side scaffolding
    ///         (prompt-template ID, model name, session identifier) —
    ///         "how this got composed."</item>
    ///   <item>The triple's field is F-2 content-source grounding
    ///         (e.g. <c>twilio-inbound:SM&lt;sid&gt;</c>,
    ///         <c>character-seed:mark.profile</c>) — "where the utterance
    ///         actually came from" — and flows into the persisted
    ///         <see cref="IAttributedContent{T}.AttributedSourceDescriptor"/>
    ///         as content-attribution grounding.</item>
    /// </list>
    /// Copying scaffolding into grounding would let a composer that
    /// records its model name silently persist that model name as the
    /// record's content-source. Fixed by projecting <c>null</c> for the
    /// triple's descriptor. If a future composer needs to carry a real
    /// content-source link, add a dedicated field to the emission surface
    /// instead of overloading the scaffolding field.
    /// </para>
    ///
    /// <para>
    /// This is the replacement for the current
    /// <c>AttributionTriple.AniAt(now)</c> pattern at the ten producer
    /// wrap sites. The composer knows its identity + attribution when
    /// it runs, so the wrap site pulls the triple from the envelope
    /// rather than reconstructing it from context.
    /// </para>
    /// </summary>
    public static AttributionTriple ToAttributionTriple<T>(this IComposerEmission<T> emission)
    {
        if (emission is null) throw new ArgumentNullException(nameof(emission));

        return new AttributionTriple(
            AttributedTo:     emission.AttributedTo,
            AttributedAt:     emission.EmittedAt,
            SourceRecordId:   null,
            SourceDescriptor: null,
            Trust:            emission.AttributionTrust);
    }

    /// <summary>
    /// Convenience constructor for the common case: build an
    /// <see cref="IComposerEmission{T}"/> for an Ani-authored composer
    /// emitting at the given time with verified trust. Removes the
    /// most-repeated construction pattern from composer sites during
    /// migration.
    ///
    /// <para>
    /// Callers that need a different attribution (unverified fallback
    /// after a parse failure, canonical composer output, etc.) build
    /// <see cref="ComposerEmission{T}"/> directly instead of using this
    /// helper.
    /// </para>
    /// </summary>
    public static ComposerEmission<T> AniEmission<T>(
        T                     content,
        CognitiveProducerKind composerRole,
        DateTimeOffset        emittedAt)
        => new(
            Content:          content,
            ComposerRole:     composerRole,
            EmittedAt:        emittedAt,
            AttributedTo:     AttributedTo.Ani,
            AttributionTrust: "verified");
}
