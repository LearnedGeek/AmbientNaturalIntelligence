using AniRuntime.Core;
using AniRuntime.Core.Models;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// Regression scenarios for <b>FC-001 — Active-thread continuity broken</b>
/// (see <c>docs/spec/ANI-Failure-Class-Registry.md</c>).
///
/// SPEC (what the system MUST do, independent of current implementation):
///   When Ani dispatches an outreach and OutreachPhase records it into the
///   active conversation thread via the same data path it uses in production
///   (<c>RecordOutboundInThreadAsync → IConversationService.AddMessageAsync</c>),
///   any subsequent retrieval of that thread's messages MUST include the
///   recorded outreach so downstream consumers (ConversationReplyPhase
///   substrate construction, prompt building, retrieval scoring) can see it.
///
/// EMPIRICAL ANCHOR (production failure that motivated this scenario):
///   May 12, 2026 21:35–21:51 CDT — Ani dispatched a windshield outreach,
///   Mark replied 15 minutes later asking about the windshield, the reply
///   path's retrieval substrate did NOT include the prior outreach, and the
///   composition path produced a verbatim parrot that anti-parrot caught
///   then fell through to SafeAck.
///
/// SCENARIO COVERAGE — three levels of scope from smallest to largest:
///   - <see cref="FC001a_DataPath_RoundTrip"/>:
///       Conversation service AddMessage → GetActiveThread round-trip.
///       Smallest possible scope. If this fails, the bug is at the data layer.
///   - <see cref="FC001b_ActiveThread_RetainsOrderedHistory"/>:
///       Multi-message ordering preserved across the round-trip.
///   - <see cref="FC001c_AniOutreach_PrecedingMarkInbound_AppearsInThreadMessages"/>:
///       The exact production sequence — Ani dispatches first, Mark inbound
///       arrives second; the thread surface used by ConversationReplyPhase
///       (via GetActiveThreadAsync) MUST return both messages so the reply
///       path has substrate to ground on.
///
/// CURRENT EXPECTED STATUS — OPEN. The production failure suggests one or
/// more of these scenarios will fail. If all PASS, the bug is higher in the
/// stack (ContextBuilder, ConversationReplyPhase prompt-building, or the
/// MemGPT compressor) and additional scenarios are required to localize it
/// (FC-001d, FC-001e — to be authored).
///
/// TDD discipline: assertions describe the SPEC. If current code does not
/// satisfy the spec, the scenario fails and that confirms FC-001 OPEN at
/// the tested level. Do NOT soften assertions to match current behavior.
/// </summary>
public class FC001_ActiveThreadContinuity_Tests : RegressionScenarioBase
{
    // Synthetic, fabricated content — deterministic, decoupled from production data.
    private const string AniOutreachContent = "FC001-FIXTURE: synthetic outreach about a fabricated windshield note";
    private const string MarkInboundContent = "FC001-FIXTURE: synthetic inbound asking about the fabricated windshield";

    /// <summary>
    /// FC-001a — Bare data-path round trip. SPEC: a message added to an
    /// active thread is retrievable via the same thread's read path.
    /// Smallest scope; rules out (or confirms) the data layer as the source
    /// of FC-001.
    /// </summary>
    [Fact]
    public async Task FC001a_DataPath_RoundTrip()
    {
        var thread = await OpenActiveThreadAsync(initiatedBy: Roles.Mark);

        await AppendMessageAsync(thread.Id, Roles.Ani, AniOutreachContent);

        var loaded = await _conversations.GetActiveThreadAsync();

        loaded.Should().NotBeNull("an active thread was opened and should be retrievable");
        loaded!.Messages.Should().ContainSingle(
            "exactly one message was appended and should round-trip through the data layer");
        loaded.Messages[0].Role.Should().Be(Roles.Ani);
        loaded.Messages[0].Content.Should().Be(AniOutreachContent);
    }

    /// <summary>
    /// FC-001b — Multi-message ordering preserved.
    /// SPEC: messages appended in order T0, T1 are returned in order T0, T1
    /// (oldest-first) so downstream consumers see canonical conversational
    /// chronology.
    /// </summary>
    [Fact]
    public async Task FC001b_ActiveThread_RetainsOrderedHistory()
    {
        var thread = await OpenActiveThreadAsync(initiatedBy: Roles.Mark);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-15);
        var t1 = DateTimeOffset.UtcNow;

        await AppendMessageAsync(thread.Id, Roles.Ani,  AniOutreachContent, sentAt: t0);
        await AppendMessageAsync(thread.Id, Roles.Mark, MarkInboundContent, sentAt: t1);

        var loaded = await _conversations.GetActiveThreadAsync();

        loaded.Should().NotBeNull();
        loaded!.Messages.Should().HaveCount(2);
        loaded.Messages[0].Role.Should().Be(Roles.Ani,    "Ani's outreach was sent FIRST");
        loaded.Messages[1].Role.Should().Be(Roles.Mark,   "Mark's inbound followed Ani's outreach");
        loaded.Messages[0].Content.Should().Be(AniOutreachContent);
        loaded.Messages[1].Content.Should().Be(MarkInboundContent);
    }

    /// <summary>
    /// FC-001c — Production sequence reproduction. The exact ordering of the
    /// May 12 windshield failure:
    ///   1. Ani dispatches an outreach (FIRST).
    ///   2. Mark replies asking about the outreach (SECOND).
    /// SPEC: the active thread returned to a downstream consumer
    /// (ConversationReplyPhase at <c>ConversationReplyPhase.cs:209</c> reads
    /// <c>thread.Messages[^1]</c> for the latest inbound and uses
    /// <c>_compressor.CompressIfNeededAsync(thread, ...)</c> at line 194 for
    /// RecentHistory) MUST contain BOTH messages so the reply path can ground
    /// its response in Ani's prior outreach.
    ///
    /// If this fails, the bug is at the data layer and FC-001 is localized
    /// there. If this passes, the bug is higher in the stack — FC-001d
    /// (ContextBuilder integration) is the next scenario to author.
    /// </summary>
    [Fact]
    public async Task FC001c_AniOutreach_PrecedingMarkInbound_AppearsInThreadMessages()
    {
        var thread = await OpenActiveThreadAsync(initiatedBy: Roles.Mark);

        // Step 1 — Ani dispatches an outreach. Production calls
        // OutreachPhase.RecordOutboundInThreadAsync, which in turn calls
        // IConversationService.AddMessageAsync. The data-path payload here
        // is identical to production.
        await AppendMessageAsync(
            thread.Id,
            Roles.Ani,
            AniOutreachContent,
            sentAt: DateTimeOffset.UtcNow.AddMinutes(-15));

        // Step 2 — Mark replies. Production calls
        // TwilioInboundPerceptionSource → ConversationService.AddMessageAsync.
        await AppendMessageAsync(
            thread.Id,
            Roles.Mark,
            MarkInboundContent,
            sentAt: DateTimeOffset.UtcNow);

        // Step 3 — Downstream consumer (ConversationReplyPhase) reads the
        // active thread. The SPEC: both messages must be present so the
        // reply path has substrate.
        var loaded = await _conversations.GetActiveThreadAsync();

        loaded.Should().NotBeNull("an active thread was opened and not closed");
        loaded!.Messages.Should().HaveCount(2,
            "both Ani's outreach AND Mark's reply were recorded on this active thread; " +
            "the reply path requires both for grounded composition");

        loaded.Messages.Should().Contain(m =>
            m.Role == Roles.Ani && m.Content == AniOutreachContent,
            "Ani's prior outreach MUST be visible in the thread that the reply path reads — " +
            "this is the FC-001 SPEC binding constraint: composition cannot ground a follow-up " +
            "reply on content the system does not surface to it");

        loaded.Messages.Should().Contain(m =>
            m.Role == Roles.Mark && m.Content == MarkInboundContent,
            "Mark's inbound must of course also be visible (the message being replied to)");
    }
}
