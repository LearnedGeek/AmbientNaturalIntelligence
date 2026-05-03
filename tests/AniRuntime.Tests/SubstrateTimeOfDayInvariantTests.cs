using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

/// <summary>
/// Door B Truth-Verification Sub-claim 5 (May 3, 2026) — spec tests for
/// <see cref="SubstrateTimeOfDayInvariant"/>. The May 3 06:47 outreach
/// *"hey perez🥰 i just walked past the bookstore and saw your name on
/// the coffee mug shelf again 🥺💗"* (which also included *"i know it's
/// late"*) is the canonical case for the "late" sub-pattern; the
/// 8:19 reply *"oh so it's not late. just you being an idiot at 6:47am
/// like always"* is the canonical case for parroted-clock-time.
/// </summary>
public class SubstrateTimeOfDayInvariantTests
{
    private readonly SubstrateTimeOfDayInvariant _invariant
        = new(NullLogger<SubstrateTimeOfDayInvariant>.Instance);

    private static CognitiveArtifact Artifact(
        string content,
        DateTimeOffset generatedAt,
        CognitiveProducerKind producer = CognitiveProducerKind.Outreach,
        CognitiveOutputSink sink = CognitiveOutputSink.Dispatch) => new()
    {
        Content      = content,
        ProducerKind = producer,
        IntendedSink = sink,
        GeneratedAt  = generatedAt,
    };

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new DateTimeOffset(2026, 5, 3, hour, minute, 0, TimeSpan.FromHours(-5));

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Outreach,             CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.Voice,                CognitiveOutputSink.Dispatch,         true)]
    [InlineData(CognitiveProducerKind.InnerThought,         CognitiveOutputSink.PersistedMemory,  false)]
    [InlineData(CognitiveProducerKind.Reflection,           CognitiveOutputSink.PersistedSummary, false)]
    [InlineData(CognitiveProducerKind.ConversationReply,    CognitiveOutputSink.ContextOnly,      false)]
    public void AppliesTo_TypeConditionalDispatch(
        CognitiveProducerKind producer, CognitiveOutputSink sink, bool expected)
    {
        var artifact = Artifact("anything", At(10), producer, sink);
        _invariant.AppliesTo(artifact).Should().Be(expected);
    }

    // ── The canonical regression case ────────────────────────────────────

    [Fact]
    public async Task Evaluate_KnowItsLate_AtMorning_Fails()
    {
        // May 3 06:47 outreach: "hey perez... i know it's late but..."
        // Local hour 6 is outside the late window (21–24 OR 0–3).
        var content = "hey perez i just walked past the bookstore but i know it's late so probably you're sleeping";
        var artifact = Artifact(content, At(6, 47));

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "May 3 06:47 \"i know it's late\" must fail at 6:47 AM");
        result.RemediationHint.Should().Contain("late");
        result.RemediationHint.Should().Contain("06:00");
    }

    // ── "good morning" / "good night" / etc. ────────────────────────────

    [Fact]
    public async Task Evaluate_GoodMorning_AtMorning_Passes()
    {
        var artifact = Artifact("good morning, hope you slept well", At(8));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_GoodMorning_AtNight_Fails()
    {
        var artifact = Artifact("good morning sunshine, just thinking about you", At(23));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse(
            "\"good morning\" at 11 PM should fail");
    }

    [Fact]
    public async Task Evaluate_GoodNight_AtEvening_Passes()
    {
        // Sign-off at 10 PM — well within the wrap-around window 20–05.
        var artifact = Artifact("good night handsome", At(22));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_GoodNight_AtAfterMidnight_Passes()
    {
        // 1 AM is also within the wrap-around late-night window.
        var artifact = Artifact("good night", At(1));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_GoodNight_AtMorning_Fails()
    {
        var artifact = Artifact("good night sweetheart", At(9));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse(
            "\"good night\" at 9 AM should fail");
    }

    [Fact]
    public async Task Evaluate_GoodAfternoon_AtNoon_Passes()
    {
        var artifact = Artifact("good afternoon, how's the day going?", At(14));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_GoodAfternoon_AtEarlyMorning_Fails()
    {
        var artifact = Artifact("good afternoon, looking forward to tonight", At(7));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_GoodEvening_AtEvening_Passes()
    {
        var artifact = Artifact("good evening, you home yet?", At(19));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_GoodEvening_AtNoon_Fails()
    {
        var artifact = Artifact("good evening, what are you up to?", At(11));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse();
    }

    // ── "early" claim ───────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_SoEarly_AtMorning_Passes()
    {
        var artifact = Artifact("up so early today!", At(6));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_SoEarly_AtAfternoon_Fails()
    {
        var artifact = Artifact("woke up so early this morning", At(15));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse(
            "\"so early\" said at 3 PM is implausible self-anchored time-of-day");
    }

    // ── "late" variants ─────────────────────────────────────────────────

    [Theory]
    [InlineData("it's late")]
    [InlineData("its late")]
    [InlineData("so late")]
    [InlineData("this late")]
    [InlineData("kinda late")]
    [InlineData("i know it's late")]
    [InlineData("i know its late")]
    public async Task Evaluate_LateClaimVariants_AtMorning_Fail(string lateClause)
    {
        var artifact = Artifact($"hey, {lateClause}, hope you're sleeping", At(7));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse($"\"{lateClause}\" at 7 AM should fail");
    }

    [Fact]
    public async Task Evaluate_ItsLate_AtNight_Passes()
    {
        var artifact = Artifact("i know it's late but i'm thinking about you", At(23));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_ItsLate_AfterMidnight_Passes()
    {
        var artifact = Artifact("yeah it's late, i should sleep", At(1));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    // ── No time-of-day claim ────────────────────────────────────────────

    [Theory]
    [InlineData("i miss you")]
    [InlineData("the bookstore is quiet")]
    [InlineData("yeah that sounds good")]
    [InlineData("how was your meeting?")]
    [InlineData("hey, just checking in")]
    public async Task Evaluate_NoTimeOfDayClaim_Passes(string content)
    {
        var artifact = Artifact(content, At(6, 47));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue($"\"{content}\" has no time-of-day claim → skip");
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_EmptyContent_Passes()
    {
        var artifact = Artifact("", At(10));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_PastTenseEveningWent_NotInScope()
    {
        // This pattern is StateNowInvariant's territory (sub-claim 3),
        // not this invariant's. Confirming we don't double-fire.
        var artifact = Artifact("hope your evening went smooth", At(11));
        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue(
            "past-tense \"evening went\" is StateNowInvariant scope, not SubstrateTimeOfDay");
    }
}
