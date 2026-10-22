using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 2.1 — pins the pure-static scoring math for outreach-seed
/// selection. Importance × (1 + AnchorBonus) over interior-side character
/// state. No I/O; the retrieval + fallback is in
/// AnchorBearingOutreachSeedSelector tests.
/// </summary>
public class OutreachSeedScoringTests
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
    public void Score_NullContent_ReturnsBareImportance()
    {
        OutreachSeedScoring.Score(null, 0.8f, new CharacterStateDoc())
            .Should().Be(0.8f);
    }

    [Fact]
    public void Score_EmptyPool_ReturnsBareImportance()
    {
        OutreachSeedScoring.Score("Mia came by today", 0.8f, new CharacterStateDoc())
            .Should().Be(0.8f);
    }

    [Fact]
    public void Score_NoAnchorMatch_ReturnsBareImportance()
    {
        var cs = State(familyContext: new[] { "Sister Lila — my code-sis" });
        OutreachSeedScoring.Score("sitting here on the couch again", 0.9f, cs)
            .Should().Be(0.9f);
    }

    [Fact]
    public void Score_AnchorMatch_ReturnsImportanceWithBonus()
    {
        var cs = State(familyContext: new[] { "Sister Lila — my code-sis" });
        // Default bonus 0.3 → 0.7 × 1.3 = 0.91
        OutreachSeedScoring.Score("thinking about Lila's weird robot emojis", 0.7f, cs)
            .Should().BeApproximately(0.91f, 0.001f);
    }

    [Fact]
    public void Score_AnchorMatch_LeadingNameExtracted()
    {
        // Entry has prose; only the leading name "Fleetwood Mac Landslide"
        // (before the em dash) should matter for the substring match.
        var cs = State(interests: new[]
        {
            "Fleetwood Mac Landslide live version — my secret joy from spinning in the kitchen"
        });
        OutreachSeedScoring.Score(
            "put on Fleetwood Mac Landslide and swayed", 0.6f, cs)
            .Should().BeApproximately(0.78f, 0.001f, "0.6 × 1.3 = 0.78");
    }

    [Fact]
    public void Score_CaseInsensitive()
    {
        var cs = State(familyContext: new[] { "Sister Lila — my code-sis" });
        OutreachSeedScoring.Score("thinking about LILA today", 0.5f, cs)
            .Should().BeApproximately(0.65f, 0.001f);
    }

    [Fact]
    public void Score_AnchorBearingThoughtBeatsHigherImportanceDefault()
    {
        // The whole point of Phase 2.1: anchor-bearing thought at modest
        // Importance beats register-locked thought at ceiling Importance.
        var cs = State(familyContext: new[] { "Sister Lila" });

        var registerLocked = OutreachSeedScoring.Score(
            "sitting here curled up on the couch", 0.9f, cs);
        var anchorBearing  = OutreachSeedScoring.Score(
            "Lila texted me again, something about her new job", 0.75f, cs);

        anchorBearing.Should().BeGreaterThan(registerLocked,
            "0.75 × 1.3 = 0.975 beats 0.9");
    }

    [Fact]
    public void Score_MarkFacingListsIgnored()
    {
        // Same discipline as NamedSubstrateAnchor: ThingsContactCares and
        // LearnedAboutContact are Mark's facts, not Ani's interior.
        var cs = State(
            thingsContactCares:  new[] { "Mia — his daughter" },
            learnedAboutContact: new[] { "Mark drives a Jeep" });

        OutreachSeedScoring.Score("Mia came by the shop today", 0.7f, cs)
            .Should().Be(0.7f, "Mia from ThingsContactCares is excluded from the pool");
        OutreachSeedScoring.Score("saw a Jeep like Mark's", 0.7f, cs)
            .Should().Be(0.7f, "Jeep from LearnedAboutContact is excluded");
    }

    [Fact]
    public void Score_ShortLeadingName_Skipped()
    {
        // Leading-name extraction < 3 chars is skipped (false-positive guard).
        var cs = State(familyContext: new[] { "Hi — my friend's dog" });

        OutreachSeedScoring.Score("Hi there, having coffee", 0.7f, cs)
            .Should().Be(0.7f, "2-char leading name 'Hi' is below the short-key floor");
    }

    [Fact]
    public void ExtractLeadingName_HandlesEmDashAndComma()
    {
        OutreachSeedScoring.ExtractLeadingName("Sister Lila — my 'code-sis,' texts at midnight")
            .Should().Be("Sister Lila");

        OutreachSeedScoring.ExtractLeadingName("Dominus, my dog who barks at horror")
            .Should().Be("Dominus");

        OutreachSeedScoring.ExtractLeadingName("Okauchee Lake (where we want to live)")
            .Should().Be("Okauchee Lake");

        OutreachSeedScoring.ExtractLeadingName("Mia")
            .Should().Be("Mia");
    }
}
