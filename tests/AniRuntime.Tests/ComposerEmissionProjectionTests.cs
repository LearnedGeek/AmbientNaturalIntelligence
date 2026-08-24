using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Unified Surface (F-3) U2 (2026-08-24) — pins the concrete
/// envelope records + the projection extension that turns an emission
/// envelope into an <see cref="AttributionTriple"/> ready for the
/// producer wrap site.
///
/// <para>
/// U2 is still additive-only — no composer or wrap site is using these
/// helpers yet. The tests here pin the CONTRACT so U3 onwards can build
/// against a stable projection surface. When a future migration changes
/// the projection semantics, these tests break loud.
/// </para>
/// </summary>
public class ComposerEmissionProjectionTests
{
    // ─────────────────────────────────────────────────────────────────────
    // ComposerEmission<T> concrete record
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComposerEmission_ConstructsWithAllFields_ImplementsInterface()
    {
        var emittedAt = new DateTimeOffset(2026, 8, 24, 10, 24, 0, TimeSpan.Zero);

        var emission = new ComposerEmission<string>(
            Content:                    "U2-FIXTURE: emission content",
            ComposerRole:               CognitiveProducerKind.InnerThought,
            EmittedAt:                  emittedAt,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified");

        IComposerEmission<string> asInterface = emission;

        asInterface.Content.Should().Be("U2-FIXTURE: emission content");
        asInterface.ComposerRole.Should().Be(CognitiveProducerKind.InnerThought);
        asInterface.EmittedAt.Should().Be(emittedAt);
        asInterface.AttributedTo.Should().Be(AttributedTo.Ani);
        asInterface.AttributionTrust.Should().Be("verified");
        asInterface.AttributedSourceDescriptor.Should().BeNull(
            "descriptor defaults to null for the common case; composers set it explicitly if needed");
    }

    [Fact]
    public void ComposerEmission_WithExplicitDescriptor_PropagatesThroughInterface()
    {
        var emission = new ComposerEmission<string>(
            Content:                    "U2-FIXTURE",
            ComposerRole:               CognitiveProducerKind.Reflection,
            EmittedAt:                  DateTimeOffset.UtcNow,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            AttributedSourceDescriptor: "reflection:cycle-42");

        emission.AttributedSourceDescriptor.Should().Be("reflection:cycle-42");
    }

    // ─────────────────────────────────────────────────────────────────────
    // ClaimBearingEmission<T> concrete record
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClaimBearingEmission_ConstructsWithClaimsList_ImplementsBothInterfaces()
    {
        var claim = new ContentClaim(
            Text:             "hey babe",
            AttributedTo:     AttributedTo.Mark,
            SourceRecordId:   Guid.NewGuid(),
            AttributionTrust: "verified");

        var emission = new ClaimBearingEmission<string>(
            Content:                    "I keep replaying how Mark said \"hey babe\"",
            ComposerRole:               CognitiveProducerKind.InnerThought,
            EmittedAt:                  DateTimeOffset.UtcNow,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            Claims:                     new[] { claim });

        IComposerEmission<string> asBase = emission;
        IClaimBearingEmission<string> asExtended = emission;

        asBase.AttributedTo.Should().Be(AttributedTo.Ani,
            "record author = Ani; the composer is Ani's inner-thought");
        asExtended.Claims.Should().HaveCount(1);
        asExtended.Claims[0].AttributedTo.Should().Be(AttributedTo.Mark,
            "embedded claim = Mark; the two attribution levels are independent");
    }

    [Fact]
    public void ClaimBearingEmission_EmptyClaimsList_IsValid()
    {
        var emission = new ClaimBearingEmission<string>(
            Content:                    "just a plain observation",
            ComposerRole:               CognitiveProducerKind.InnerThought,
            EmittedAt:                  DateTimeOffset.UtcNow,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            Claims:                     Array.Empty<ContentClaim>());

        emission.Claims.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────
    // ToAttributionTriple projection
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToAttributionTriple_ProjectsRecordAuthorFieldsIntoTriple()
    {
        var emittedAt = new DateTimeOffset(2026, 8, 24, 10, 24, 0, TimeSpan.Zero);
        var emission = new ComposerEmission<string>(
            Content:                    "U2-FIXTURE",
            ComposerRole:               CognitiveProducerKind.InnerThought,
            EmittedAt:                  emittedAt,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified");

        var triple = emission.ToAttributionTriple();

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.AttributedAt.Should().Be(emittedAt,
            "the emission's EmittedAt becomes the record's AttributedAt in the triple");
        triple.SourceRecordId.Should().BeNull(
            "SourceRecordId is left null for composer-side callers to set explicitly if a source-record link exists");
        triple.SourceDescriptor.Should().BeNull(
            "no descriptor was set on the emission, so the triple's descriptor is null");
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void ToAttributionTriple_PropagatesDescriptorWhenPresent()
    {
        var emission = new ComposerEmission<string>(
            Content:                    "U2-FIXTURE",
            ComposerRole:               CognitiveProducerKind.Reflection,
            EmittedAt:                  DateTimeOffset.UtcNow,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            AttributedSourceDescriptor: "reflection:cycle-42");

        var triple = emission.ToAttributionTriple();

        triple.SourceDescriptor.Should().Be("reflection:cycle-42");
    }

    [Fact]
    public void ToAttributionTriple_WorksForClaimBearingEmission()
    {
        // The projection is on IComposerEmission<T> (the base surface),
        // so any subtype implementing that surface can call it — including
        // the extended IClaimBearingEmission<T>. Claims are not part of
        // the record-author attribution triple; per-claim attribution is
        // persisted separately in U4+ phases.
        var emission = new ClaimBearingEmission<string>(
            Content:                    "test",
            ComposerRole:               CognitiveProducerKind.InnerThought,
            EmittedAt:                  DateTimeOffset.UtcNow,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            Claims:                     Array.Empty<ContentClaim>());

        var triple = emission.ToAttributionTriple();

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
    }

    [Fact]
    public void ToAttributionTriple_NullEmission_Throws()
    {
        // Defensive per the interface contract — extension can be called
        // on any reference including null. Throw ArgumentNullException so
        // wrap sites fail loud rather than silently producing a bogus
        // triple.
        IComposerEmission<string>? nullEmission = null;

        var act = () => nullEmission!.ToAttributionTriple();

        act.Should().Throw<ArgumentNullException>();
    }

    // ─────────────────────────────────────────────────────────────────────
    // AniEmission convenience constructor
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AniEmission_ProducesAniAuthoredVerifiedEnvelope()
    {
        var emittedAt = DateTimeOffset.UtcNow;

        var emission = ComposerEmissionExtensions.AniEmission(
            content:      "U2-FIXTURE: convenience-constructed emission",
            composerRole: CognitiveProducerKind.Outreach,
            emittedAt:    emittedAt);

        emission.Content.Should().Be("U2-FIXTURE: convenience-constructed emission");
        emission.ComposerRole.Should().Be(CognitiveProducerKind.Outreach);
        emission.EmittedAt.Should().Be(emittedAt);
        emission.AttributedTo.Should().Be(AttributedTo.Ani);
        emission.AttributionTrust.Should().Be("verified");
        emission.AttributedSourceDescriptor.Should().BeNull();
    }

    [Fact]
    public void AniEmission_RoundTripsToAttributionTriple_EquivalentToAttributionTripleAniAt()
    {
        // U2 collapses the current AttributionTriple.AniAt(now) pattern
        // at ten wrap sites to `emission.ToAttributionTriple()`. Pin that
        // the two paths produce equivalent triples for the common case
        // (Ani-authored composer, no descriptor, no source-record link).
        var emittedAt = new DateTimeOffset(2026, 8, 24, 10, 24, 0, TimeSpan.Zero);

        var emission = ComposerEmissionExtensions.AniEmission(
            content:      "test",
            composerRole: CognitiveProducerKind.InnerThought,
            emittedAt:    emittedAt);

        var viaEmission = emission.ToAttributionTriple();
        var viaFactory  = AttributionTriple.AniAt(emittedAt);

        viaEmission.Should().Be(viaFactory,
            "the U2 projection path must produce a triple structurally equivalent to the pre-U2 AttributionTriple.AniAt factory for the common case — otherwise the U3+ migration would silently change behavior at wrap sites");
    }
}
