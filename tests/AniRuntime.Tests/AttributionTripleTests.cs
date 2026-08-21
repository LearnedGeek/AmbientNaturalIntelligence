using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P1 (2026-08-21) — verifies the
/// <see cref="AttributionTriple"/> value type and its factory methods
/// produce the correct field layouts for each canonical attribution
/// shape (Ani-authored / Mark-uttered / Mark-canonical / world event /
/// historical-unknown / historical-ani-unverified).
///
/// <para>
/// Each factory maps to a specific ingest-time inference path per the
/// F-2 Phase 1 design plan D4 backfill heuristic table. Tests pin the
/// expected field layout so downstream producer wiring (P6) and the
/// backfill script (P3) inherit a stable contract.
/// </para>
/// </summary>
public class AttributionTripleTests
{
    [Fact]
    public void AniAt_SetsAuthorToAni_TimeSet_NoSource_VerifiedTrust()
    {
        var at = new DateTimeOffset(2026, 8, 20, 16, 29, 0, TimeSpan.Zero);

        var triple = AttributionTriple.AniAt(at);

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.AttributedAt.Should().Be(at);
        triple.SourceRecordId.Should().BeNull();
        triple.SourceDescriptor.Should().BeNull();
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void AniFromRecord_SetsAuthorToAni_LinksSourceRecord_VerifiedTrust()
    {
        var at = DateTimeOffset.UtcNow;
        var sourceId = Guid.NewGuid();

        var triple = AttributionTriple.AniFromRecord(at, sourceId);

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.AttributedAt.Should().Be(at);
        triple.SourceRecordId.Should().Be(sourceId);
        triple.SourceDescriptor.Should().BeNull("FK path uses record id, not descriptor");
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void MarkAt_SetsAuthorToMark_UsesDescriptor_VerifiedTrust()
    {
        var at = DateTimeOffset.UtcNow;

        var triple = AttributionTriple.MarkAt(at, "twilio-inbound:SM635700f44cb68f63396074ac721a9f20");

        triple.AttributedTo.Should().Be(AttributedTo.Mark);
        triple.AttributedAt.Should().Be(at);
        triple.SourceRecordId.Should().BeNull("Twilio inbound isn't a persisted MemoryRecord source");
        triple.SourceDescriptor.Should().Be("twilio-inbound:SM635700f44cb68f63396074ac721a9f20");
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void MarkCanonical_SetsAuthorToMark_NullTime_VerifiedTrust()
    {
        // Character-seed content: Mark asserted it as canonical, no specific
        // utterance time. Design plan D4 backfill row: Facts + character-seed
        // → AttributedTo=Mark, AttributedAt=null.
        var triple = AttributionTriple.MarkCanonical("character-seed:mark.profile");

        triple.AttributedTo.Should().Be(AttributedTo.Mark);
        triple.AttributedAt.Should().BeNull("canonical content is timeless — no specific utterance moment");
        triple.SourceDescriptor.Should().Be("character-seed:mark.profile");
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void WorldAt_SetsAuthorToWorld_UsesDescriptor()
    {
        var at = DateTimeOffset.UtcNow;

        var triple = AttributionTriple.WorldAt(at, "rss:npr-news:2026-08-20T11:14");

        triple.AttributedTo.Should().Be(AttributedTo.World);
        triple.AttributedAt.Should().Be(at);
        triple.SourceDescriptor.Should().Be("rss:npr-news:2026-08-20T11:14");
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void UnknownHistorical_MarksAsExplicitlyUnknown_UnverifiedHistorical()
    {
        // Backfill couldn't infer attribution from tier + type + source combo.
        // Record lands on the manual-curation tail. Must NOT silently default
        // to Mark or Ani.
        var triple = AttributionTriple.UnknownHistorical();

        triple.AttributedTo.Should().Be(AttributedTo.Unknown, "backfill fallback must not silently default to a real actor");
        triple.AttributedAt.Should().BeNull();
        triple.SourceRecordId.Should().BeNull();
        triple.SourceDescriptor.Should().BeNull();
        triple.Trust.Should().Be("unverified-historical",
            "distinguishes backfill-couldn't-infer from new-record-unverified");
    }

    [Fact]
    public void AniUnverifiedHistorical_AttributesAuthorToAni_MarksInternalClaimsUnverified()
    {
        // Pre-F-2 Interior record: record's OWN author is trivially Ani (Interior
        // tier == Ani-authored), but the CONTENT may contain misattribution
        // claims from before the attribution-fix work. Backfill sets Ani as
        // author with unverified-historical trust so retrieval-render surfaces
        // the internal-claim untrustworthiness. This is the 12:04 misattribution
        // class from the 2026-08-20 substrate-feedback finding.
        var triple = AttributionTriple.AniUnverifiedHistorical();

        triple.AttributedTo.Should().Be(AttributedTo.Ani,
            "Interior tier trivially implies Ani authored the record");
        triple.AttributedAt.Should().BeNull("historical records don't preserve emit time cleanly");
        triple.SourceRecordId.Should().BeNull();
        triple.SourceDescriptor.Should().BeNull();
        triple.Trust.Should().Be("unverified-historical",
            "internal 'you said X' claims in the content cannot be retroactively verified");
    }

    [Fact]
    public void Triple_ValueEquality_SameFields_CompareEqual()
    {
        // readonly record struct → structural value equality across all fields.
        var at = new DateTimeOffset(2026, 8, 20, 16, 29, 0, TimeSpan.Zero);
        var a = AttributionTriple.MarkAt(at, "twilio-inbound:X");
        var b = AttributionTriple.MarkAt(at, "twilio-inbound:X");

        a.Should().Be(b, "record struct value equality across all fields");
    }

    [Fact]
    public void Triple_ValueEquality_DifferentSource_CompareUnequal()
    {
        var at = DateTimeOffset.UtcNow;
        var a = AttributionTriple.MarkAt(at, "twilio-inbound:X");
        var b = AttributionTriple.MarkAt(at, "twilio-inbound:Y");

        a.Should().NotBe(b, "differing SourceDescriptor breaks value equality");
    }
}
