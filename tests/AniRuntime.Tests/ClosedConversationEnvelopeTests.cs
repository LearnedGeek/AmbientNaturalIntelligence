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
    //
    // PR #120 review-fix (Devin): case-insensitive matching + blank-as-valid
    // normalization mirrors the downstream retrieval filter
    // (ConsciousSubstrateGistComposer OrdinalIgnoreCase) + store default
    // (SqliteClosedConversationStore blank→"valid"). Prevents envelope tag
    // drifting from the store's actual retrieval treatment.
    [Theory]
    [InlineData("valid",               "closed-conversation.valid")]
    [InlineData("Valid",               "closed-conversation.valid")]                // case-insensitive
    [InlineData("VALID",               "closed-conversation.valid")]                // case-insensitive
    [InlineData("",                    "closed-conversation.valid")]                // blank → valid (store default)
    [InlineData("   ",                 "closed-conversation.valid")]                // whitespace → valid (store default)
    [InlineData("invalid_fabrication", "closed-conversation.invalid-fabrication")]
    [InlineData("Invalid_Fabrication", "closed-conversation.invalid-fabrication")]  // case-insensitive
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

    // PR #120 review-fix (Devin): SemanticKey forwards Record.Embedding so
    // future generic near-duplicate detection over envelopes can consume it.
    // Pre-fix, this was hard-null and the vector was silently dropped at the
    // envelope layer even though the summarizer had already computed it.
    [Fact]
    public void SemanticKey_ForwardsRecordEmbedding_WhenPresent()
    {
        var record = SampleRecord();
        record.Embedding = new[] { 0.1f, 0.2f, 0.3f };

        IProvenancedContent<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = record,
        };
        env.SemanticKey.Should().Equal(0.1f, 0.2f, 0.3f);
    }

    [Fact]
    public void SemanticKey_IsNull_WhenRecordEmbeddingIsNull()
    {
        // Best-effort embedding: summarizer catches embedding failures and
        // persists the record without one. Envelope must not synthesize a
        // vector when the record has none.
        var record = SampleRecord();
        record.Embedding = null;

        IProvenancedContent<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = record,
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
