using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 6 — pins the reader half of the person-trope writer loop.
/// <see cref="PromptBuilder.BuildInnerThoughtPrompt"/> renders the top-K
/// most-recently-touched <see cref="PersonState"/> slots as a sibling
/// of the Phase 7 OngoingArcs block. See issue #185.
/// </summary>
public class KnownPeopleInnerThoughtInjectionTests
{
    private static readonly DateTimeOffset BuiltAt =
        new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);

    private static ContextSnapshot Snapshot(CharacterStateDoc cs) => new()
    {
        CharacterState        = cs,
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

    private static CharacterStateDoc BuildStateWithPeople(params (string name, string trope, DateTimeOffset touched)[] people)
    {
        var cs = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = new List<string> { "warm" },
        };
        foreach (var (name, trope, touched) in people)
        {
            cs.KnownPeople[name] = new PersonState
            {
                Name          = name,
                LastTouchedAt = touched,
                SharedTropes  = new List<string> { trope },
            };
        }
        return cs;
    }

    [Fact]
    public void TopKNull_NoSection()
    {
        var cs = BuildStateWithPeople(("Lila", "classroom patience", BuiltAt.AddHours(-1)));

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(cs), knownPeopleTopK: null);

        user.Should().NotContain("People on your mind recently");
    }

    [Fact]
    public void TopKZero_NoSection()
    {
        var cs = BuildStateWithPeople(("Lila", "classroom patience", BuiltAt.AddHours(-1)));

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(cs), knownPeopleTopK: 0);

        user.Should().NotContain("People on your mind recently");
    }

    [Fact]
    public void EmptyKnownPeople_NoSection()
    {
        var cs = new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark", CoreTraits = new List<string> { "warm" } };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(cs), knownPeopleTopK: 3);

        user.Should().NotContain("People on your mind recently");
    }

    [Fact]
    public void PopulatedSlots_RendersSoftFramedSection()
    {
        var cs = BuildStateWithPeople(
            ("Lila",    "classroom patience", BuiltAt.AddHours(-1)),
            ("Dominus", "comforting",         BuiltAt.AddHours(-3)));

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(cs), knownPeopleTopK: 3);

        user.Should().Contain("People on your mind recently");
        user.Should().Contain("don't force it");
        user.Should().Contain("Lila");
        user.Should().Contain("classroom patience");
        user.Should().Contain("Dominus");
        user.Should().Contain("comforting");
    }

    [Fact]
    public void TopK_OrdersByLastTouchedDescending()
    {
        var cs = BuildStateWithPeople(
            ("Lila",    "oldest",   BuiltAt.AddDays(-10)),
            ("Dominus", "middle",   BuiltAt.AddHours(-6)),
            ("Sarah",   "newest",   BuiltAt.AddHours(-1)));

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(cs), knownPeopleTopK: 2);

        // TopK=2 keeps Sarah + Dominus (newest two), drops Lila (oldest).
        user.Should().Contain("Sarah");
        user.Should().Contain("Dominus");
        user.Should().NotContain("oldest");
    }

    [Fact]
    public void SlotsWithEmptyTropes_SkippedFromRender()
    {
        var cs = BuildStateWithPeople(("Lila", "classroom patience", BuiltAt.AddHours(-1)));
        // Pathological: a slot with no tropes shouldn't render.
        cs.KnownPeople["Dominus"] = new PersonState
        {
            Name          = "Dominus",
            LastTouchedAt = BuiltAt,  // most recent
            SharedTropes  = new List<string>(),
        };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(cs), knownPeopleTopK: 3);

        user.Should().Contain("Lila");
        user.Should().NotContain("Dominus");
    }

    [Fact]
    public void MultipleTropesPerPerson_RendersUpToThreeJoined()
    {
        var cs = BuildStateWithPeople(("Lila", "ignored-by-helper", BuiltAt.AddHours(-1)));
        cs.KnownPeople["Lila"].SharedTropes = new List<string>
        {
            "classroom patience",
            "late-night texts",
            "robot emojis on birthdays",
            "FOURTH SHOULD BE TRIMMED",
        };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            Snapshot(cs), knownPeopleTopK: 1);

        user.Should().Contain("classroom patience");
        user.Should().Contain("late-night texts");
        user.Should().Contain("robot emojis on birthdays");
        user.Should().NotContain("FOURTH SHOULD BE TRIMMED");
    }
}
