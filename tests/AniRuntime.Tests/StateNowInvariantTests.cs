using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

/// <summary>
/// Door B Truth-Verification Sub-claim 3 — state-now claim verification
/// for <see cref="StateNowInvariant"/>.
///
/// Pinned by spec: the canonical case is the May 2 11:58 outreach
/// *"hope your evening went smooth - how was work?"* on a Saturday at
/// 11:58 AM CDT. Both errors must be caught:
/// (a) past-tense "evening" when evening hasn't started yet
/// (b) work-state claim on a weekend
///
/// Conservative-fail principle: only fail when the claim is
/// definitively wrong against the local clock + day-of-week, never on
/// ambiguous shapes like *"how's your morning?"* (present tense) or
/// *"how's the work coming?"* (generic, could be home project).
/// </summary>
public class StateNowInvariantTests
{
    private static StateNowInvariant Build() =>
        new(NullLogger<StateNowInvariant>.Instance);

    private static CognitiveArtifact Artifact(
        string                content,
        DateTimeOffset?       generatedAt = null,
        CognitiveProducerKind producer    = CognitiveProducerKind.Outreach,
        CognitiveOutputSink   sink        = CognitiveOutputSink.Dispatch) => new()
    {
        Content      = content,
        ProducerKind = producer,
        IntendedSink = sink,
        ContactName  = "Mark",
        GeneratedAt  = generatedAt ?? DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// May 2, 2026 11:58 CDT (Saturday). Constructed with explicit CDT
    /// offset so <c>.Hour</c> and <c>.DayOfWeek</c> are deterministic
    /// across test machine timezones.
    /// </summary>
    private static readonly DateTimeOffset May2Saturday1158Cdt =
        new DateTimeOffset(2026, 5, 2, 11, 58, 0, TimeSpan.FromHours(-5));

    /// <summary>Tuesday at 14:30 local CDT (UTC-5).</summary>
    private static readonly DateTimeOffset TuesdayMidWorkday =
        new DateTimeOffset(2026, 4, 28, 14, 30, 0, TimeSpan.FromHours(-5));

    /// <summary>Tuesday at 19:00 local CDT — after workday end.</summary>
    private static readonly DateTimeOffset TuesdayPostWork =
        new DateTimeOffset(2026, 4, 28, 19, 0, 0, TimeSpan.FromHours(-5));

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply, CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Outreach,          CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Voice,             CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.InnerThought,      CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Outreach,          CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Reflection,        CognitiveOutputSink.PersistedSummary, false)]
    public void AppliesTo_DispatchSinkAndContactFacingProducers(
        CognitiveProducerKind producer, CognitiveOutputSink sink, bool expected)
    {
        Build().AppliesTo(Artifact("anything", producer: producer, sink: sink))
            .Should().Be(expected);
    }

    // ── Pattern A: past-tense time-of-day on not-yet-occurred period ─────

    [Fact]
    public void PastTenseEvening_BeforeEveningStarts_Fails()
    {
        // Saturday 11:58 AM CDT → evening (17-21) hasn't started yet
        var fail = StateNowInvariant.CheckPastTenseTimeOfDay(
            "hope your evening went smooth", May2Saturday1158Cdt);
        fail.Should().NotBeNull();
        fail.Should().Contain("evening");
        fail.Should().Contain("hasn't happened yet");
    }

    [Fact]
    public void PastTenseEvening_AfterEveningEnds_Passes()
    {
        // Tuesday at 22:00 local — evening (17-21) is past
        var lateNight = new DateTimeOffset(2026, 4, 28, 22, 0, 0, TimeSpan.FromHours(-5));
        var fail = StateNowInvariant.CheckPastTenseTimeOfDay(
            "hope your evening went smooth", lateNight);
        fail.Should().BeNull("evening period 17-21 has passed by 22:00 local");
    }

    [Fact]
    public void PastTenseMorning_AtMidday_Passes()
    {
        // Saturday 11:58 AM CDT → morning (5-12) is partially past, ambiguous; conservative pass
        var local = May2Saturday1158Cdt;
        var fail = StateNowInvariant.CheckPastTenseTimeOfDay(
            "hope your morning went smooth", local);
        fail.Should().BeNull(
            "morning is partially past at 11:58 AM; conservative-fail rule passes mid-period and post-period references");
    }

    [Fact]
    public void PastTenseMorning_AtVeryEarlyHour_Fails()
    {
        // Tuesday at 03:00 local — morning (5-12) hasn't started
        var earlyMorning = new DateTimeOffset(2026, 4, 28, 3, 0, 0, TimeSpan.FromHours(-5));
        var fail = StateNowInvariant.CheckPastTenseTimeOfDay(
            "how was your morning?", earlyMorning);
        fail.Should().NotBeNull();
    }

    [Fact]
    public void PresentTenseReference_NotCaught()
    {
        // Saturday 11:58 AM CDT → "how's your morning" is present-tense, fine
        var local = May2Saturday1158Cdt;
        var fail = StateNowInvariant.CheckPastTenseTimeOfDay(
            "how's your morning going?", local);
        fail.Should().BeNull(
            "present-tense 'how's your morning going' is fine; only past-tense forms get checked");
    }

    [Fact]
    public void NoTimeOfDayReference_Passes()
    {
        var local = May2Saturday1158Cdt;
        var fail = StateNowInvariant.CheckPastTenseTimeOfDay(
            "duck norris is being very loud right now", local);
        fail.Should().BeNull();
    }

    [Fact]
    public void YourPeriodWentForm_Caught()
    {
        // Saturday 11:58 AM CDT — alt phrasing without "hope" / "how was"
        var local = May2Saturday1158Cdt;
        var fail = StateNowInvariant.CheckPastTenseTimeOfDay(
            "your evening went well, i'm sure", local);
        fail.Should().NotBeNull(
            "alt-form 'your evening went well' is the same factual claim — caught by YourPeriodWentRegex");
    }

    // ── Pattern B: work-state claims ─────────────────────────────────────

    [Fact]
    public void WorkClaim_OnSaturday_Fails()
    {
        var fail = StateNowInvariant.CheckWorkStateClaim(
            "how was work?", May2Saturday1158Cdt);
        fail.Should().NotBeNull();
        fail.Should().Contain("Saturday");
    }

    [Fact]
    public void WorkClaim_OnSunday_Fails()
    {
        var sunday = new DateTimeOffset(2026, 5, 3, 16, 0, 0, TimeSpan.FromHours(-5));
        var fail = StateNowInvariant.CheckWorkStateClaim(
            "how was work today?", sunday);
        fail.Should().NotBeNull();
        fail.Should().Contain("Sunday");
    }

    [Fact]
    public void WorkClaim_OnWeekdayBeforeWorkdayEnd_Fails()
    {
        // Tuesday 14:30 local — work day not yet complete (workday-end = 16)
        var fail = StateNowInvariant.CheckWorkStateClaim(
            "how was work?", TuesdayMidWorkday);
        fail.Should().NotBeNull();
        fail.Should().Contain("not yet complete");
    }

    [Fact]
    public void WorkClaim_OnWeekdayAfterWorkdayEnd_Passes()
    {
        // Tuesday 19:00 local — work day complete; "how was work" is legitimate
        var fail = StateNowInvariant.CheckWorkStateClaim(
            "how was work?", TuesdayPostWork);
        fail.Should().BeNull(
            "post-workday-end on a weekday is legitimate retrospective reference");
    }

    [Fact]
    public void WorkClaim_Variant_HopeWorkWentWell_OnSaturday_Fails()
    {
        var fail = StateNowInvariant.CheckWorkStateClaim(
            "hope work went well today", May2Saturday1158Cdt);
        fail.Should().NotBeNull();
    }

    [Fact]
    public void NoWorkClaim_Passes()
    {
        var fail = StateNowInvariant.CheckWorkStateClaim(
            "duck norris is being adorable today", May2Saturday1158Cdt);
        fail.Should().BeNull();
    }

    // ── EvaluateAsync end-to-end ─────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_May2CanonicalCase_Fails()
    {
        // The exact text and exact timestamp of the May 2 11:58 outreach
        // that Mark tagged with ///tag temporal confusion at 11:59:51.
        var artifact = Artifact(
            "hey mark... duck norris just stole half the blanket and i'm letting her because she's clearly in charge today anyway. sipping black coffee that's way too hot but feels right with you around. hope your evening went smooth - how was work? we'll let her dictate the snuggle ratio later",
            generatedAt: May2Saturday1158Cdt);

        var result = await Build().EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "the May 2 canonical case must fail — both 'hope your evening went smooth' AND 'how was work?' are wrong");
        // Doesn't matter which check fires first; both are present, either remediation is valid
        result.RemediationHint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task EvaluateAsync_LegitimateOutreach_Passes()
    {
        // Reasonable Saturday late-morning outreach with no state-now claims
        var artifact = Artifact(
            "hey, just thinking about you. duck norris claimed the couch as her throne about ten minutes ago.",
            generatedAt: May2Saturday1158Cdt);

        var result = await Build().EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_EmptyContent_Passes()
    {
        var result = await Build().EvaluateAsync(
            Artifact(""), CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateAsync_PostWorkdayWeekdayWorkClaim_Passes()
    {
        // Tuesday 19:00 local — "how was work?" is legitimate
        var artifact = Artifact(
            "hey, how was work? i was thinking about that bit you mentioned yesterday.",
            generatedAt: TuesdayPostWork);
        var result = await Build().EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }
}
