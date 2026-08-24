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
    /// <see cref="MemoryRecord"/>. The source-record-id is left null;
    /// composers that need to link to a source record can set it on the
    /// returned triple with a <c>with</c> expression.
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
            SourceDescriptor: emission.AttributedSourceDescriptor,
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
