using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P1 (2026-08-21) — verifies the
/// <see cref="IAttributedContent{T}"/> interface contract via a minimal
/// test-only implementation. No production records implement this
/// interface yet (P6 wires producers). This test pins the interface
/// shape so P6/P2/P3 inherit a stable contract.
/// </summary>
public class IAttributedContentContractTests
{
    /// <summary>
    /// Minimal test-only implementation. Records with utterance semantics
    /// (MemoryRecord, InnerThoughtResult, ConsciousSubstrateGist,
    /// ClosedConversationRecord, PerceptionEvent, ChatMessage) will
    /// implement this interface in later phases.
    /// </summary>
    private sealed class TestAttributedString : IAttributedContent<string>
    {
        public required string Content { get; init; }
        public required AttributedTo AttributedTo { get; init; }
        public DateTimeOffset? AttributedAt { get; init; }
        public Guid? AttributedSourceRecordId { get; init; }
        public string? AttributedSourceDescriptor { get; init; }
        public string AttributionTrust { get; init; } = "verified";
    }

    [Fact]
    public void Interface_Covariance_AllowsBaseReferenceToDerivedContent()
    {
        // out T covariance — an IAttributedContent<string> can be assigned
        // to IAttributedContent<object>. Mirrors IProvenancedContent<out T>.
        IAttributedContent<string> stringContent = new TestAttributedString
        {
            Content = "hello", AttributedTo = AttributedTo.Ani,
        };
        IAttributedContent<object> objectContent = stringContent;

        objectContent.Content.Should().Be("hello");
    }

    [Fact]
    public void Contract_MinimalConstruction_YieldsUnknownWithNullSource()
    {
        // Default trust = "verified" (from init default), but AttributedTo
        // has no default — required initialization prevents silent Unknown.
        var a = new TestAttributedString
        {
            Content = "test",
            AttributedTo = AttributedTo.Unknown,
        };

        a.AttributedTo.Should().Be(AttributedTo.Unknown);
        a.AttributedAt.Should().BeNull();
        a.AttributedSourceRecordId.Should().BeNull();
        a.AttributedSourceDescriptor.Should().BeNull();
        a.AttributionTrust.Should().Be("verified", "default trust — implementers can override");
    }

    [Fact]
    public void Contract_SourceRecordIdAndDescriptor_ConventionOnlyOneSet()
    {
        // Design plan D2: producers pick one shape per record — FK OR
        // descriptor, not both. This test documents the convention
        // (not enforced structurally; producers own the discipline).
        var recordSourced = new TestAttributedString
        {
            Content = "reflected content",
            AttributedTo = AttributedTo.Ani,
            AttributedSourceRecordId = Guid.NewGuid(),
        };
        recordSourced.AttributedSourceDescriptor.Should().BeNull();

        var descriptorSourced = new TestAttributedString
        {
            Content = "twilio inbound content",
            AttributedTo = AttributedTo.Mark,
            AttributedSourceDescriptor = "twilio-inbound:SM<sid>",
        };
        descriptorSourced.AttributedSourceRecordId.Should().BeNull();
    }

    [Fact]
    public void Contract_AttributionTrust_CarriesKnownStringValues()
    {
        // Design plan D5: three canonical trust values. Interface uses
        // string (not enum) for forward-compat with future categories.
        var verified = new TestAttributedString
        {
            Content = "x", AttributedTo = AttributedTo.Ani, AttributionTrust = "verified",
        };
        var unverified = new TestAttributedString
        {
            Content = "x", AttributedTo = AttributedTo.Ani, AttributionTrust = "unverified",
        };
        var historical = new TestAttributedString
        {
            Content = "x", AttributedTo = AttributedTo.Ani, AttributionTrust = "unverified-historical",
        };

        verified.AttributionTrust.Should().Be("verified");
        unverified.AttributionTrust.Should().Be("unverified");
        historical.AttributionTrust.Should().Be("unverified-historical");
    }

    [Fact]
    public void Contract_TripleFactory_MapsToImplementationFields()
    {
        // AttributionTriple → IAttributedContent<T> field mapping.
        // Producers construct the triple once and hand off; persistence
        // layer maps triple fields onto the record's IAttributedContent<T>
        // fields. This test pins the field-name correspondence.
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.MarkAt(at, "twilio-inbound:SM<sid>");

        var attributed = new TestAttributedString
        {
            Content = "hey",
            AttributedTo = triple.AttributedTo,
            AttributedAt = triple.AttributedAt,
            AttributedSourceRecordId = triple.SourceRecordId,
            AttributedSourceDescriptor = triple.SourceDescriptor,
            AttributionTrust = triple.Trust,
        };

        attributed.AttributedTo.Should().Be(AttributedTo.Mark);
        attributed.AttributedAt.Should().Be(at);
        attributed.AttributedSourceRecordId.Should().BeNull();
        attributed.AttributedSourceDescriptor.Should().Be("twilio-inbound:SM<sid>");
        attributed.AttributionTrust.Should().Be("verified");
    }
}
