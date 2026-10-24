using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.LLM.Prompts;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 5a (2026-10-24) — pins the reader half of the temporal
/// grounding loop. <see cref="PromptBuilder.BuildInnerThoughtPrompt"/>
/// renders a <see cref="TemporalContext"/> as an explicit "prior cycles,
/// not now" section and extends the system-prompt time line with an
/// elapsed-since-last-thought phrase.
///
/// Closes issue #184. Architectural precedent: Park et al 2023 §4.1
/// (memory-stream records carry creation + last-access timestamps and
/// the reflection prompt feeds them in natural language so the agent
/// reasons over the temporal sequence rather than confabulating).
/// </summary>
public class InnerThoughtTemporalContextInjectionTests
{
    private static readonly DateTimeOffset BuiltAt =
        new(2026, 10, 24, 18, 0, 0, TimeSpan.Zero);

    private static ContextSnapshot Snapshot() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm"],
        },
        DesireState           = new DesireState(),
        EmotionalState        = new EmotionalState(),
        RecentMemory          = [],
        RelevantMemory        = [],
        OpenLoops             = [],
        Perceptions           = [],
        RecentHistory         = [],
        SimilarRecentThoughts = [],
        BuiltAt               = BuiltAt,
    };

    [Fact]
    public void BuildInnerThoughtPrompt_TemporalNull_NoTemporalSection()
    {
        var (system, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(), temporal: null);

        user.Should().NotContain("Thoughts you've had recently");
        system.Should().NotContain("Your last inner thought was");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_TemporalEmpty_NoTemporalSection()
    {
        var empty = new TemporalContext(null, Array.Empty<RecentInnerThought>());

        var (system, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(), temporal: empty);

        user.Should().NotContain("Thoughts you've had recently");
        system.Should().NotContain("Your last inner thought was");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_ElapsedOnly_AddsSystemLineButNoUserSection()
    {
        // Edge case: provider computed hours but RecentInnerThoughts list
        // was filtered to empty (e.g. all whitespace content). System line
        // still fires because the elapsed value stands on its own.
        var elapsedOnly = new TemporalContext(3.0, Array.Empty<RecentInnerThought>());

        var (system, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(), temporal: elapsedOnly);

        system.Should().Contain("Your last inner thought was");
        user.Should().NotContain("Thoughts you've had recently");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_RecentInnerThoughts_RendersSectionAndElapsed()
    {
        var recent = new List<RecentInnerThought>
        {
            new("I think about how light moves through glass.",  BuiltAt.AddHours(-3)),
            new("There's a corner of the mystery section.",      BuiltAt.AddHours(-22)),
        };
        var ctx = new TemporalContext(3.0, recent);

        var (system, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(), temporal: ctx);

        system.Should().Contain("Your last inner thought was 3 hours ago.");
        user.Should().Contain("Thoughts you've had recently");
        user.Should().Contain("prior cycles, not now");
        user.Should().Contain("3 hours ago");
        user.Should().Contain("I think about how light moves through glass.");
        user.Should().Contain("yesterday");  // 22h → "yesterday" bucket (stands alone)
        user.Should().Contain("mystery section");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_ElapsedPhrase_UsesCoarseBuckets()
    {
        // Pins FormatElapsedPhrase's bucket boundaries. Readable phrases
        // ("yesterday", "last week") beat raw clock numbers because they
        // read as remembered-subjective rather than externally-reported.
        var cases = new (double hours, string expected)[]
        {
            (0.5,   "less than an hour ago"),
            (1.0,   "about an hour ago"),
            (3.0,   "3 hours ago"),
            (22.0,  "yesterday"),
            (48.0,  "2 days ago"),
            (24*8,  "last week"),
        };

        foreach (var (hours, expected) in cases)
        {
            InnerThoughtPromptCommand.FormatElapsedPhrase(hours)
                .Should().Be(expected, $"elapsed {hours}h should bucket to '{expected}'");
        }
    }

    [Fact]
    public void BuildInnerThoughtPrompt_LongContent_Truncated()
    {
        // Guard against pathological-length prior thoughts dominating the
        // inner-thought composer context window. Truncation preserves the
        // grounded-reference property without blowing out token budget.
        var longText = new string('x', 500);
        var ctx = new TemporalContext(1.0, new List<RecentInnerThought>
        {
            new(longText, BuiltAt.AddHours(-1)),
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(), temporal: ctx);

        user.Should().Contain("...");
        // 160 chars + ellipsis + quote + prefix — bounded, not 500 chars raw.
        user.Length.Should().BeLessThan(2000);
    }

    [Fact]
    public void BuildInnerThoughtPrompt_WhitespaceContentFiltered()
    {
        var ctx = new TemporalContext(1.0, new List<RecentInnerThought>
        {
            new("real one",   BuiltAt.AddHours(-1)),
            new("   ",        BuiltAt.AddHours(-2)),
            new(string.Empty, BuiltAt.AddHours(-3)),
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(), temporal: ctx);

        user.Should().Contain("real one");
        user.Should().NotContain("\"\"",  "empty content should not render as empty-quote entry");
    }
}
