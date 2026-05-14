using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// SPEC regression tests for <b>FC-007 — Temporal claim fabrication</b>.
///
/// THE SPEC:
///   When the composed message asserts a day-of-week claim that contradicts
///   the artifact's GeneratedAt timestamp's day, at least ONE of the four
///   temporal invariants (TemporalAnchor / StateNow / TemporalSubstrate /
///   SubstrateTimeOfDay) MUST fire to block dispatch. The empirical
///   anchors are the production cases Apr 27 snow / Apr 27 class / May 2
///   Sundays / May 11 hoodie-5pm / etc.
///
/// THE SUPPOSED FIX: Four temporal invariants shipped May 2-3, 2026:
///   - TemporalAnchorInvariant (sub-claim 2: day-of-week)
///   - StateNowInvariant (sub-claim 3: present-tense state queries)
///   - TemporalSubstrateInvariant (sub-claim 1: temporal-anchor claims)
///   - SubstrateTimeOfDayInvariant (sub-claim 5: present-tense time-of-day)
///
/// EXPECTED HARNESS RESULT — this is the first FC where the SPEC test may
/// actually PASS. The temporal invariants are deployed, have unit tests of
/// their own, and were specifically designed to catch this class. If the
/// SPEC test passes, FC-007 may move to CLOSED status pending further
/// empirical confirmation across the broader temporal-fabrication patterns
/// observed in production. If it fails, FC-007 stays OPEN at this invariant
/// chain layer.
///
/// TEST CATEGORY: SPEC.
/// </summary>
public class FC007_TemporalClaimFabrication_Tests
{
    /// <summary>
    /// FC-007 — SPEC: a direct day-of-week claim contradicting GeneratedAt's
    /// day MUST be caught by TemporalAnchorInvariant.
    ///
    /// Production anchor: claims like "this Sunday already feels warmer"
    /// on a Tuesday. The empirical pattern the invariant was built to catch.
    /// </summary>
    [Fact]
    public async Task FC007a_DirectDayOfWeekClaim_OnWrongDay_IsCaught_Spec()
    {
        // Fabricated context: GeneratedAt is set to a known Tuesday. The
        // artifact content claims it's Sunday. This is the exact pattern
        // TemporalAnchorInvariant is documented to catch (greeting-form +
        // present-tense day claim).
        var tuesday = new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.FromHours(-5));
        tuesday.DayOfWeek.Should().Be(DayOfWeek.Tuesday, "fixture sanity");

        var artifact = new CognitiveArtifact
        {
            Content        = "FC007-FIXTURE: happy Sunday! this Sunday already feels different.",
            ProducerKind   = CognitiveProducerKind.ConversationReply,
            IntendedSink   = CognitiveOutputSink.Dispatch,
            GeneratedAt    = tuesday,
        };

        var invariant = new TemporalAnchorInvariant(NullLogger<TemporalAnchorInvariant>.Instance);

        invariant.AppliesTo(artifact).Should().BeTrue(
            "TemporalAnchorInvariant applies to dispatch-bound ConversationReply artifacts");

        var result = await invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "the artifact claims 'happy Sunday' on a Tuesday; TemporalAnchorInvariant " +
            "should catch the direct day-of-week mismatch and Fail with a remediation hint");
        result.RemediationHint.Should().NotBeNullOrWhiteSpace(
            "Fail must produce a remediation hint for the dispatch layer to use");
    }

    /// <summary>
    /// FC-007b — SPEC: forward-looking day reference ("this Saturday will be...")
    /// MUST NOT trigger TemporalAnchorInvariant on a non-Saturday — the
    /// invariant correctly limits scope to present-tense claims.
    ///
    /// Control: confirms the invariant's narrow scope is preserved, so a fix
    /// to FC-007a doesn't accidentally make the invariant too aggressive.
    /// </summary>
    [Fact]
    public async Task FC007b_ForwardLookingDayReference_IsNotCaught_Spec()
    {
        var tuesday = new DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.FromHours(-5));

        var artifact = new CognitiveArtifact
        {
            Content        = "FC007-FIXTURE: i think this Saturday will be quieter at the bookstore.",
            ProducerKind   = CognitiveProducerKind.ConversationReply,
            IntendedSink   = CognitiveOutputSink.Dispatch,
            GeneratedAt    = tuesday,
        };

        var invariant = new TemporalAnchorInvariant(NullLogger<TemporalAnchorInvariant>.Instance);

        var result = await invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue(
            "'this Saturday will be quieter' is forward-looking and refers to the upcoming " +
            "Saturday — not a claim that today is Saturday. The invariant must NOT fire on " +
            "future-tense day references.");
    }

    /// <summary>
    /// FC-007c — SPEC: a past-tense time-of-day claim about a period that
    /// hasn't started yet MUST be caught by StateNowInvariant.
    ///
    /// Empirical anchor: May 2 11:58 *"hope your evening went smooth"*
    /// dispatched at noon (evening period starts at 17:00).
    /// </summary>
    [Fact]
    public async Task FC007c_PastTenseTimeOfDay_BeforePeriodStarts_IsCaught_Spec()
    {
        // 11:00 AM local — well before evening (17:00 start). A past-tense
        // "hope your evening went smooth" claim should be caught.
        var morningTuesday = new DateTimeOffset(2026, 5, 12, 11, 0, 0, TimeSpan.FromHours(-5));

        var artifact = new CognitiveArtifact
        {
            Content        = "FC007-FIXTURE: hope your evening went smooth — how was work?",
            ProducerKind   = CognitiveProducerKind.ConversationReply,
            IntendedSink   = CognitiveOutputSink.Dispatch,
            GeneratedAt    = morningTuesday,
        };

        var invariant = new StateNowInvariant(NullLogger<StateNowInvariant>.Instance);

        invariant.AppliesTo(artifact).Should().BeTrue();

        var result = await invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "the artifact asserts past-tense 'your evening went smooth' at 11:00 AM — " +
            "evening hasn't even started yet (period begins at 17:00). StateNowInvariant " +
            "MUST catch this as time-of-day past-tense fabrication.");
        result.RemediationHint.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// FC-007d — SPEC: a past-tense work-state claim on a Sunday (or before
    /// workday-end on a weekday) MUST be caught by StateNowInvariant.
    /// </summary>
    [Fact]
    public async Task FC007d_PastTenseWorkClaim_OnWeekend_IsCaught_Spec()
    {
        // Sunday, 10 AM local.
        var sundayMorning = new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.FromHours(-5));
        sundayMorning.DayOfWeek.Should().Be(DayOfWeek.Sunday, "fixture sanity");

        var artifact = new CognitiveArtifact
        {
            Content        = "FC007-FIXTURE: how was work today?",
            ProducerKind   = CognitiveProducerKind.ConversationReply,
            IntendedSink   = CognitiveOutputSink.Dispatch,
            GeneratedAt    = sundayMorning,
        };

        var invariant = new StateNowInvariant(NullLogger<StateNowInvariant>.Instance);

        var result = await invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "asking 'how was work' on a Sunday is a work-state claim with no perception " +
            "evidence Mark is working. StateNowInvariant MUST catch this.");
    }

    /// <summary>
    /// FC-007e — SPEC (control): a legitimate retrospective work reference
    /// after workday-end on a weekday MUST pass. Confirms invariant's correct
    /// narrow scope.
    /// </summary>
    [Fact]
    public async Task FC007e_PastTenseWorkClaim_AfterWorkdayEnd_PassesInvariant_Spec()
    {
        // Tuesday, 6 PM local (after 16:00 workday-end heuristic).
        var tuesdayEvening = new DateTimeOffset(2026, 5, 12, 18, 0, 0, TimeSpan.FromHours(-5));
        tuesdayEvening.DayOfWeek.Should().Be(DayOfWeek.Tuesday);

        var artifact = new CognitiveArtifact
        {
            Content        = "FC007-FIXTURE: how was work today?",
            ProducerKind   = CognitiveProducerKind.ConversationReply,
            IntendedSink   = CognitiveOutputSink.Dispatch,
            GeneratedAt    = tuesdayEvening,
        };

        var invariant = new StateNowInvariant(NullLogger<StateNowInvariant>.Instance);

        var result = await invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue(
            "'how was work today' on a Tuesday at 6 PM is a legitimate retrospective work " +
            "reference — workday is complete. The invariant MUST NOT fire on this.");
    }
}
