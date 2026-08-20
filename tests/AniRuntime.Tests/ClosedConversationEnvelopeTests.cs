using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 8c (2026-08-19) — verifies
/// <see cref="ClosedConversationEnvelope"/> implements
/// <see cref="IClosedConversationEnvelope"/> correctly, exposes the
/// wrapped <see cref="ClosedConversationRecord"/> via passthroughs, and
/// produces per-validity SourceType tags that let downstream audit
/// distinguish <c>closed-conversation.valid</c> from
/// <c>closed-conversation.invalid-fabrication</c> etc.
/// </summary>
public class ClosedConversationEnvelopeTests
{
    private static ClosedConversationRecord SampleRecord(string validity = "valid") => new()
    {
        Id           = Guid.NewGuid(),
        ThreadId     = Guid.NewGuid(),
        ClosedAt     = DateTimeOffset.UtcNow,
        Gist         = "bookstore quiet — warm reverie",
        Validity     = validity,
        MarkRegister = new() { ["Tenderness"] = 1.0f },
        AniRegister  = new() { ["Tenderness"] = 1.0f },
    };

    [Fact]
    public void Envelope_WrapsRecord_ExposesPassthroughs()
    {
        var record = SampleRecord();
        var env    = new ClosedConversationEnvelope { Record = record };

        env.ThreadId.Should().Be(record.ThreadId);
        env.Gist.Should().Be("bookstore quiet — warm reverie");
        env.Validity.Should().Be("valid");
        env.Record.Should().BeSameAs(record);
    }

    // SourceType tags the record's validity so downstream audit dashboards
    // can filter on the producer-boundary tag without unwrapping. Kebab-case
    // per sibling-envelope convention (frame.ani-interior, world-seed.circadian).
    [Theory]
    [InlineData("valid",               "closed-conversation.valid")]
    [InlineData("invalid_fabrication", "closed-conversation.invalid-fabrication")]
    [InlineData("invalid_other",       "closed-conversation.invalid-other")]
    [InlineData("some_future_state",   "closed-conversation.unknown")]
    public void SourceType_ComposesFromValidity_UsingKebabCaseConvention(string validity, string expected)
    {
        IProvenancedContent<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(validity),
        };
        env.SourceType.Should().Be(expected);
    }

    [Fact]
    public void Producer_Identifies_ClosedConversationSummarizer()
    {
        IProvenancedContent<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };
        env.Producer.Should().Be("ClosedConversationSummarizer");
    }

    [Fact]
    public async Task CreatedAt_IsStableAcrossReads()
    {
        // Sibling-impl discipline (PR #112): captured once at construction.
        IProvenancedContent<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };

        var first = env.CreatedAt;
        await Task.Delay(20);
        var second = env.CreatedAt;

        second.Should().Be(first,
            "CreatedAt must be captured once at construction, not recomputed on each read");
    }

    [Fact]
    public void SemanticKey_IsNull()
    {
        IProvenancedContent<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };
        env.SemanticKey.Should().BeNull();
    }

    [Fact]
    public void Content_ReturnsWrappedRecord_SingleSourceOfTruth()
    {
        var record = SampleRecord();
        IProvenancedContent<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = record,
        };
        env.Content.Should().BeSameAs(record,
            "Content is the canonical read path; Record is construction shorthand pointing at the same object");
    }
}
