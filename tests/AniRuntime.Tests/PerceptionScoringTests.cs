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

    // ── F-4 Phase 4a.1c fatigue ────────────────────────────────────────────

    [Fact]
    public void FatigueDecay_ZeroExposure_ReturnsOne()
    {
        PerceptionScoring.FatigueDecay(
            exposureCount: 0, lastExposedAt: null, now: DateTimeOffset.UtcNow)
            .Should().Be(1f);
    }

    [Fact]
    public void FatigueDecay_NullLastExposedAt_ReturnsOne()
    {
        PerceptionScoring.FatigueDecay(
            exposureCount: 5, lastExposedAt: null, now: DateTimeOffset.UtcNow)
            .Should().Be(1f, "null LastExposedAt means never surfaced — fresh");
    }

    [Fact]
    public void FatigueDecay_OneExposureImmediate_DecaysToExpected()
    {
        var now = DateTimeOffset.UtcNow;
        // exp(-1/3) ≈ 0.7165
        PerceptionScoring.FatigueDecay(
            exposureCount: 1, lastExposedAt: now, now: now)
            .Should().BeApproximately(0.7165f, 0.001f);
    }

    [Fact]
    public void FatigueDecay_FiveExposuresImmediate_DecaysSharp()
    {
        var now = DateTimeOffset.UtcNow;
        // exp(-5/3) ≈ 0.1889
        PerceptionScoring.FatigueDecay(
            exposureCount: 5, lastExposedAt: now, now: now)
            .Should().BeApproximately(0.1889f, 0.001f);
    }

    [Fact]
    public void FatigueDecay_TimeRecovery_HalfLifeHoursPasses_PartialRecovery()
    {
        // At 48h since last exposure, time_decay = exp(-1) ≈ 0.37
        // effective_exposure = 5 × 0.37 ≈ 1.85
        // fatigue = exp(-1.85/3) ≈ 0.538
        var now = DateTimeOffset.UtcNow;
        var fatigue = PerceptionScoring.FatigueDecay(
            exposureCount: 5, lastExposedAt: now.AddHours(-48), now: now);
        fatigue.Should().BeApproximately(0.538f, 0.01f,
            "48h since last exposure partially recovers from 5-exposure fatigue");
    }

    [Fact]
    public void FatigueDecay_TimeRecovery_WeekPasses_NearlyFullRecovery()
    {
        // At 168h (1 week), time_decay = exp(-168/48) ≈ 0.030
        // effective = 5 × 0.030 ≈ 0.15; fatigue ≈ exp(-0.05) ≈ 0.95
        var now = DateTimeOffset.UtcNow;
        var fatigue = PerceptionScoring.FatigueDecay(
            exposureCount: 5, lastExposedAt: now.AddHours(-168), now: now);
        fatigue.Should().BeGreaterThan(0.9f,
            "a week since last exposure substantially recovers fatigue");
    }

    [Fact]
    public void FatigueDecay_MonotonicInExposureAtSameTime()
    {
        var now = DateTimeOffset.UtcNow;
        var one   = PerceptionScoring.FatigueDecay(1, now, now);
        var three = PerceptionScoring.FatigueDecay(3, now, now);
        var five  = PerceptionScoring.FatigueDecay(5, now, now);
        one.Should().BeGreaterThan(three);
        three.Should().BeGreaterThan(five);
    }

    [Fact]
    public void FatigueDecay_MonotonicInTimeSinceAtSameExposure()
    {
        var now = DateTimeOffset.UtcNow;
        var immediate = PerceptionScoring.FatigueDecay(5, now, now);
        var later1    = PerceptionScoring.FatigueDecay(5, now.AddHours(-24), now);
        var later2    = PerceptionScoring.FatigueDecay(5, now.AddHours(-72), now);
        immediate.Should().BeLessThan(later1,
            "time passes, exposure 'wears off,' fatigue decreases");
        later1.Should().BeLessThan(later2);
    }

    [Fact]
    public void PerceptionEffectiveWeight_FreshBeatsStaleRepeat()
    {
        // Issue #169 acceptance scenario: fresh news (never surfaced)
        // outranks a 5× surfaced same-topic story at same significance.
        var dict = new Dictionary<string, float> { ["news"] = 0.8f };
        var now = DateTimeOffset.UtcNow;

        var fresh = PerceptionScoring.PerceptionEffectiveWeight(
            significance: 0.7f, importance: 0.6f, content: "news item A",
            topicValence: dict, exposureCount: 0, lastExposedAt: null, now: now);

        var stale = PerceptionScoring.PerceptionEffectiveWeight(
            significance: 0.7f, importance: 0.6f, content: "news item B",
            topicValence: dict, exposureCount: 5, lastExposedAt: now, now: now);

        fresh.Should().BeGreaterThan(stale,
            "a 5×-exposed story loses to a fresh one at the same base weight");
    }
}
