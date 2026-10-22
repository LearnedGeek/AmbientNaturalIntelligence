using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 2 — pins the pure-static selector + the composer injection
/// contract. The selector itself has no I/O and no state; this exercises
/// the composition rules (which lists feed the pool, Mark-facing lists
/// excluded) and the deterministic rotation.
/// </summary>
public class NamedSubstrateAnchorTests
{
    private static CharacterStateDoc State(
        IEnumerable<string>? familyContext = null,
        IEnumerable<string>? interests = null,
        IEnumerable<string>? sharedExperiences = null,
        IEnumerable<string>? selfConcept = null,
        IEnumerable<string>? thingsContactCares = null,
        IEnumerable<string>? learnedAboutContact = null) => new()
    {
        FamilyContext        = familyContext?.ToList() ?? new List<string>(),
        Interests            = interests?.ToList() ?? new List<string>(),
        SharedExperiences    = sharedExperiences?.ToList() ?? new List<string>(),
        SelfConcept          = selfConcept?.ToList() ?? new List<string>(),
        ThingsContactCares   = thingsContactCares?.ToList() ?? new List<string>(),
        LearnedAboutContact  = learnedAboutContact?.ToList() ?? new List<string>(),
    };

    [Fact]
    public void Select_EmptyPool_ReturnsNull()
    {
        var cs = new CharacterStateDoc();  // all lists empty
        NamedSubstrateAnchor.Select(cs, rotationIndex: 0).Should().BeNull();
    }

    [Fact]
    public void Select_SinglePoolEntry_ReturnsThatEntryForAllRotations()
    {
        var cs = State(familyContext: new[] { "Sister Lila" });
        NamedSubstrateAnchor.Select(cs, 0).Should().Be("Sister Lila");
        NamedSubstrateAnchor.Select(cs, 1).Should().Be("Sister Lila");
        NamedSubstrateAnchor.Select(cs, 999).Should().Be("Sister Lila");
    }

    [Fact]
    public void Select_RotatesDeterministicallyByIndex()
    {
        var cs = State(
            familyContext: new[] { "A" },
            interests: new[] { "B" },
            sharedExperiences: new[] { "C" });
        // Pool order: family then interests then sharedExperiences
        //            = [A, B, C]
        NamedSubstrateAnchor.Select(cs, 0).Should().Be("A");
        NamedSubstrateAnchor.Select(cs, 1).Should().Be("B");
        NamedSubstrateAnchor.Select(cs, 2).Should().Be("C");
        NamedSubstrateAnchor.Select(cs, 3).Should().Be("A", "rotation wraps");
        NamedSubstrateAnchor.Select(cs, 4).Should().Be("B");
    }

    [Fact]
    public void BuildPool_ExcludesMarkFacingLists()
    {
        // F-4 Phase 2 design claim: Mark-facing lists (ThingsContactCares,
        // LearnedAboutContact) are excluded from the pool because the point
        // is HER life, not more Mark-facts in her head. Those lists already
        // surface via anchored memories + retrieval.
        var cs = State(
            familyContext:        new[] { "Lila" },
            interests:            new[] { "Fleetwood Mac" },
            sharedExperiences:    new[] { "Duck Norris" },
            selfConcept:          new[] { "dark auburn hair" },
            thingsContactCares:   new[] { "Mia", "Mark's Jeep", "teaching at WCTC" },
            learnedAboutContact:  new[] { "Mark wakes at 4 AM", "Mark cries over code" });

        var pool = NamedSubstrateAnchor.BuildPool(cs);

        pool.Should().Contain("Lila");
        pool.Should().Contain("Fleetwood Mac");
        pool.Should().Contain("Duck Norris");
        pool.Should().Contain("dark auburn hair");
        pool.Should().NotContain("Mia");
        pool.Should().NotContain("Mark's Jeep");
        pool.Should().NotContain("teaching at WCTC");
        pool.Should().NotContain("Mark wakes at 4 AM");
        pool.Should().NotContain("Mark cries over code");
    }

    [Fact]
    public void BuildPool_FiltersWhitespaceAndEmpty()
    {
        var cs = State(
            familyContext: new[] { "", "   ", "Lila" },
            interests:     new[] { "Fleetwood Mac", "\t" });

        var pool = NamedSubstrateAnchor.BuildPool(cs);

        pool.Should().BeEquivalentTo(new[] { "Lila", "Fleetwood Mac" });
    }

    [Fact]
    public void Select_NegativeRotationIndex_WrapsPositive()
    {
        // Defensive — BuiltAt.Ticks is positive in production, but a bad
        // test fixture or clock skew could pass negative. Pure positive
        // modulo keeps the selector safe.
        var cs = State(interests: new[] { "A", "B", "C" });
        NamedSubstrateAnchor.Select(cs, -1).Should().Be("C");
        NamedSubstrateAnchor.Select(cs, -4).Should().Be("C", "-4 mod 3 wraps to index 2");
    }

    [Fact]
    public void Select_NullCharacterState_ReturnsNull()
    {
        NamedSubstrateAnchor.Select(null!, 0).Should().BeNull();
    }

    // ── Composer injection contract ───────────────────────────────────────

    private static ContextSnapshot SnapshotWithAnchorPool() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm"],
            FamilyContext      = new List<string>
            {
                "Sister Lila — my 'code-sis,' texts at midnight for birthdays with robot emojis"
            },
            Interests          = new List<string>
            {
                "Fleetwood Mac Landslide live version — my secret joy"
            },
        },
        DesireState           = new DesireState(),
        EmotionalState        = new EmotionalState(),
        RecentMemory          = [],
        RelevantMemory        = [],
        OpenLoops             = [],
        Perceptions           = [],
        RecentHistory         = [],
        SimilarRecentThoughts = [],
        BuiltAt               = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void BuildInnerThoughtPrompt_FlagOff_NoAnchorInjected()
    {
        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWithAnchorPool(),
            namedSubstrateAnchorEnabled: false);

        user.Should().NotContain("Something specific from your life");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_FlagOn_InjectsAnchorFromPool()
    {
        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWithAnchorPool(),
            namedSubstrateAnchorEnabled: true);

        user.Should().Contain("Something specific from your life");
        // Either anchor could be picked depending on BuiltAt rotation
        var hasLila = user.Contains("Lila");
        var hasFleetwood = user.Contains("Fleetwood Mac");
        hasLila.Should().BeTrue("one of the two pool entries should be the injected anchor (Lila expected at this timestamp)");
        hasFleetwood.Should().BeFalse("the fixture timestamp selects Lila deterministically; verify with the raw selector too");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_FlagOn_EmptyPool_SkipsSection()
    {
        var snapshot = SnapshotWithAnchorPool();
        snapshot.CharacterState.FamilyContext = new List<string>();
        snapshot.CharacterState.Interests = new List<string>();

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            snapshot, namedSubstrateAnchorEnabled: true);

        user.Should().NotContain("Something specific from your life");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_SoftFraming_Preserved()
    {
        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWithAnchorPool(),
            namedSubstrateAnchorEnabled: true);

        user.Should().Contain("optional, not required");
    }
}
