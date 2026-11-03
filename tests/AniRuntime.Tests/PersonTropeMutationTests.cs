using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 6 — pins the pure-mutation half of the person-trope writer
/// path: <see cref="ReflectionPhase.ApplyPersonTropes"/> and its
/// companion <see cref="ReflectionPhase.IsCanonicalPersonName"/>. These
/// tests encode the confabulation discipline (allowlist defense, tropes-
/// not-events, bounded tropes-per-person, primary-contact exclusion)
/// without an LLM hop. See issue #185.
/// </summary>
public class PersonTropeMutationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);

    private static CharacterStateDoc BuildState() => new()
    {
        Name               = "Ani",
        PrimaryContactName = "Mark",
        FamilyContext = new List<string>
        {
            "Sister Lila — my 'code-sis,' texts at midnight for birthdays with robot emojis",
            "Mark is my family now — the only person who makes me feel like I belong somewhere",
            "Dominus — my dog. He barks at horror movies and steals socks",
        },
        CanonicalContacts = new List<string> { "Sarah", "Kevin" },
    };

    // -------- IsCanonicalPersonName --------

    [Fact]
    public void IsCanonical_FamilyContextSubstring_Accepted()
    {
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("Lila",    cs).Should().BeTrue();
        ReflectionPhase.IsCanonicalPersonName("Dominus", cs).Should().BeTrue();
    }

    [Fact]
    public void IsCanonical_CanonicalContactsExactMatch_Accepted()
    {
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("Sarah", cs).Should().BeTrue();
        ReflectionPhase.IsCanonicalPersonName("Kevin", cs).Should().BeTrue();
    }

    [Fact]
    public void IsCanonical_CaseInsensitive()
    {
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("lila",  cs).Should().BeTrue();
        ReflectionPhase.IsCanonicalPersonName("SARAH", cs).Should().BeTrue();
    }

    [Fact]
    public void IsCanonical_PrimaryContact_Rejected()
    {
        // Issue #185 explicitly: Mark is out of scope for Phase 6.
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("Mark", cs).Should().BeFalse();
        ReflectionPhase.IsCanonicalPersonName("mark", cs).Should().BeFalse();
    }

    [Fact]
    public void IsCanonical_FabricatedName_Rejected()
    {
        // The "hey perez" defense pattern (May 3 2026) — a name not in
        // canonical substrate must never land on a canonical slot.
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("Perez",  cs).Should().BeFalse();
        ReflectionPhase.IsCanonicalPersonName("Rachel", cs).Should().BeFalse();
    }

    [Fact]
    public void IsCanonical_WhitespaceOrTooShort_Rejected()
    {
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("",   cs).Should().BeFalse();
        ReflectionPhase.IsCanonicalPersonName("  ", cs).Should().BeFalse();
        ReflectionPhase.IsCanonicalPersonName("A",  cs).Should().BeFalse();
    }

    // -------- ApplyPersonTropes --------

    [Fact]
    public void Apply_NewSlot_Created()
    {
        var cs = BuildState();
        var tropes = new List<PersonTrope>
        {
            new("Lila", "classroom patience"),
        };

        var changed = ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 5, now: Now);

        changed.Should().BeTrue();
        cs.KnownPeople.Should().ContainKey("Lila");
        cs.KnownPeople["Lila"].SharedTropes.Should().ContainSingle().And.Contain("classroom patience");
        cs.KnownPeople["Lila"].FirstMentionedAt.Should().Be(Now);
        cs.KnownPeople["Lila"].LastTouchedAt.Should().Be(Now);
        cs.KnownPeople["Lila"].MentionCount.Should().Be(1);
    }

    [Fact]
    public void Apply_ExistingSlot_TropePrependedAndCountBumped()
    {
        var cs = BuildState();
        cs.KnownPeople["Lila"] = new PersonState
        {
            Name             = "Lila",
            FirstMentionedAt = Now.AddDays(-7),
            LastTouchedAt    = Now.AddDays(-1),
            MentionCount     = 2,
            SharedTropes     = new List<string> { "late-night texts" },
        };

        var tropes = new List<PersonTrope>
        {
            new("Lila", "classroom patience"),
        };

        ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 5, now: Now);

        cs.KnownPeople["Lila"].SharedTropes
            .Should().BeEquivalentTo(new[] { "classroom patience", "late-night texts" },
                opts => opts.WithStrictOrdering());
        cs.KnownPeople["Lila"].LastTouchedAt.Should().Be(Now);
        cs.KnownPeople["Lila"].MentionCount.Should().Be(3);
        cs.KnownPeople["Lila"].FirstMentionedAt.Should().Be(Now.AddDays(-7));
    }

    [Fact]
    public void Apply_DuplicateTrope_MovesToFrontNotAppended()
    {
        var cs = BuildState();
        cs.KnownPeople["Dominus"] = new PersonState
        {
            Name          = "Dominus",
            LastTouchedAt = Now.AddDays(-1),
            SharedTropes  = new List<string> { "steals socks", "barks at thunder" },
        };

        var tropes = new List<PersonTrope>
        {
            new("Dominus", "steals socks"), // duplicate (case-sensitive match)
        };

        ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 5, now: Now);

        cs.KnownPeople["Dominus"].SharedTropes
            .Should().BeEquivalentTo(new[] { "steals socks", "barks at thunder" },
                opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void Apply_BoundedByMaxTropesPerPerson()
    {
        var cs = BuildState();
        var tropes = new List<PersonTrope>
        {
            new("Lila", "t1"), new("Lila", "t2"), new("Lila", "t3"),
            new("Lila", "t4"), new("Lila", "t5"), new("Lila", "t6"),
        };

        ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 3, now: Now);

        cs.KnownPeople["Lila"].SharedTropes.Should().HaveCount(3);
        // Most-recent first: t6 was applied last.
        cs.KnownPeople["Lila"].SharedTropes[0].Should().Be("t6");
    }

    [Fact]
    public void Apply_FabricatedName_Rejected()
    {
        var cs = BuildState();
        var tropes = new List<PersonTrope>
        {
            new("Perez",  "random pattern"),
            new("Rachel", "another pattern"),
        };

        var changed = ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 5, now: Now);

        changed.Should().BeFalse();
        cs.KnownPeople.Should().BeEmpty();
    }

    [Fact]
    public void Apply_PrimaryContact_Rejected()
    {
        var cs = BuildState();
        var tropes = new List<PersonTrope>
        {
            new("Mark", "warm when he asks about my day"),
        };

        var changed = ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 5, now: Now);

        changed.Should().BeFalse();
        cs.KnownPeople.Should().BeEmpty();
    }

    [Fact]
    public void Apply_MixedBatch_OnlyCanonicalAccrue()
    {
        var cs = BuildState();
        var tropes = new List<PersonTrope>
        {
            new("Lila",   "classroom patience"), // canonical
            new("Mark",   "attentive"),           // primary-contact: reject
            new("Perez",  "fabricated"),           // not in cast: reject
            new("Dominus","comforting"),           // canonical
        };

        var changed = ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 5, now: Now);

        changed.Should().BeTrue();
        cs.KnownPeople.Should().HaveCount(2);
        cs.KnownPeople.Should().ContainKey("Lila");
        cs.KnownPeople.Should().ContainKey("Dominus");
        cs.KnownPeople.Should().NotContainKey("Mark");
        cs.KnownPeople.Should().NotContainKey("Perez");
    }

    [Fact]
    public void Apply_CasingVariant_RoutesToExistingSlotNoDuplicate()
    {
        var cs = BuildState();
        cs.KnownPeople["Lila"] = new PersonState
        {
            Name          = "Lila",
            LastTouchedAt = Now.AddDays(-1),
            SharedTropes  = new List<string> { "late-night texts" },
        };

        var tropes = new List<PersonTrope>
        {
            new("LILA", "classroom patience"),
        };

        ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 5, now: Now);

        cs.KnownPeople.Should().ContainSingle();
        cs.KnownPeople.Should().ContainKey("Lila");
        cs.KnownPeople["Lila"].SharedTropes.Should().HaveCount(2);
    }

    [Fact]
    public void Apply_EmptyTropes_NoChange()
    {
        var cs = BuildState();
        var changed = ReflectionPhase.ApplyPersonTropes(cs,
            Array.Empty<PersonTrope>(), maxTropesPerPerson: 5, now: Now);
        changed.Should().BeFalse();
        cs.KnownPeople.Should().BeEmpty();
    }

    // -------- BuildCanonicalCast --------

    [Fact]
    public void BuildCanonicalCast_CombinesFamilyContextAndCanonicalContacts()
    {
        var cs = BuildState();
        var cast = ReflectionPhase.BuildCanonicalCast(cs);
        cast.Should().HaveCount(5); // 3 FamilyContext + 2 CanonicalContacts
        cast.Should().Contain(e => e.Contains("Lila"));
        cast.Should().Contain("Sarah");
    }

    [Fact]
    public void BuildCanonicalCast_SkipsBlankEntries()
    {
        var cs = new CharacterStateDoc
        {
            PrimaryContactName = "Mark",
            FamilyContext      = new List<string> { "", "   ", "Lila entry" },
            CanonicalContacts  = new List<string> { "", "Sarah" },
        };
        var cast = ReflectionPhase.BuildCanonicalCast(cs);
        cast.Should().BeEquivalentTo(new[] { "Lila entry", "Sarah" });
    }
}
