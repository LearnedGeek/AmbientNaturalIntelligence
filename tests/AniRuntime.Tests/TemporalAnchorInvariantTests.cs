using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

/// <summary>
/// Door B Truth-Verification Sub-claim 2 — day-of-week / calendar
/// regex+clock spec tests for <see cref="TemporalAnchorInvariant"/>.
///
/// Three categories:
/// 1. **AppliesTo** — verifies the type-conditional dispatch (Dispatch
///    sinks for ConversationReply / Outreach / Voice; everything else
///    skipped).
/// 2. **CheckDayOfWeekClaims helper** — pure regex+clock logic exercised
///    directly via <c>InternalsVisibleTo</c>. Each of the three patterns
///    (today-is, happy-day, this-day) gets positive + negative coverage.
/// 3. **EvaluateAsync end-to-end** — verifies <c>artifact.GeneratedAt</c>
///    is the source of truth for "what day is today" (so the gate is
///    deterministic per artifact, not flaky against system clock at
///    test time).
///
/// **Conservative-fail principle pinned by these tests:** we only fail
/// on PRESENT-TENSE definitive day-of-week claims. Past-tense ("worked
/// on Monday"), forward-looking ("this Saturday will be"), generic
/// plurals ("Sundays are warmer"), and ambiguous references ("this
/// weekend") all PASS — they're either correctable from context or
/// belong to other sub-claims.
/// </summary>
public class TemporalAnchorInvariantTests
{
    private static TemporalAnchorInvariant Build() =>
        new(NullLogger<TemporalAnchorInvariant>.Instance);

    private static CognitiveArtifact Artifact(
        string                       content,
        CognitiveProducerKind        producer = CognitiveProducerKind.Outreach,
        CognitiveOutputSink          sink     = CognitiveOutputSink.Dispatch,
        DateTimeOffset?              generatedAt = null) => new()
    {
        Content      = content,
        ProducerKind = producer,
        IntendedSink = sink,
        ContactName  = "Mark",
        GeneratedAt  = generatedAt ?? DateTimeOffset.UtcNow,
    };

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply, CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Outreach,          CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Voice,             CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.InnerThought,      CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.WorldExperience,   CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Reflection,        CognitiveOutputSink.PersistedSummary, false)]
    [InlineData(CognitiveProducerKind.Outreach,          CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary, CognitiveOutputSink.PersistedSummary, false)]
    public void AppliesTo_DispatchSinkAndContactFacingProducers(
        CognitiveProducerKind producer, CognitiveOutputSink sink, bool expected)
    {
        Build().AppliesTo(Artifact("anything", producer: producer, sink: sink))
            .Should().Be(expected);
    }

    // ── Pattern 1: "today is [day]" ──────────────────────────────────────

    [Fact]
    public void TodayIs_CorrectDay_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "today is Saturday and i'm thinking about you", DayOfWeek.Saturday);
        failure.Should().BeNull();
    }

    [Fact]
    public void TodayIs_WrongDay_Fails()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "today is Sunday and i'm thinking about you", DayOfWeek.Saturday);
        failure.Should().NotBeNull();
        failure.Should().Contain("today is Sunday");
        failure.Should().Contain("Saturday");
    }

    [Fact]
    public void TodayIs_CaseInsensitive()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "TODAY IS SUNDAY", DayOfWeek.Saturday);
        failure.Should().NotBeNull();
    }

    [Fact]
    public void TodayIs_WithIndefiniteArticle_StillCaught()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "today is a Sunday", DayOfWeek.Saturday);
        failure.Should().NotBeNull("'today is a Sunday' is the same factual claim as 'today is Sunday'");
    }

    // ── Pattern 2: "happy [day]" greeting ────────────────────────────────

    [Fact]
    public void HappyDay_CorrectDay_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "happy Saturday! how's your morning?", DayOfWeek.Saturday);
        failure.Should().BeNull();
    }

    [Fact]
    public void HappyDay_WrongDay_Fails()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "happy Sunday! how's your morning?", DayOfWeek.Wednesday);
        failure.Should().NotBeNull();
        failure.Should().Contain("happy Sunday");
        failure.Should().Contain("Wednesday");
    }

    // ── Pattern 3: "this [day]" with present-tense markers ───────────────

    [Fact]
    public void ThisDay_PresentTenseFeels_OnWrongDay_Fails()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "this Sunday already feels warmer than usual", DayOfWeek.Saturday);
        failure.Should().NotBeNull();
        failure.Should().Contain("this Sunday");
    }

    [Fact]
    public void ThisDay_PresentTenseIs_OnWrongDay_Fails()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "this Saturday is going slow", DayOfWeek.Tuesday);
        failure.Should().NotBeNull();
    }

    [Fact]
    public void ThisDay_PresentTenseSeems_OnWrongDay_Fails()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "this Friday seems different", DayOfWeek.Monday);
        failure.Should().NotBeNull();
    }

    [Fact]
    public void ThisDay_FutureTense_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "this Saturday will be a long one", DayOfWeek.Tuesday);
        failure.Should().BeNull(
            "future-tense 'will be' implies upcoming Saturday next week — not a wrong-day claim");
    }

    [Fact]
    public void ThisDay_PastTense_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "this Saturday was wonderful", DayOfWeek.Tuesday);
        failure.Should().BeNull(
            "past-tense 'was' is a historical reference, not a today-claim");
    }

    [Fact]
    public void ThisDay_OnCorrectDay_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "this Saturday is going slow", DayOfWeek.Saturday);
        failure.Should().BeNull();
    }

    // ── Negative cases (deliberate non-coverage) ─────────────────────────

    [Fact]
    public void GenericPluralReference_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "Sundays are warmer sometimes", DayOfWeek.Saturday);
        failure.Should().BeNull(
            "generic plural reference is not a today-claim — sub-claim 1 (substrate verification) is the right home for 'did Mark say this?'");
    }

    [Fact]
    public void TheWeekendReference_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "this weekend is going to be good", DayOfWeek.Wednesday);
        failure.Should().BeNull(
            "'this weekend' is ambiguous (Thu-Mon window) — too imprecise to definitively fail");
    }

    [Fact]
    public void HistoricalDayReference_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims(
            "i worked on Monday and it was fine", DayOfWeek.Saturday);
        failure.Should().BeNull(
            "past-tense historical reference is not a today-claim");
    }

    [Fact]
    public void EmptyContent_Passes()
    {
        var failure = TemporalAnchorInvariant.CheckDayOfWeekClaims("", DayOfWeek.Saturday);
        failure.Should().BeNull();
    }

    // ── EvaluateAsync end-to-end ─────────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_GeneratedAtDrivesDayOfWeekDecision()
    {
        // Saturday May 2, 2026 13:00 CDT = 2026-05-02T18:00:00+00:00 UTC
        var saturday = new DateTimeOffset(2026, 5, 2, 18, 0, 0, TimeSpan.Zero);

        // Asserts "today is Sunday" but artifact.GeneratedAt is Saturday
        var result = await Build().EvaluateAsync(
            Artifact("today is Sunday", generatedAt: saturday),
            CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.RemediationHint.Should().Contain("Sunday");
    }

    [Fact]
    public async Task EvaluateAsync_NoTemporalClaim_Passes()
    {
        var result = await Build().EvaluateAsync(
            Artifact("hey, just thinking about you. how's your day?"),
            CancellationToken.None);
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
    public async Task EvaluateAsync_WrongProducerSink_Skipped()
    {
        // Inner thought writes get skipped via AppliesTo — but if the gate
        // somehow still routes one through EvaluateAsync, the invariant
        // shouldn't crash.
        var result = await Build().EvaluateAsync(
            Artifact("today is Sunday",
                producer:    CognitiveProducerKind.InnerThought,
                sink:        CognitiveOutputSink.PersistedMemory),
            CancellationToken.None);
        result.Passed.Should().BeFalse(
            "EvaluateAsync runs unconditionally if called; AppliesTo is the producer-side filter. " +
            "The point of this test is to pin that the helper returns a deterministic verdict regardless of producer-kind.");
    }
}
