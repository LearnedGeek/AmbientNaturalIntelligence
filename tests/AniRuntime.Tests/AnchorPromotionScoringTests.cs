using AniRuntime.Core.Utilities;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 4a.2c — pins the pure-static anchor-promotion scorer.
/// Counting is case-insensitive and trim-tolerant; threshold is inclusive;
/// existing-key match preserves canonical casing.
/// </summary>
public class AnchorPromotionScoringTests
{
    [Fact]
    public void Score_NullAnchors_ReturnsEmpty()
    {
        AnchorPromotionScoring.Score(null, null, minOccurrences: 3)
            .Should().BeEmpty();
    }

    [Fact]
    public void Score_EmptyAnchors_ReturnsEmpty()
    {
        AnchorPromotionScoring.Score(Array.Empty<string>(), null, minOccurrences: 3)
            .Should().BeEmpty();
    }

    [Fact]
    public void Score_ThresholdZeroOrNegative_ReturnsEmpty()
    {
        var anchors = new[] { "a", "a", "a" };
        AnchorPromotionScoring.Score(anchors, null, minOccurrences: 0).Should().BeEmpty();
        AnchorPromotionScoring.Score(anchors, null, minOccurrences: -1).Should().BeEmpty();
    }

    [Fact]
    public void Score_BelowThreshold_Skipped()
    {
        var anchors = new[] { "lila", "lila" };
        AnchorPromotionScoring.Score(anchors, null, minOccurrences: 3)
            .Should().BeEmpty();
    }

    [Fact]
    public void Score_AtThreshold_Emitted()
    {
        var anchors = new[] { "lila", "lila", "lila" };
        var result = AnchorPromotionScoring.Score(anchors, null, minOccurrences: 3);
        result.Should().HaveCount(1);
        result[0].Topic.Should().Be("lila");
        result[0].Occurrences.Should().Be(3);
        result[0].ExistingKeyMatch.Should().BeNull();
    }

    [Fact]
    public void Score_CaseInsensitive_CountsMerge()
    {
        var anchors = new[] { "Lila", "LILA", "lila", " lila " };
        var result = AnchorPromotionScoring.Score(anchors, null, minOccurrences: 3);
        result.Should().HaveCount(1);
        result[0].Occurrences.Should().Be(4);
    }

    [Fact]
    public void Score_PreservesFirstSeenCasing_WhenNoExistingKey()
    {
        var anchors = new[] { "Lila", "lila", "LILA" };
        var result = AnchorPromotionScoring.Score(anchors, null, minOccurrences: 3);
        result.Should().ContainSingle();
        result[0].Topic.Should().Be("Lila", "first-seen casing wins when no existing key matches");
    }

    [Fact]
    public void Score_MatchesExistingKey_PreservesCanonicalCasing()
    {
        var anchors      = new[] { "lila", "LILA", "lila" };
        var existingKeys = new[] { "Lila" };
        var result = AnchorPromotionScoring.Score(anchors, existingKeys, minOccurrences: 3);
        result.Should().ContainSingle();
        result[0].ExistingKeyMatch.Should().Be("Lila");
    }

    [Fact]
    public void Score_NoExistingKeyMatch_LeavesMatchNull()
    {
        var anchors      = new[] { "the shelf", "the shelf", "the shelf" };
        var existingKeys = new[] { "cooking", "mornings" };
        var result = AnchorPromotionScoring.Score(anchors, existingKeys, minOccurrences: 3);
        result.Should().ContainSingle();
        result[0].ExistingKeyMatch.Should().BeNull();
    }

    [Fact]
    public void Score_MixedAnchors_OnlyThresholdCrossersEmitted()
    {
        var anchors = new[]
        {
            "lila", "lila", "lila",      // 3 — crosses
            "cooking",                     // 1 — below
            "the shelf", "the shelf",      // 2 — below
            "mornings", "mornings", "mornings", "mornings",  // 4 — crosses
        };
        var result = AnchorPromotionScoring.Score(anchors, null, minOccurrences: 3);
        result.Should().HaveCount(2);
        result.Select(c => c.Topic).Should().BeEquivalentTo(new[] { "lila", "mornings" });
    }

    [Fact]
    public void Score_SkipsWhitespaceAndEmpty()
    {
        var anchors = new[] { "lila", "", "  ", null!, "lila", "lila" };
        var result = AnchorPromotionScoring.Score(anchors, null, minOccurrences: 3);
        result.Should().ContainSingle();
        result[0].Occurrences.Should().Be(3);
    }
}
