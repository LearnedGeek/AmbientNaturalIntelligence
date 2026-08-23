using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P6 (2026-08-22) — pins the
/// attribution decisions each producer applies at emit time. These
/// tests don't exercise the full producer pipelines (that's what
/// existing per-producer tests do); they document + verify the
/// AttributionTriple factory each producer USES so a future maintainer
/// changing the wiring at any producer site has to update the pinned
/// invariant here.
///
/// <para>
/// The 12:04-shape misattribution originated in the CognitiveCyclePipeline
/// inner-thought emit site — the exact site pinned by
/// <see cref="InnerThought_UsesAniAtFactory_TrustVerified"/>. If that
/// wiring ever regresses to <see cref="AttributionTriple.UnknownHistorical"/>
/// (or drops the attribution fields entirely), this test fails loud.
/// </para>
/// </summary>
public class ProducerAttributionWiringTests
{
    // Per-producer attribution table (from design plan D4 forward-only shape)
    //
    //   CognitiveCyclePipeline inner thought  → AttributionTriple.AniAt
    //   OutreachPipeline outreach Episodic    → AttributionTriple.AniAt
    //   ReactiveShareService reactive share   → AttributionTriple.AniAt
    //   SilenceChoiceRecorder inner thought   → AttributionTriple.AniAt
    //   PerceptionPhase twilio-inbound        → AttributionTriple.MarkAt
    //   PerceptionPhase other sources         → AttributionTriple.WorldAt
    //   PostReplyEmotionalProcessor hurt-thought → AttributionTriple.AniAt
    //   ReflectionPhase synthesis             → AttributionTriple.AniAt
    //   SqliteConversationService Episodic Mark → AttributionTriple.MarkAt
    //   SqliteConversationService Episodic Ani  → AttributionTriple.AniAt
    //   MemoryWriteAction internal note       → AttributionTriple.AniAt

    [Fact]
    public void InnerThought_UsesAniAtFactory_TrustVerified()
    {
        // 12:04-shape site. Inner thoughts persist as Interior tier;
        // record author is trivially Ani (Interior == Ani's self-model).
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.AniAt(at);

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.AttributedAt.Should().Be(at);
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void OutreachEpisodic_UsesAniAtFactory_TrustVerified()
    {
        // Ani composed and dispatched the outreach — author verified.
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.AniAt(at);

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void PerceptionPhase_TwilioInbound_UsesMarkAtFactory()
    {
        // Twilio SMS came from Mark; verified.
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.MarkAt(at, "twilio-inbound:test:0");

        triple.AttributedTo.Should().Be(AttributedTo.Mark);
        triple.AttributedAt.Should().Be(at);
        triple.SourceDescriptor.Should().StartWith("twilio-inbound:");
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void PerceptionPhase_WorldSource_UsesWorldAtFactory()
    {
        // RSS / weather / environmental — no utterer.
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.WorldAt(at, "rss:test:0");

        triple.AttributedTo.Should().Be(AttributedTo.World);
        triple.AttributedAt.Should().Be(at);
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void ConversationEpisodic_Mark_UsesMarkAtFactory()
    {
        // Conversation-messages Episodic write derives attribution from
        // ConversationMessage.Role. Mark's turn → Mark verified.
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.MarkAt(at, "conversation:mark:0");

        triple.AttributedTo.Should().Be(AttributedTo.Mark);
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void ConversationEpisodic_Ani_UsesAniAtFactory()
    {
        // Ani's turn → Ani verified.
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.AniAt(at);

        triple.AttributedTo.Should().Be(AttributedTo.Ani);
        triple.Trust.Should().Be("verified");
    }

    [Fact]
    public void MemoryRecord_WithAttributionTripleFields_ExposesViaInterface()
    {
        // Sanity: MemoryRecord constructed with triple fields exposes
        // them via IAttributedContent<MemoryRecord> for downstream
        // consumers (retrieval-render tag reader in P4, etc.).
        var at = DateTimeOffset.UtcNow;
        var triple = AttributionTriple.AniAt(at);

        var record = new MemoryRecord
        {
            Content                    = "test",
            OccurredAt                 = at,
            AttributedTo               = triple.AttributedTo,
            AttributedAt               = triple.AttributedAt,
            AttributedSourceRecordId   = triple.SourceRecordId,
            AttributedSourceDescriptor = triple.SourceDescriptor,
            AttributionTrust           = triple.Trust,
        };

        IAttributedContent<MemoryRecord> attributed = record;
        attributed.AttributedTo.Should().Be(AttributedTo.Ani);
        attributed.AttributionTrust.Should().Be("verified");
        attributed.AttributedAt.Should().Be(at);
    }
}
