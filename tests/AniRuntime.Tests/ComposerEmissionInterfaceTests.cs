using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Unified Surface (F-3) U1 (2026-08-24) — pins the shape of
/// the new composer-emission interfaces + their SOLID relationship.
///
/// <para>
/// U1 is types-only: no wiring yet, no composer changes, no downstream
/// consumers. The tests here pin the interface CONTRACT so subsequent
/// phases (U3 onwards) build against a stable shape. When a future
/// phase changes the contract, these tests break loud.
/// </para>
///
/// <para>
/// What these tests do NOT cover: behavior. There is no behavior yet.
/// The tests assert structural properties — the interfaces exist, they
/// compose per SOLID, and a minimal fake implementation satisfies the
/// contract. Behavioral tests land in U3-U9 as each composer wires
/// through the envelope.
/// </para>
/// </summary>
public class ComposerEmissionInterfaceTests
{
    // ── Minimal test fake — one concrete implementation of each interface
    // so tests can assert the shape. Not a production type; only lives here.

    private sealed record FakeEmission(
        string                 Content,
        CognitiveProducerKind  ComposerRole,
        DateTimeOffset         EmittedAt,
        AttributedTo           AttributedTo,
        string                 AttributionTrust,
        string?                AttributedSourceDescriptor) : IComposerEmission<string>;

    private sealed record FakeClaimBearingEmission(
        string                       Content,
        CognitiveProducerKind        ComposerRole,
        DateTimeOffset               EmittedAt,
        AttributedTo                 AttributedTo,
        string                       AttributionTrust,
        string?                      AttributedSourceDescriptor,
        IReadOnlyList<ContentClaim>  Claims) : IClaimBearingEmission<string>;

    // ─────────────────────────────────────────────────────────────────────
    // Interface shape
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void IComposerEmission_MinimalImplementation_ExposesAllFields()
    {
        var emittedAt = DateTimeOffset.UtcNow;

        IComposerEmission<string> emission = new FakeEmission(
            Content:                    "U1-FIXTURE: composer content",
            ComposerRole:               CognitiveProducerKind.InnerThought,
            EmittedAt:                  emittedAt,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            AttributedSourceDescriptor: null);

        emission.Content.Should().Be("U1-FIXTURE: composer content");
        emission.ComposerRole.Should().Be(CognitiveProducerKind.InnerThought);
        emission.EmittedAt.Should().Be(emittedAt);
        emission.AttributedTo.Should().Be(AttributedTo.Ani);
        emission.AttributionTrust.Should().Be("verified");
        emission.AttributedSourceDescriptor.Should().BeNull();
    }

    [Fact]
    public void IClaimBearingEmission_MinimalImplementation_ExposesClaimsPlusBaseFields()
    {
        var claim = new ContentClaim(
            Text:             "hey babe",
            AttributedTo:     AttributedTo.Mark,
            SourceRecordId:   Guid.NewGuid(),
            AttributionTrust: "verified");

        IClaimBearingEmission<string> emission = new FakeClaimBearingEmission(
            Content:                    "U1-FIXTURE: composer prose that quotes Mark saying \"hey babe\"",
            ComposerRole:               CognitiveProducerKind.InnerThought,
            EmittedAt:                  DateTimeOffset.UtcNow,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            AttributedSourceDescriptor: null,
            Claims:                     new[] { claim });

        // Base-interface fields present through the extended interface —
        // Interface Segregation lets the extension add capabilities without
        // duplicating base fields on the concrete type.
        emission.Content.Should().Contain("U1-FIXTURE");
        emission.ComposerRole.Should().Be(CognitiveProducerKind.InnerThought);
        emission.AttributedTo.Should().Be(AttributedTo.Ani);
        emission.AttributionTrust.Should().Be("verified");

        // Extended-interface field: the per-claim attribution list.
        emission.Claims.Should().HaveCount(1);
        emission.Claims[0].Text.Should().Be("hey babe");
        emission.Claims[0].AttributedTo.Should().Be(AttributedTo.Mark,
            "the enveloping composer is Ani-authored, but this specific embedded claim quotes Mark — the two attribution levels are independent");
    }

    [Fact]
    public void IClaimBearingEmission_IsAssignableToIComposerEmission()
    {
        // SOLID Liskov + Interface Segregation check: a claim-bearing
        // emission is-a composer emission. Consumers of the base surface
        // work with either implementation.
        var claimBearing = new FakeClaimBearingEmission(
            Content:                    "test",
            ComposerRole:               CognitiveProducerKind.Reflection,
            EmittedAt:                  DateTimeOffset.UtcNow,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            AttributedSourceDescriptor: null,
            Claims:                     Array.Empty<ContentClaim>());

        IComposerEmission<string> asBase = claimBearing;
        asBase.Should().NotBeNull("subtype must be assignable to base surface");
        asBase.Should().BeAssignableTo<IClaimBearingEmission<string>>(
            "runtime type inspection recovers the extended surface when consumers need per-claim attribution");
    }

    [Fact]
    public void IClaimBearingEmission_EmptyClaimsList_IsValid()
    {
        // A composer that emits self-content only (no embedded quoting)
        // declares an empty claims list. Empty is a valid signal, not
        // a defect — distinct from "the composer didn't declare any
        // claims" (which under the interface contract would still be
        // the same empty list, just semantically "nothing to declare").
        var emission = new FakeClaimBearingEmission(
            Content:                    "just a plain observation",
            ComposerRole:               CognitiveProducerKind.InnerThought,
            EmittedAt:                  DateTimeOffset.UtcNow,
            AttributedTo:               AttributedTo.Ani,
            AttributionTrust:           "verified",
            AttributedSourceDescriptor: null,
            Claims:                     Array.Empty<ContentClaim>());

        IClaimBearingEmission<string> asExtended = emission;
        asExtended.Claims.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────
    // ContentClaim record
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContentClaim_UnverifiedClaim_CanCarryNullSourceRecordId()
    {
        // Ungrounded claim — composer paraphrased shared context rather
        // than quoting a specific past turn. SourceRecordId is null and
        // trust is "unverified" — the exact shape a downstream mismatch
        // detector should flag as potentially-fabricated.
        var claim = new ContentClaim(
            Text:             "we agreed we'd talk about this later",
            AttributedTo:     AttributedTo.Mark,
            SourceRecordId:   null,
            AttributionTrust: "unverified");

        claim.SourceRecordId.Should().BeNull();
        claim.AttributionTrust.Should().Be("unverified");
    }

    [Fact]
    public void ContentClaim_RecordEquality_HoldsForSameFieldValues()
    {
        // ContentClaim is a `record` — value equality by field values.
        // Pinned so a future refactor to `class` or field addition doesn't
        // silently break downstream identity comparisons.
        var sourceId = Guid.NewGuid();
        var a = new ContentClaim("hi", AttributedTo.Mark, sourceId, "verified");
        var b = new ContentClaim("hi", AttributedTo.Mark, sourceId, "verified");

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    // ─────────────────────────────────────────────────────────────────────
    // CognitiveProducerKind — F-3 U1 added ReactiveShare
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CognitiveProducerKind_HasReactiveShare_ForF3EnvelopeCoverage()
    {
        // F-3 U1 (2026-08-24) added ReactiveShare so the composer-emission
        // envelope can identify reactive-share output without introducing
        // a parallel ComposerRole enum. Pinned so a rollback doesn't
        // silently drop it before the U7 phase wires it up.
        var reactiveShare = CognitiveProducerKind.ReactiveShare;
        ((int)reactiveShare).Should().Be(9,
            "value pinned so existing serialized enum values remain stable");
    }
}
