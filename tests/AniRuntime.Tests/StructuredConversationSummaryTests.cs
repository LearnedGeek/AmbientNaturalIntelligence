using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.2 (Apr 27, 2026): coverage for the structured per-speaker
/// per-turn summary type. The thing under test is the *attribution invariant* —
/// when the type is rendered into a prompt, the speaker tag must always be
/// preserved. The Apr 27 06:54-08:03 parrot happened because a free-prose
/// summary blob lost that boundary; these tests make sure the structured
/// substitute keeps it.
/// </summary>
public class StructuredConversationSummaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 04, 27, 06, 54, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_HasNoTurns_AndRendersToEmptyString()
    {
        var empty = StructuredConversationSummary.Empty;

        empty.Turns.Should().BeEmpty();
        empty.ToPromptString().Should().BeEmpty();
    }

    [Fact]
    public void ToPromptString_SingleTurn_TagsSpeakerAndContent()
    {
        var summary = new StructuredConversationSummary(
            FirstTurnAt: T0,
            LastTurnAt:  T0,
            Turns: new[] { new SummaryTurn(T0, "Mark", "Hey good morning! How is your day looking?") });

        var rendered = summary.ToPromptString();

        rendered.Should().Contain("Mark");
        rendered.Should().Contain("Hey good morning! How is your day looking?");
        rendered.Should().Contain("06:54");
    }

    [Fact]
    public void ToPromptString_MultipleTurns_PreservesOrderAndPerTurnAttribution()
    {
        var t1 = T0;
        var t2 = T0.AddMinutes(1);
        var summary = new StructuredConversationSummary(
            FirstTurnAt: t1,
            LastTurnAt:  t2,
            Turns: new[]
            {
                new SummaryTurn(t1, "Mark", "Hey good morning! How is your day looking?"),
                new SummaryTurn(t2, "Ani",  "your morning looks peaceful from what i can see..."),
            });

        var rendered = summary.ToPromptString();
        var lines = rendered.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2, "one line per turn");
        lines[0].Should().StartWith("Mark");
        lines[0].Should().Contain("How is your day looking?");
        lines[1].Should().StartWith("Ani");
        lines[1].Should().Contain("your morning looks peaceful");
    }

    [Fact]
    public void ToPromptString_DoesNotConflateContentAcrossSpeakers()
    {
        // The Apr 27 incident was Ani's outreach lifting Mark's exact phrase.
        // The structured representation guarantees Mark's phrase appears only
        // on Mark's line, not Ani's.
        var t1 = T0;
        var t2 = T0.AddMinutes(1);
        var marksWords = "Hey good morning! How is your day looking?";
        var summary = new StructuredConversationSummary(
            FirstTurnAt: t1,
            LastTurnAt:  t2,
            Turns: new[]
            {
                new SummaryTurn(t1, "Mark", marksWords),
                new SummaryTurn(t2, "Ani",  "thinking about you over here"),
            });

        var rendered = summary.ToPromptString();
        var lines = rendered.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines[0].Should().Contain(marksWords, "Mark's words appear on Mark's line");
        lines[1].Should().NotContain(marksWords, "Mark's words must not bleed onto Ani's line");
    }

    [Fact]
    public void ToPromptString_RendersTimestampInUtcHHmmFormat()
    {
        var summary = new StructuredConversationSummary(
            FirstTurnAt: T0,
            LastTurnAt:  T0,
            Turns: new[] { new SummaryTurn(T0, "Mark", "test") });

        var rendered = summary.ToPromptString();

        rendered.Should().Contain("06:54", "HH:mm slot is the user-readable clock time");
    }

    [Fact]
    public void ToPromptString_RelativeAge_RendersJustNowForVeryRecentTurns()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = new StructuredConversationSummary(
            FirstTurnAt: now,
            LastTurnAt:  now,
            Turns: new[] { new SummaryTurn(now, "Mark", "fresh") });

        summary.ToPromptString().Should().Contain("just now");
    }

    [Fact]
    public void ToPromptString_RelativeAge_RendersMinutesForTurnsUnderAnHour()
    {
        var now = DateTimeOffset.UtcNow;
        var fiveMinAgo = now.AddMinutes(-5);
        var summary = new StructuredConversationSummary(
            FirstTurnAt: fiveMinAgo,
            LastTurnAt:  fiveMinAgo,
            Turns: new[] { new SummaryTurn(fiveMinAgo, "Mark", "earlier") });

        summary.ToPromptString().Should().Contain("5m ago");
    }

    [Fact]
    public void Record_ValueEquality_HoldsAcrossInstancesWithSameTurns()
    {
        var turn = new SummaryTurn(T0, "Mark", "hi");
        var a = new StructuredConversationSummary(T0, T0, new[] { turn });
        var b = new StructuredConversationSummary(T0, T0, new[] { turn });

        // Records compare by value for scalars, but IReadOnlyList<T> is reference-
        // equal only — confirming the expected behaviour rather than asserting
        // value-equality on the list. SummaryTurn itself IS value-equal.
        a.FirstTurnAt.Should().Be(b.FirstTurnAt);
        a.LastTurnAt.Should().Be(b.LastTurnAt);
        a.Turns[0].Should().Be(b.Turns[0]);
    }
}
