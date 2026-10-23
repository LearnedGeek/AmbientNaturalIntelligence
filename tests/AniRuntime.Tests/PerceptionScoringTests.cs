using AniRuntime.Core.Utilities;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 4a.1a — pins the pure-static TopicValence fuzzy lookup
/// discipline. No I/O; axis-2 of the three-dimension importance model.
/// </summary>
public class PerceptionScoringTests
{
    [Fact]
    public void FuzzyTopicValenceLookup_EmptyDict_ReturnsDefault()
    {
        PerceptionScoring
            .FuzzyTopicValenceLookup("some content", new Dictionary<string, float>())
            .Should().Be(PerceptionScoring.NoMatchDefaultWeight);
    }

    [Fact]
    public void FuzzyTopicValenceLookup_NullContent_ReturnsDefault()
    {
        var dict = new Dictionary<string, float> { ["Mia"] = 0.9f };
        PerceptionScoring.FuzzyTopicValenceLookup(null, dict)
            .Should().Be(PerceptionScoring.NoMatchDefaultWeight);
    }

    [Fact]
    public void FuzzyTopicValenceLookup_SingleKeyMatch_ReturnsThatWeight()
    {
        var dict = new Dictionary<string, float> { ["Mia"] = 0.9f };
        PerceptionScoring
            .FuzzyTopicValenceLookup("Mia is at Starbucks today", dict)
            .Should().Be(0.9f);
    }

    [Fact]
    public void FuzzyTopicValenceLookup_MatchIsCaseInsensitive()
    {
        var dict = new Dictionary<string, float> { ["Mia"] = 0.9f };
        PerceptionScoring
            .FuzzyTopicValenceLookup("mia is at starbucks", dict)
            .Should().Be(0.9f);
    }

    [Fact]
    public void FuzzyTopicValenceLookup_MultipleMatches_ReturnsHighestWeight()
    {
        var dict = new Dictionary<string, float>
        {
            ["Spanish lessons"] = 0.5f,
            ["Mia"]             = 0.9f,
            ["Okauchee"]        = 0.8f,
        };
        PerceptionScoring
            .FuzzyTopicValenceLookup("Mia and I went to Okauchee for Spanish lessons", dict)
            .Should().Be(0.9f, "Mia at 0.9 is the highest among the three matches");
    }

    [Fact]
    public void FuzzyTopicValenceLookup_NoMatch_ReturnsDefault()
    {
        var dict = new Dictionary<string, float> { ["Mia"] = 0.9f, ["Okauchee"] = 0.8f };
        PerceptionScoring
            .FuzzyTopicValenceLookup("Eurovision Song Contest Final tonight", dict)
            .Should().Be(PerceptionScoring.NoMatchDefaultWeight);
    }

    [Fact]
    public void FuzzyTopicValenceLookup_ShortKeyIgnored()
    {
        // Short-key guard: keys of length < 3 produce false positives
        // (substring match on 1-2 char keys matches nearly any content).
        var dict = new Dictionary<string, float>
        {
            ["in"]              = 0.95f,  // 2-char; should be ignored
            ["Spanish lessons"] = 0.5f,
        };
        PerceptionScoring
            .FuzzyTopicValenceLookup("Spanish lessons today", dict)
            .Should().Be(0.5f, "the 2-char 'in' key is ignored; only 'Spanish lessons' matches");
    }

    [Fact]
    public void FuzzyTopicValenceLookup_WhitespaceKeySkipped()
    {
        var dict = new Dictionary<string, float>
        {
            ["   "] = 0.9f,
            ["Mia"] = 0.5f,
        };
        PerceptionScoring
            .FuzzyTopicValenceLookup("Mia is at Starbucks", dict)
            .Should().Be(0.5f);
    }

    [Fact]
    public void FuzzyTopicValenceLookup_ContentContainsKeyAsSubstring_StillMatches()
    {
        // Known heuristic limitation documented in the XML doc:
        // substring match catches "Mia" inside "miami". Test pins this
        // behavior so a future tightening (e.g., word-boundary) is a
        // conscious change, not an accident.
        var dict = new Dictionary<string, float> { ["Mia"] = 0.9f };
        PerceptionScoring
            .FuzzyTopicValenceLookup("Miami weather today", dict)
            .Should().Be(0.9f, "substring matching is intentional per the XML doc; may tighten in a later phase");
    }

    // ── F-4 Phase 4a.1b composite ──────────────────────────────────────────

    [Fact]
    public void PerceptionEffectiveWeight_ProductOfThreeAxes()
    {
        var dict = new Dictionary<string, float> { ["Mia"] = 0.8f };
        var w = PerceptionScoring.PerceptionEffectiveWeight(
            significance: 0.9f, importance: 0.5f, content: "Mia came by", topicValence: dict);
        // 0.9 × 0.5 × 0.8 = 0.36
        w.Should().BeApproximately(0.36f, 0.001f);
    }

    [Fact]
    public void PerceptionEffectiveWeight_HighSignificanceWinsOverHighInterestOnly()
    {
        // The Life Audit anchor: Peru recipe scored higher than Ukraine war
        // under Mark-interest-only. Phase 4a.1b inverts the ordering by
        // multiplying Significance into the composite.
        var dict = new Dictionary<string, float>
        {
            ["recipe"] = 0.9f,       // Mark has engaged with cooking content
            ["politics"] = 0.3f,     // Mark hasn't deeply engaged with politics
        };

        // Mark-interest-only would prefer Peru recipe (high keyword match).
        var peruRecipe = PerceptionScoring.PerceptionEffectiveWeight(
            significance: 0.1f, importance: 0.85f, content: "Peru recipe", topicValence: dict);
        var ukraineWar = PerceptionScoring.PerceptionEffectiveWeight(
            significance: 0.9f, importance: 0.4f, content: "Ukraine politics escalates", topicValence: dict);

        ukraineWar.Should().BeGreaterThan(peruRecipe,
            "Phase 4a.1b: Significance 0.9 overrides Mark-interest ordering");
    }

    [Fact]
    public void PerceptionEffectiveWeight_ZeroSignificance_ZerosComposite()
    {
        var dict = new Dictionary<string, float> { ["x"] = 0.9f };
        var w = PerceptionScoring.PerceptionEffectiveWeight(
            significance: 0.0f, importance: 1.0f, content: "x", topicValence: dict);
        w.Should().Be(0f);
    }

    [Fact]
    public void PerceptionEffectiveWeight_NoTopicMatch_StillComposes()
    {
        var dict = new Dictionary<string, float> { ["Mia"] = 0.8f };
        var w = PerceptionScoring.PerceptionEffectiveWeight(
            significance: 0.6f, importance: 0.5f, content: "some unrelated content", topicValence: dict);
        // 0.6 × 0.5 × 0.5 (default) = 0.15
        w.Should().BeApproximately(0.15f, 0.001f);
    }
}
