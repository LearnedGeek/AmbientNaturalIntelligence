using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Unified Surface (F-3) U1 (2026-08-24) — envelope shape
/// returned by every composer's LLM call. Carries the composer's output
/// content alongside the metadata (identity, emission time, attribution
/// triple) that the composer knows about itself, so downstream producers
/// don't have to reconstruct it via <c>AttributionTriple.AniAt(now)</c>
/// at each of the current ten wrap sites.
///
/// <para>
/// <b>Why this exists:</b> F-1 established producer-boundary provenance
/// (<see cref="IProvenancedContent{T}"/>). F-2 established record
/// attribution (<see cref="IAttributedContent{T}"/>) — who authored the
/// content within a record. But composer OUTPUT still crossed the surface
/// as a raw string. Every producer wrap step then reconstructed attribution
/// from context using <c>AttributionTriple.AniAt(now)</c>. Trivially
/// correct for Ani-owned composers (all ten current sites) but ten
/// near-duplicate reconstruction sites. Any rule change had to be applied
/// ten times. This envelope collapses those ten sites to mechanical
/// projection: the composer knows its identity + attribution when it
/// runs, so it emits them alongside the content.
/// </para>
///
/// <para>
/// <b>Companion — content-claim attribution.</b> This interface carries
/// record-author attribution (who WROTE the content). It does NOT carry
/// per-claim attribution for embedded content ("you said X" claims inside
/// prose). That's <see cref="IClaimBearingEmission{T}"/>, which extends
/// this interface for composers that emit structured output including
/// per-claim attribution — inner-thought and reflection specifically. See
/// <c>ani-docs/spec/ANI-Unified-Attribution-Surface-Plan.md</c> for the
/// full split rationale.
/// </para>
///
/// <para>
/// <b>SOLID — composition, not extension.</b> <see cref="IProvenancedContent{T}"/>
/// (F-1) tells us where a record came from. <see cref="IAttributedContent{T}"/>
/// (F-2) tells us who authored its content. A composer emission is both
/// — it comes from a specific composer AND it has an author. This
/// interface carries both concerns at the emission surface without
/// forcing implementers of the F-1/F-2 record surfaces to also implement
/// emission-specific fields (Interface Segregation).
/// </para>
/// </summary>
/// <typeparam name="T">The wrapped payload type — typically <c>string</c>
/// for prose composers, or a structured type (e.g., a gist record) for
/// composers with typed output.</typeparam>
public interface IComposerEmission<out T>
{
    /// <summary>The composer's output payload.</summary>
    T Content { get; }

    /// <summary>
    /// Which composer emitted this envelope. Reuses the existing
    /// <see cref="CognitiveProducerKind"/> enum — one source of truth for
    /// composer identity across F-1/F-2/F-3 surfaces.
    /// </summary>
    CognitiveProducerKind ComposerRole { get; }

    /// <summary>Wall-clock emission time (UTC).</summary>
    DateTimeOffset EmittedAt { get; }

    /// <summary>
    /// Who authored the content. For every composer in the current runtime
    /// this is <see cref="AttributedTo.Ani"/> — Ani produces the content
    /// via her composer models. The field is here for structural
    /// completeness so a future composer that isn't Ani-owned (e.g., an
    /// external-tool response wrapped as a composer) can be enveloped
    /// consistently.
    /// </summary>
    AttributedTo AttributedTo { get; }

    /// <summary>
    /// Trust marker for the record-author attribution. Typically
    /// <c>"verified"</c> for live composer emissions (the composer knows
    /// it authored the content). May be <c>"unverified"</c> in fallback
    /// paths (e.g., when Option B structured emission parse fails and the
    /// envelope is reconstructed from the raw string with defensive
    /// defaults).
    /// </summary>
    string AttributionTrust { get; }

    /// <summary>
    /// Optional descriptor identifying the composition-side source (e.g.,
    /// prompt-template ID, model name, session identifier). Null for
    /// composers that don't carry one.
    ///
    /// <para>
    /// <b>DO NOT confuse with the F-2 content-source descriptor</b>
    /// (<see cref="IAttributedContent{T}.AttributedSourceDescriptor"/>).
    /// This field is <b>emission-side scaffolding</b> — "how the content
    /// got composed" (model name, template ID, session). The F-2 field is
    /// <b>content-source grounding</b> — "where the utterance actually
    /// came from" (e.g. <c>twilio-inbound:SM&lt;sid&gt;</c>,
    /// <c>character-seed:mark.profile</c>). These share a name but are
    /// different concepts and must NOT be projected into each other.
    /// <see cref="ComposerEmissionExtensions.ToAttributionTriple"/>
    /// enforces this by leaving the triple's <c>SourceDescriptor</c>
    /// null — pinned by Devin PR #137 review-fix (2026-08-24) after an
    /// earlier draft of the projection helper conflated the two.
    /// </para>
    /// </summary>
    string? AttributedSourceDescriptor { get; }
}
