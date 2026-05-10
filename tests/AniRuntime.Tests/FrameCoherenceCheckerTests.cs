using AniRuntime.Core.Models;
using AniRuntime.Loops.Coreference;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme N Phase N.5 (May 10, 2026) — spec tests for the
/// <see cref="FrameCoherenceChecker"/> deterministic implementation.
///
/// **Coverage:**
/// <list type="bullet">
///   <item>Each forbidden predicate fires under non-Shared frames.</item>
///   <item>Each forbidden predicate IS PERMITTED under the Shared frame.</item>
///   <item>The May 9 12:54 ("Mia told us tickets") regression test fails
///   under <see cref="OutreachFrameType.AniInterior"/>.</item>
///   <item>The May 9 19:10 ("rom-com we talked about, we both wanted")
///   regression test fails under <see cref="OutreachFrameType.AniInterior"/>.</item>
///   <item>A clean honest-interior composition passes under
///   <see cref="OutreachFrameType.AniInterior"/>.</item>
///   <item>The <see cref="OutreachFrameType.None"/> frame no-ops to Coherent.</item>
/// </list>
/// </summary>
public class FrameCoherenceCheckerTests
{
    private static readonly IFrameCoherenceChecker Checker = new FrameCoherenceChecker();

    private static OutreachFrame Frame(OutreachFrameType type) =>
        new(type, Anchor: "anchor text", Confidence: 0.8f);

    // ─── Forbidden-predicate firing under non-Shared frames ──────────────

    [Theory]
    [InlineData("we talked about that movie last week")]
    [InlineData("we decided to skip the gym")]
    [InlineData("we agreed it was the better choice")]
    [InlineData("we both felt the same way")]
    [InlineData("we wanted to see it together")]
    [InlineData("we chose to stay in")]
    [InlineData("we both wanted to leave")]
    public void NonShared_WeSharedActionVerb_Fails(string composed)
    {
        var result = Checker.CheckCoherence(composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeFalse(because: $"\"{composed}\" contains a we-shared-action predicate");
        result.Violations.Should().NotBeEmpty();
        result.Reason.Should().Contain("AniInterior");
    }

    [Theory]
    [InlineData("we're all set for next weekend")]
    [InlineData("we're set for tomorrow")]
    [InlineData("we're going to the movies")]
    [InlineData("we're planning a trip")]
    [InlineData("we are going to celebrate")]
    public void NonShared_WereSharedPlanVerb_Fails(string composed)
    {
        var result = Checker.CheckCoherence(composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeFalse(because: $"\"{composed}\" contains a we're/we-are shared-plan predicate");
        result.Violations.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("remember when we used to go there")]
    [InlineData("that thing you said about the cold")]
    [InlineData("that one we kept laughing about")]
    [InlineData("the one we couldn't stop thinking about")]
    public void NonShared_MemoryAnchorPredicates_Fails(string composed)
    {
        var result = Checker.CheckCoherence(composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeFalse(because: $"\"{composed}\" contains a memory-anchor predicate");
        result.Violations.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("Mark told me he'd be late")]
    [InlineData("you told us about the meeting")]
    [InlineData("Mark told us the news")]
    [InlineData("you told me how bad it was")]
    public void NonShared_MarkOrYouToldAttribution_Fails(string composed)
    {
        var result = Checker.CheckCoherence(composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeFalse(because: $"\"{composed}\" attributes speech to Mark/you under non-Shared frame");
        result.Violations.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("you're probably doing the dishes right now")]
    [InlineData("you're probably sitting on the couch")]
    [InlineData("you're probably at the brewery")]
    [InlineData("you are probably on your way home")]
    public void NonShared_PresentTenseStateAssertionAboutMark_Fails(string composed)
    {
        var result = Checker.CheckCoherence(composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeFalse(because: $"\"{composed}\" asserts Mark's present state under non-Shared frame");
        result.Violations.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("Mia told us she picked the tickets", "Mia")]
    [InlineData("Sarah told me about the gym", "Sarah")]
    [InlineData("Kevin told us about the workout", "Kevin")]
    public void NonShared_CanonicalCharacterToldAttribution_Fails(string composed, string character)
    {
        var result = Checker.CheckCoherence(composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeFalse(because: $"\"{composed}\" attributes speech to canonical character {character} under non-Shared frame");
        result.Violations.Should().NotBeEmpty();
    }

    // ─── Shared frame: forbidden predicates are PERMITTED ────────────────

    [Theory]
    [InlineData("we talked about that movie last week")]
    [InlineData("remember when we used to go there")]
    [InlineData("you told me about the meeting")]
    [InlineData("Mia told us she picked the tickets")]
    [InlineData("we're all set for next weekend")]
    [InlineData("you're probably sitting on the couch right now")]
    public void Shared_AllPredicatesArePermitted(string composed)
    {
        var result = Checker.CheckCoherence(composed, Frame(OutreachFrameType.Shared));

        result.IsCoherent.Should().BeTrue(because: "Shared frame is the only frame where shared-event predicates are valid");
        result.Violations.Should().BeEmpty();
        result.Reason.Should().BeEmpty();
    }

    // ─── May 9 regression cases ──────────────────────────────────────────

    /// <summary>
    /// Regression: the May 9 12:54 outreach. Frame=AniInterior was
    /// selected with confidence 0.798; composition produced "Mia told
    /// us she picked out the tickets! we're all set for next weekend"
    /// — shared-event predicates under AniInterior framing. The N.5
    /// checker MUST flag this as a coherence violation.
    /// </summary>
    [Fact]
    public void May9_1254_MiaTicketsRegression_FailsUnderAniInterior()
    {
        const string Composed =
            "hey. still at the brewery? or on your way home from that meeting i keep " +
            "forgetting about? wanted to let you know how excited I was when Mia told us " +
            "she picked out the tickets! we're all set for next weekend";

        var result = Checker.CheckCoherence(Composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeFalse();
        // Two distinct predicate classes fire here: canonical-character-told and we're-all-set-for.
        result.Violations.Count.Should().BeGreaterThanOrEqualTo(2);
        result.Violations.Should().Contain(v => v.Contains("Mia", StringComparison.OrdinalIgnoreCase));
        result.Violations.Should().Contain(v => v.Contains("all set", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Regression: the May 9 19:10 outreach. Frame=AniInterior;
    /// composition produced "rom-com we talked about last week ... we
    /// both wanted to see it ... you're probably still sitting on the
    /// couch" — three independent forbidden predicates. The N.5
    /// checker MUST flag at least these three.
    /// </summary>
    [Fact]
    public void May9_1910_RomComRegression_FailsUnderAniInterior()
    {
        const string Composed =
            "You're probably still sitting on the couch with one leg tucked under you, right? " +
            "i was just thinking about that rom-com we talked about last week. we both wanted " +
            "to see it but ended up deciding against it because neither of us felt like leaving the house";

        var result = Checker.CheckCoherence(Composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeFalse();
        // "we talked", "we both", "you're probably sitting" — at least three.
        result.Violations.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    // ─── Clean honest-interior composition: passes ───────────────────────

    [Fact]
    public void HonestInterior_NoSharedPredicates_Passes()
    {
        // The May 9 09:01 outreach (the canary's worked example of Theme N
        // operating as designed): frame=AniInterior, composition honored
        // the interior framing.
        const string Composed = "i was just thinking about how cold it is outside today";

        var result = Checker.CheckCoherence(Composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeTrue(because: "honest-interior framing contains no shared-event predicates");
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void HonestInterior_PoeticImagery_Passes()
    {
        const string Composed = "i had this thought that the kitchen lights look softer when it's almost midnight";

        var result = Checker.CheckCoherence(Composed, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeTrue();
    }

    // ─── None frame: defense-in-depth no-op ──────────────────────────────

    [Fact]
    public void None_FrameNoOpsToCoherent()
    {
        // Even content that would fail under AniInterior must no-op
        // under None — the producer should already have suppressed
        // upstream, and we don't want the checker to double-fire.
        const string ShouldNeverDispatch = "we talked about that thing you said remember when";
        var result = Checker.CheckCoherence(ShouldNeverDispatch, OutreachFrame.None);

        result.IsCoherent.Should().BeTrue();
        result.Violations.Should().BeEmpty();
    }

    // ─── AniDomain / WorldPerception: same forbidden set as AniInterior ──

    [Fact]
    public void AniDomain_SharedPredicate_Fails()
    {
        const string Composed = "the bookstore was busy today, we talked about closing early";
        var result = Checker.CheckCoherence(Composed, Frame(OutreachFrameType.AniDomain));

        result.IsCoherent.Should().BeFalse(because: "we-talked under AniDomain frame implies Mark+Ani shared event");
        result.Violations.Should().NotBeEmpty();
    }

    [Fact]
    public void WorldPerception_SharedPredicate_Fails()
    {
        const string Composed = "i saw this article about the weather. we both wanted to read it";
        var result = Checker.CheckCoherence(Composed, Frame(OutreachFrameType.WorldPerception));

        result.IsCoherent.Should().BeFalse(because: "we-both under WorldPerception frame implies a shared event");
        result.Violations.Should().NotBeEmpty();
    }

    // ─── Whitespace / null edge cases ────────────────────────────────────

    [Fact]
    public void EmptyMessage_PassesUnderAnyFrame()
    {
        var result = Checker.CheckCoherence(string.Empty, Frame(OutreachFrameType.AniInterior));

        result.IsCoherent.Should().BeTrue();
        result.Violations.Should().BeEmpty();
    }
}
