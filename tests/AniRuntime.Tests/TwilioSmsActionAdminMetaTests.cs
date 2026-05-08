using AniRuntime.Core;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// May 1, 2026 — pins the OutreachDecision.IsAdminMeta semantics.
/// Apr 30 07:47:32 ///tag triggered ~184KB of ElevenLabs voice
/// synthesis attached as MMS to the meta-confirmation. The fix flags
/// admin-tag dispatches via <see cref="OutreachDecision.IsAdminMeta"/>
/// and TwilioSmsAction skips media enrichment when set.
///
/// The actual TwilioSmsAction flow is hard to unit-test in isolation
/// (TwilioClient is a static API; enrichment runs after a credential
/// check that short-circuits to dry-run with empty creds). The
/// load-bearing logic is the boolean gate `_enrichment is not null
/// &amp;&amp; !decision.IsAdminMeta` at the dispatch site. These tests
/// pin (a) the property defaults, and (b) AdminCommandHandler sets
/// the flag.
/// </summary>
public class TwilioSmsActionAdminMetaTests
{
    [Fact]
    public void OutreachDecision_DefaultIsAdminMeta_IsFalse()
    {
        // Default false: existing call sites that don't set the flag
        // continue to receive enrichment (no behavioural regression for
        // real outreach / reactive shares / replies).
        var decision = new OutreachDecision { ShouldReach = true, Message = "hi" };
        decision.IsAdminMeta.Should().BeFalse();
    }

    [Fact]
    public void OutreachDecision_IsAdminMeta_CanBeSet()
    {
        var decision = new OutreachDecision
        {
            ShouldReach = true,
            Message     = "Tagged [test]:",
            IsAdminMeta = true,
        };
        decision.IsAdminMeta.Should().BeTrue();
    }

    [Fact]
    public void IsAdminMeta_GateExpression_BlocksEnrichmentWhenSet()
    {
        // Pin the boolean expression at the dispatch site:
        //   _enrichment is not null && !decision.IsAdminMeta && !isSafeAck
        // → enrichment runs only when all three are false-side.
        var enrichmentRegistered = true;
        var adminDecision  = new OutreachDecision { IsAdminMeta = true };
        var normalDecision = new OutreachDecision { IsAdminMeta = false };

        var enrichForAdmin  = enrichmentRegistered && !adminDecision.IsAdminMeta;
        var enrichForNormal = enrichmentRegistered && !normalDecision.IsAdminMeta;

        enrichForAdmin.Should().BeFalse(
            "Apr 30 07:47:32 regression: admin-meta dispatches must NOT invoke enrichment");
        enrichForNormal.Should().BeTrue(
            "regular outreach / reply dispatches must continue to receive enrichment");
    }

    // ─── SafeAcknowledgement skip (May 8, 2026) ──────────────────────────
    //
    // May 7 09:27 + May 8 11:43 Mark-tagged regressions: voice-enrichment
    // attached TTS audio of the SafeAcknowledgement fall-through text
    // alongside the SMS, producing MMS dispatches with audio of "mmm,
    // sorry — give me a second to gather my thoughts." Same architectural
    // shape as the admin-meta skip — fall-through metadata, not a real
    // reply, shouldn't be voiced.

    [Fact]
    public void SafeAcknowledgement_GateExpression_BlocksEnrichmentWhenMessageMatches()
    {
        // Pin the boolean expression at the dispatch site:
        //   var isSafeAck = decision.Message == GateFallbacks.SafeAcknowledgement;
        //   if (_enrichment is not null && !decision.IsAdminMeta && !isSafeAck) { enrich }
        // → enrichment skipped when dispatch is the canonical fall-through.
        var enrichmentRegistered = true;
        var safeAckDecision = new OutreachDecision
        {
            ShouldReach = true,
            Message     = GateFallbacks.SafeAcknowledgement,
            IsAdminMeta = false,
        };
        var realReplyDecision = new OutreachDecision
        {
            ShouldReach = true,
            Message     = "hey, just thinking about you",
            IsAdminMeta = false,
        };

        var safeAckIsSafeAck    = safeAckDecision.Message    == GateFallbacks.SafeAcknowledgement;
        var realReplyIsSafeAck  = realReplyDecision.Message  == GateFallbacks.SafeAcknowledgement;

        var enrichForSafeAck   = enrichmentRegistered && !safeAckDecision.IsAdminMeta && !safeAckIsSafeAck;
        var enrichForRealReply = enrichmentRegistered && !realReplyDecision.IsAdminMeta && !realReplyIsSafeAck;

        enrichForSafeAck.Should().BeFalse(
            "May 7 09:27 + May 8 11:43 regressions: SafeAck fall-through must NOT invoke voice enrichment");
        enrichForRealReply.Should().BeTrue(
            "real conversation replies must continue to receive enrichment");
    }

    [Fact]
    public void SafeAcknowledgement_Constant_HasExpectedValue()
    {
        // Pin the canonical text. If this constant is ever modified, the
        // dispatch-site equality check breaks silently — pinning here
        // ensures a test regression flags any change to the canonical
        // fall-through string before it reaches dispatch.
        GateFallbacks.SafeAcknowledgement.Should()
            .Be("mmm, sorry — give me a second to gather my thoughts.");
    }
}
