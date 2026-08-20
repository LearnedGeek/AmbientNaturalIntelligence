using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 8d (2026-08-19) — verifies
/// <see cref="OutreachDecisionEnvelope"/> implements
/// <see cref="IOutreachDecisionEnvelope"/> correctly, exposes the wrapped
/// <see cref="OutreachDecision"/> record via passthroughs, and produces
/// per-producer SourceType tags distinguishing the four production sites
/// (LlmParsed / AdminMeta / SmsReply / ReactiveShare) so downstream audit
/// can tell the LLM-driven outreach path from admin-meta / reply /
/// reactive-share without inspecting the record's IsAdminMeta field or
/// the Reasoning free-text.
/// </summary>
public class OutreachDecisionEnvelopeTests
{
    private static OutreachDecision Sample(bool shouldReach = true, bool isAdminMeta = false, float confidence = 0.8f)
        => new()
        {
            ShouldReach = shouldReach,
            Message     = "hey — thinking of you",
            ActionType  = ActionTypes.Sms,
            Reasoning   = "test",
            Confidence  = confidence,
            IsAdminMeta = isAdminMeta,
        };

    [Fact]
    public void Envelope_WrapsRecord_ExposesPassthroughs()
    {
        var decision = Sample(shouldReach: true, isAdminMeta: false, confidence: 0.73f);
        var env = new OutreachDecisionEnvelope
        {
            Decision = decision,
            Source   = OutreachDecisionSource.LlmParsed,
        };

        env.ShouldReach.Should().BeTrue();
        env.Confidence.Should().Be(0.73f);
        env.IsAdminMeta.Should().BeFalse();
        env.Decision.Should().BeSameAs(decision);
    }

    // SourceType tags distinguish the four production sites. Kebab-case per
    // sibling-envelope convention (frame.ani-interior, world-seed.circadian,
    // closed-conversation.valid).
    [Theory]
    [InlineData(OutreachDecisionSource.LlmParsed,     "outreach-decision.llm-parsed")]
    [InlineData(OutreachDecisionSource.AdminMeta,     "outreach-decision.admin-meta")]
    [InlineData(OutreachDecisionSource.SmsReply,      "outreach-decision.sms-reply")]
    [InlineData(OutreachDecisionSource.ReactiveShare, "outreach-decision.reactive-share")]
    public void SourceType_ComposesFromSource_UsingKebabCaseConvention(OutreachDecisionSource source, string expected)
    {
        IProvenancedContent<OutreachDecision> env = new OutreachDecisionEnvelope
        {
            Decision = Sample(),
            Source   = source,
        };
        env.SourceType.Should().Be(expected);
    }

    [Theory]
    [InlineData(OutreachDecisionSource.LlmParsed,     "OutreachPipeline")]
    [InlineData(OutreachDecisionSource.AdminMeta,     "AdminCommandHandler")]
    [InlineData(OutreachDecisionSource.SmsReply,      "SmsReplyChannel")]
    [InlineData(OutreachDecisionSource.ReactiveShare, "ReactiveShareService")]
    public void Producer_IdentifiesConstructingSite(OutreachDecisionSource source, string expected)
    {
        IProvenancedContent<OutreachDecision> env = new OutreachDecisionEnvelope
        {
            Decision = Sample(),
            Source   = source,
        };
        env.Producer.Should().Be(expected);
    }

    [Fact]
    public async Task CreatedAt_IsStableAcrossReads()
    {
        // Sibling-impl discipline (PR #112): captured once at construction.
        IProvenancedContent<OutreachDecision> env = new OutreachDecisionEnvelope
        {
            Decision = Sample(),
            Source   = OutreachDecisionSource.LlmParsed,
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
        // OutreachDecision has no embedding surface, unlike ClosedConversationRecord.
        IProvenancedContent<OutreachDecision> env = new OutreachDecisionEnvelope
        {
            Decision = Sample(),
            Source   = OutreachDecisionSource.LlmParsed,
        };
        env.SemanticKey.Should().BeNull();
    }

    [Fact]
    public void Content_ReturnsWrappedDecision_SingleSourceOfTruth()
    {
        var decision = Sample();
        IProvenancedContent<OutreachDecision> env = new OutreachDecisionEnvelope
        {
            Decision = decision,
            Source   = OutreachDecisionSource.LlmParsed,
        };
        env.Content.Should().BeSameAs(decision,
            "Content is the canonical read path; Decision is construction shorthand pointing at the same object");
    }

    [Fact]
    public void IsAdminMeta_Passthrough_ReflectsAdminMetaRecord()
    {
        // AdminCommandHandler sets IsAdminMeta = true; envelope passthrough
        // exposes it without unwrapping — matches OutreachDecisionSource.AdminMeta.
        var env = new OutreachDecisionEnvelope
        {
            Decision = Sample(isAdminMeta: true),
            Source   = OutreachDecisionSource.AdminMeta,
        };
        env.IsAdminMeta.Should().BeTrue();
    }
}
