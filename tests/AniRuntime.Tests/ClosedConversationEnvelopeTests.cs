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

    // ─── F-3 U8 (2026-08-24) — IComposerEmission surface ─────────────
    //
    // The envelope now also carries composer-emission attribution so the
    // thread-close summarizer's record-author attribution flows on the
    // same surface as F-1 producer provenance. Below tests pin the six
    // IComposerEmission members plus the shared-timestamp contract and
    // the AttributionTriple projection.

    [Fact]
    public void ComposerRole_Identifies_ClosedThreadSummary()
    {
        IComposerEmission<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };
        env.ComposerRole.Should().Be(CognitiveProducerKind.ClosedThreadSummary,
            "thread-close summarizer identifies as ClosedThreadSummary — matches CognitiveProducerKind and the F-1 Producer string tag");
    }

    [Fact]
    public void AttributedTo_IsAni_ForSummarizerAuthoredContent()
    {
        // The summarizer is Ani-authored: her LLM produces the paraphrased
        // gist over her own thread-history substrate. Matches the ten other
        // composer wrap sites migrated in F-3 U3–U7.
        IComposerEmission<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };
        env.AttributedTo.Should().Be(AttributedTo.Ani);
    }

    [Fact]
    public void AttributionTrust_IsVerified_WhenComposerAuthoredEmission()
    {
        // The composer knows it authored the content (the LLM call is the
        // emission point). No fallback-reconstruction path at this producer,
        // so trust is always "verified" — no "unverified" branch exists.
        IComposerEmission<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };
        env.AttributionTrust.Should().Be("verified");
    }

    [Fact]
    public void AttributedSourceDescriptor_IsNull_ForThisComposer()
    {
        // Emission-side scaffolding descriptor (prompt-template ID, model
        // name, session identifier) is not tracked at this wrap site.
        IComposerEmission<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };
        env.AttributedSourceDescriptor.Should().BeNull();
    }

    [Fact]
    public void EmittedAt_SharesTimestampWith_CreatedAt()
    {
        // F-3 U8 timestamp-sharing contract: the F-1 CreatedAt and F-3
        // EmittedAt describe the same instant (envelope-emit time is
        // composer-emit time for this producer). Backing by a single
        // captured field prevents the surfaces from drifting.
        var envelope = new ClosedConversationEnvelope { Record = SampleRecord() };

        var provenanced = (IProvenancedContent<ClosedConversationRecord>)envelope;
        var emission    = (IComposerEmission<ClosedConversationRecord>)envelope;

        emission.EmittedAt.Should().Be(provenanced.CreatedAt,
            "F-1 CreatedAt and F-3 EmittedAt must share the single construction-captured instant");
    }

    [Fact]
    public async Task EmittedAt_IsStableAcrossReads()
    {
        // Sibling-impl discipline mirrored from CreatedAt: EmittedAt is
        // captured once at construction, not recomputed on each read.
        IComposerEmission<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };

        var first = env.EmittedAt;
        await Task.Delay(20);
        var second = env.EmittedAt;

        second.Should().Be(first);
    }

    [Fact]
    public void ContentAccess_Unambiguous_ThroughUnifiedEnvelopeInterface()
    {
        // The IClosedConversationEnvelope interface promotes Content with
        // `new` to disambiguate the two inherited Content members. This
        // test pins that a consumer holding the unified interface can
        // read Content without an interface-cast — the U8 change must not
        // regress the pre-existing consumer ergonomics.
        var record = SampleRecord();
        IClosedConversationEnvelope env = new ClosedConversationEnvelope
        {
            Record = record,
        };
        env.Content.Should().BeSameAs(record);
    }

    [Fact]
    public void Content_IsSameInstance_AcrossAllThreeInterfaceSurfaces()
    {
        // All three Content members (unified interface, F-1, F-3) point at
        // the same wrapped record instance — one payload, three surfaces.
        var record = SampleRecord();
        var envelope = new ClosedConversationEnvelope { Record = record };

        var unified     = ((IClosedConversationEnvelope)envelope).Content;
        var provenanced = ((IProvenancedContent<ClosedConversationRecord>)envelope).Content;
        var emission    = ((IComposerEmission<ClosedConversationRecord>)envelope).Content;

        unified.Should().BeSameAs(record);
        provenanced.Should().BeSameAs(record);
        emission.Should().BeSameAs(record);
    }

    [Fact]
    public void ToAttributionTriple_ProjectsAniVerified_WithNullSourceGrounding()
    {
        // The F-3 projection helper reads composer-emit attribution off
        // the envelope. SourceRecordId and SourceDescriptor are left null
        // by the projection (see Devin PR #137 review-fix on
        // ComposerEmissionExtensions.ToAttributionTriple).
        IComposerEmission<ClosedConversationRecord> env = new ClosedConversationEnvelope
        {
            Record = SampleRecord(),
        };

        var triple = env.ToAttributionTriple();

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.Trust.Should().Be("verified");
        triple.AttributedAt.Should().Be(env.EmittedAt);
        triple.SourceRecordId.Should().BeNull();
        triple.SourceDescriptor.Should().BeNull();
    }
}
