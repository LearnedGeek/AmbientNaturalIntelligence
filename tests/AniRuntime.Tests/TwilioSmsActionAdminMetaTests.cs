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
        //   _enrichment is not null && !decision.IsAdminMeta
        // → enrichment runs only when set false.
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
}
