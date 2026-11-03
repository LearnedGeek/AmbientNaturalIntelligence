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
    public void IsCanonical_SubstringOfLoreWord_Rejected()
    {
        // PR #189 Devin + Copilot: the earlier substring form accepted "Rob"
        // from "robot emojis" and would persist it as a canonical slot.
        // Whole-word boundary rejects.
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("Rob",  cs).Should().BeFalse();
        ReflectionPhase.IsCanonicalPersonName("rob",  cs).Should().BeFalse();
    }

    [Fact]
    public void IsCanonical_LowercaseProseSubstantive_Rejected()
    {
        // PR #189 Devin: the earlier form accepted "parents" from
        // "No parents in the picture" and "family" from "my family now".
        // Capitalized-first-letter rule rejects.
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("parents", cs).Should().BeFalse();
        ReflectionPhase.IsCanonicalPersonName("Parents", cs).Should().BeFalse(); // seed has lowercase
        ReflectionPhase.IsCanonicalPersonName("family",  cs).Should().BeFalse();
        ReflectionPhase.IsCanonicalPersonName("Family",  cs).Should().BeFalse();
    }

    [Fact]
    public void IsCanonical_CapitalizedMultiWordName_Accepted()
    {
        // "Sister Lila" appears verbatim in the seed prose as a capitalized
        // two-word phrase. Should match.
        var cs = BuildState();
        ReflectionPhase.IsCanonicalPersonName("Sister Lila", cs).Should().BeTrue();
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
    public void Apply_MultipleTropesForOnePersonInOneCall_BumpsMentionCountOnce()
    {
        // PR #189 Devin: MentionCount tracks REFLECTION CYCLES touching the
        // slot, not the number of extracted tropes. Two tropes for Lila in
        // one call must count as one cycle.
        var cs = BuildState();
        cs.KnownPeople["Lila"] = new PersonState
        {
            Name          = "Lila",
            LastTouchedAt = Now.AddDays(-1),
            MentionCount  = 4,
            SharedTropes  = new List<string> { "late-night texts" },
        };

        var tropes = new List<PersonTrope>
        {
            new("Lila", "classroom patience"),
            new("Lila", "robot emojis on birthdays"),
        };

        ReflectionPhase.ApplyPersonTropes(cs, tropes, maxTropesPerPerson: 5, now: Now);

        cs.KnownPeople["Lila"].MentionCount.Should().Be(5); // 4 + 1 (not + 2)
        cs.KnownPeople["Lila"].SharedTropes.Should().HaveCount(3);
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

    // -------- ResolveCanonicalSlotKey (issue #191) --------
    //
    // The alias-collapse resolver — "Lila" and "Sister Lila" must land on
    // the same slot across cycles. Without this the inner life fragments
    // into phrasing-variant ghosts (Copilot caught the gap on PR #189).

    private static CharacterStateDoc BuildStateWithAliasCanonicals() => new()
    {
        PrimaryContactName = "Mark",
        FamilyContext = new List<string>
        {
            "Sister Lila — my 'code-sis,' texts at midnight for birthdays with robot emojis",
            "Dominus — my dog. He barks at horror movies and steals socks",
        },
        CanonicalContacts = new List<string> { "Lila", "Dominus" },
    };

    [Fact]
    public void Resolve_ExactMatchOnCanonicalContacts_ReturnsCanonical()
    {
        var cs = BuildStateWithAliasCanonicals();
        ReflectionPhase.ResolveCanonicalSlotKey("Lila",    cs).Should().Be("Lila");
        ReflectionPhase.ResolveCanonicalSlotKey("Dominus", cs).Should().Be("Dominus");
    }

    [Fact]
    public void Resolve_ExactMatchCaseInsensitive_ReturnsCanonical()
    {
        var cs = BuildStateWithAliasCanonicals();
        ReflectionPhase.ResolveCanonicalSlotKey("lila", cs).Should().Be("Lila");
        ReflectionPhase.ResolveCanonicalSlotKey("LILA", cs).Should().Be("Lila");
    }

    [Fact]
    public void Resolve_AliasContainingCanonical_CollapsesToCanonical()
    {
        // The headline case: "Sister Lila" and "Lila" must route to one slot.
        var cs = BuildStateWithAliasCanonicals();
        ReflectionPhase.ResolveCanonicalSlotKey("Sister Lila", cs).Should().Be("Lila");
        ReflectionPhase.ResolveCanonicalSlotKey("sister lila", cs).Should().Be("Lila");
    }

    [Fact]
    public void Resolve_WholeWordBoundary_NoFalseCollapse()
    {
        // "Lilac" contains "Lila" as a substring but not as a whole word.
        // Must NOT collapse — otherwise an unrelated name gets folded in.
        var cs = BuildStateWithAliasCanonicals();
        ReflectionPhase.ResolveCanonicalSlotKey("Lilac", cs).Should().Be("Lilac");
    }

    [Fact]
    public void Resolve_NoCanonicalMatch_ReturnsEmittedTrimmed()
    {
        // Allowlist still gates upstream; this just confirms resolver is a
        // passthrough when CanonicalContacts has nothing relevant.
        var cs = BuildStateWithAliasCanonicals();
        ReflectionPhase.ResolveCanonicalSlotKey("  Sarah  ", cs).Should().Be("Sarah");
    }

    [Fact]
    public void Resolve_EmptyCanonicalContacts_ReturnsEmittedTrimmed()
    {
        var cs = new CharacterStateDoc
        {
            PrimaryContactName = "Mark",
            CanonicalContacts  = new List<string>(),
        };
        ReflectionPhase.ResolveCanonicalSlotKey("Sister Lila", cs).Should().Be("Sister Lila");
    }

    [Fact]
    public void Resolve_WhitespaceOrNullInput_ReturnsEmpty()
    {
        var cs = BuildStateWithAliasCanonicals();
        ReflectionPhase.ResolveCanonicalSlotKey("",   cs).Should().Be(string.Empty);
        ReflectionPhase.ResolveCanonicalSlotKey("  ", cs).Should().Be(string.Empty);
    }

    // -------- Alias collapse via ApplyPersonTropes (end-to-end) --------

    [Fact]
    public void Apply_SisterLilaAndLila_FoldToOneSlot()
    {
        // The exact regression #191 pins: PR #189 would have landed two
        // slots. With CanonicalContacts = ["Lila", ...] the resolver
        // collapses "Sister Lila" onto "Lila" so one slot carries both.
        var cs = BuildStateWithAliasCanonicals();

        ReflectionPhase.ApplyPersonTropes(cs,
            new List<PersonTrope> { new("Lila", "classroom patience") },
            maxTropesPerPerson: 5, now: Now);
        ReflectionPhase.ApplyPersonTropes(cs,
            new List<PersonTrope> { new("Sister Lila", "late-night texts") },
            maxTropesPerPerson: 5, now: Now.AddMinutes(5));

        cs.KnownPeople.Should().ContainSingle();
        cs.KnownPeople.Should().ContainKey("Lila");
        cs.KnownPeople["Lila"].SharedTropes
            .Should().BeEquivalentTo(new[] { "late-night texts", "classroom patience" },
                opts => opts.WithStrictOrdering());
        cs.KnownPeople["Lila"].MentionCount.Should().Be(2); // one per cycle
    }

    [Fact]
    public void Apply_AliasFirstThenCanonical_FoldToCanonicalKey()
    {
        // Ordering invariant: first cycle emits "Sister Lila", second emits
        // "Lila". The slot KEY must be the canonical short form ("Lila"),
        // not the alias — so later cycles that emit either form keep
        // routing deterministically.
        var cs = BuildStateWithAliasCanonicals();

        ReflectionPhase.ApplyPersonTropes(cs,
            new List<PersonTrope> { new("Sister Lila", "code-sis vibes") },
            maxTropesPerPerson: 5, now: Now);

        cs.KnownPeople.Should().ContainKey("Lila");
        cs.KnownPeople.Should().NotContainKey("Sister Lila");
        cs.KnownPeople["Lila"].Name.Should().Be("Lila");
    }

    [Fact]
    public void Apply_LilacNotCollapsed_RejectedByAllowlist()
    {
        // "Lilac" would (hypothetically) survive the resolver as "Lilac"
        // (whole-word boundary check rejects the collapse). It must still
        // fail the upstream IsCanonicalPersonName allowlist because
        // "Lilac" doesn't appear in FamilyContext prose or CanonicalContacts.
        var cs = BuildStateWithAliasCanonicals();

        var changed = ReflectionPhase.ApplyPersonTropes(cs,
            new List<PersonTrope> { new("Lilac", "fabricated") },
            maxTropesPerPerson: 5, now: Now);

        changed.Should().BeFalse();
        cs.KnownPeople.Should().BeEmpty();
    }

    // -------- ApplyCanonicalContactsBackfill (bot review PR #193) --------
    //
    // Startup migration: existing v2.1 deployments carry empty
    // CanonicalContacts (resolver passthrough) AND may have persisted
    // alias slots like "Sister Lila" from Phase 6 cycles that ran before
    // #193 shipped. Backfill must be idempotent on re-runs.

    private static CharacterStateDoc BuildPersistedV21State(params (string key, PersonState slot)[] knownPeople)
    {
        var cs = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            PersonaVersion     = "2.1",
            CoreTraits         = new List<string> { "warm" },
            FamilyContext      = new List<string>
            {
                "Sister Lila — my 'code-sis'",
                "Dominus — my dog",
            },
            // Note: CanonicalContacts deliberately left empty to simulate
            // a pre-#193 persisted state.
        };
        foreach (var (key, slot) in knownPeople) cs.KnownPeople[key] = slot;
        return cs;
    }

    private static readonly IReadOnlyList<string> SeedContacts = new[] { "Lila", "Dominus" };

    [Fact]
    public void Backfill_EmptyCanonicalContacts_Populated()
    {
        var cs = BuildPersistedV21State();

        var changed = ReflectionPhase.ApplyCanonicalContactsBackfill(
            cs, SeedContacts, maxTropesPerPerson: 5);

        changed.Should().BeTrue();
        cs.CanonicalContacts.Should().BeEquivalentTo(new[] { "Lila", "Dominus" });
    }

    [Fact]
    public void Backfill_NonEmptyCanonicalContacts_LeftAlone()
    {
        var cs = BuildPersistedV21State();
        cs.CanonicalContacts = new List<string> { "PriorEntry" };

        var changed = ReflectionPhase.ApplyCanonicalContactsBackfill(
            cs, SeedContacts, maxTropesPerPerson: 5);

        changed.Should().BeFalse();
        cs.CanonicalContacts.Should().BeEquivalentTo(new[] { "PriorEntry" });
    }

    [Fact]
    public void Backfill_IsIdempotent_OnSecondRun()
    {
        var cs = BuildPersistedV21State();

        var first  = ReflectionPhase.ApplyCanonicalContactsBackfill(cs, SeedContacts, 5);
        var second = ReflectionPhase.ApplyCanonicalContactsBackfill(cs, SeedContacts, 5);

        first.Should().BeTrue();
        second.Should().BeFalse("subsequent restarts must be no-ops so we don't thrash the DB");
    }

    [Fact]
    public void Backfill_AliasSlotWithNoCanonicalSlot_Rekeyed()
    {
        // Pre-#193 persisted state: a single "Sister Lila" slot. After
        // backfill the slot must be rekeyed to "Lila" with history intact.
        var oldSlot = new PersonState
        {
            Name             = "Sister Lila",
            FirstMentionedAt = Now.AddDays(-5),
            LastTouchedAt    = Now.AddHours(-2),
            MentionCount     = 4,
            SharedTropes     = new List<string> { "late-night texts", "classroom patience" },
        };
        var cs = BuildPersistedV21State(("Sister Lila", oldSlot));

        ReflectionPhase.ApplyCanonicalContactsBackfill(cs, SeedContacts, maxTropesPerPerson: 5);

        cs.KnownPeople.Should().ContainSingle();
        cs.KnownPeople.Should().ContainKey("Lila");
        cs.KnownPeople.Should().NotContainKey("Sister Lila");
        cs.KnownPeople["Lila"].Name.Should().Be("Lila");
        cs.KnownPeople["Lila"].MentionCount.Should().Be(4);
        cs.KnownPeople["Lila"].SharedTropes.Should().HaveCount(2);
    }

    [Fact]
    public void Backfill_AliasAndCanonicalSlotsCoexist_MergedOntoCanonical()
    {
        // Pathological pre-#193 state: both "Sister Lila" and "Lila" slots
        // persisted (first cycle before #193 emitted one, later cycle the
        // other — both landed separately). Merge them: tropes combined,
        // dedup'd, capped, mention counts summed, dates taken as
        // min(First), max(Last).
        var aliasSlot = new PersonState
        {
            Name             = "Sister Lila",
            FirstMentionedAt = Now.AddDays(-10),
            LastTouchedAt    = Now.AddDays(-3),
            MentionCount     = 3,
            SharedTropes     = new List<string> { "robot emojis", "late-night texts" },
        };
        var canonicalSlot = new PersonState
        {
            Name             = "Lila",
            FirstMentionedAt = Now.AddDays(-7),
            LastTouchedAt    = Now.AddHours(-1), // most recent
            MentionCount     = 2,
            SharedTropes     = new List<string> { "classroom patience", "late-night texts" }, // duplicate
        };
        var cs = BuildPersistedV21State(
            ("Sister Lila", aliasSlot),
            ("Lila",        canonicalSlot));

        ReflectionPhase.ApplyCanonicalContactsBackfill(cs, SeedContacts, maxTropesPerPerson: 5);

        cs.KnownPeople.Should().ContainSingle();
        cs.KnownPeople.Should().ContainKey("Lila");

        var merged = cs.KnownPeople["Lila"];
        merged.MentionCount.Should().Be(5); // 3 + 2
        merged.FirstMentionedAt.Should().Be(Now.AddDays(-10)); // min
        merged.LastTouchedAt.Should().Be(Now.AddHours(-1));    // max
        // Tropes: canonical's two kept, alias's "robot emojis" appended
        // (new), "late-night texts" skipped (duplicate case-insensitive).
        merged.SharedTropes.Should().HaveCount(3);
        merged.SharedTropes.Should().Contain("classroom patience");
        merged.SharedTropes.Should().Contain("late-night texts");
        merged.SharedTropes.Should().Contain("robot emojis");
    }

    [Fact]
    public void Backfill_MergeRespectsMaxTropesPerPerson()
    {
        var aliasSlot = new PersonState
        {
            Name         = "Sister Lila",
            SharedTropes = new List<string> { "a1", "a2", "a3", "a4" },
        };
        var canonicalSlot = new PersonState
        {
            Name         = "Lila",
            SharedTropes = new List<string> { "c1", "c2" },
        };
        var cs = BuildPersistedV21State(
            ("Sister Lila", aliasSlot),
            ("Lila",        canonicalSlot));

        ReflectionPhase.ApplyCanonicalContactsBackfill(cs, SeedContacts, maxTropesPerPerson: 3);

        cs.KnownPeople["Lila"].SharedTropes.Should().HaveCount(3);
    }

    [Fact]
    public void Backfill_NoChange_ReturnsFalse()
    {
        // Fresh state: no aliased slots, non-empty CanonicalContacts.
        // The whole helper must be a no-op so callers can skip the write.
        var cs = new CharacterStateDoc
        {
            PrimaryContactName = "Mark",
            CanonicalContacts  = new List<string> { "Lila", "Dominus" },
        };
        cs.KnownPeople["Lila"] = new PersonState
        {
            Name = "Lila",
            SharedTropes = new List<string> { "t1" },
        };

        var changed = ReflectionPhase.ApplyCanonicalContactsBackfill(cs, SeedContacts, 5);

        changed.Should().BeFalse();
    }
}
