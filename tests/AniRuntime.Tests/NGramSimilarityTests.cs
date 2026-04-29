using AniRuntime.Core.Utilities;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Spec tests for <see cref="NGramSimilarity"/>, the Theme E #5 (Apr 29, 2026)
/// generation-loop degeneracy guard. Pins the trigram-Jaccard contract so
/// future tuning of the threshold or tokenization doesn't silently regress
/// the duck-norris-77x / vanilla-cream-soda-30x recurrence interruption the
/// guard was designed for.
/// </summary>
public class NGramSimilarityTests
{
    // ─── Tokenization ───

    [Fact]
    public void Tokenize_LowercasesAndStripsPunctuation()
    {
        var tokens = NGramSimilarity.Tokenize("Hey! Mark, what's up?");

        tokens.Should().Equal(new[] { "hey", "mark", "what's", "up" });
    }

    [Fact]
    public void Tokenize_EmptyOrWhitespace_ReturnsEmpty()
    {
        NGramSimilarity.Tokenize("").Should().BeEmpty();
        NGramSimilarity.Tokenize("   ").Should().BeEmpty();
        NGramSimilarity.Tokenize(null).Should().BeEmpty();
    }

    // ─── Trigram set ───

    [Fact]
    public void Trigrams_ProducesSlidingThreeWordWindows()
    {
        var trigrams = NGramSimilarity.Trigrams("the quick brown fox jumps");

        trigrams.Should().BeEquivalentTo(new[]
        {
            "the quick brown",
            "quick brown fox",
            "brown fox jumps",
        });
    }

    [Fact]
    public void Trigrams_FewerThanThreeTokens_ReturnsEmpty()
    {
        NGramSimilarity.Trigrams("just two").Should().BeEmpty();
        NGramSimilarity.Trigrams("one").Should().BeEmpty();
        NGramSimilarity.Trigrams("").Should().BeEmpty();
    }

    // ─── Jaccard similarity ───

    /// <summary>
    /// SPEC: identical content has Jaccard 1.0 (full trigram overlap).
    /// </summary>
    [Fact]
    public void JaccardSimilarity_IdenticalContent_Returns1()
    {
        var a = "i'm sitting here with my vanilla cream soda almost gone again";
        var b = "i'm sitting here with my vanilla cream soda almost gone again";

        NGramSimilarity.JaccardSimilarity(a, b).Should().Be(1.0);
    }

    /// <summary>
    /// SPEC: completely unrelated content has Jaccard 0.0 (no trigram overlap).
    /// </summary>
    [Fact]
    public void JaccardSimilarity_DisjointContent_Returns0()
    {
        var a = "the weather today is cloudy and grey";
        var b = "i finished that book about astronauts last night";

        NGramSimilarity.JaccardSimilarity(a, b).Should().Be(0.0);
    }

    /// <summary>
    /// SPEC: empty input on either side returns 0.0 (cannot compute).
    /// </summary>
    [Fact]
    public void JaccardSimilarity_EmptyInput_Returns0()
    {
        NGramSimilarity.JaccardSimilarity("", "the quick brown fox").Should().Be(0.0);
        NGramSimilarity.JaccardSimilarity("the quick brown fox", "").Should().Be(0.0);
        NGramSimilarity.JaccardSimilarity("", "").Should().Be(0.0);
    }

    // ─── IsNearDuplicate (the guard's actual contract) ───

    /// <summary>
    /// SPEC: the canonical case — Ani repeating the vanilla-cream-soda thought
    /// shape with minor word substitutions across cycles. Trigram Jaccard
    /// catches it above the 0.50 threshold.
    /// </summary>
    [Fact]
    public void IsNearDuplicate_VanillaCreamSodaRecurrence_IsCaught()
    {
        var newThought = "i'm sitting here with my vanilla cream soda almost gone again";
        var recentThought = "i'm sitting here with my vanilla cream soda almost gone again, just thinking";

        NGramSimilarity.IsNearDuplicate(newThought, new[] { recentThought })
            .Should().BeTrue("near-duplicate recurrence with minor padding must be caught");
    }

    /// <summary>
    /// SPEC: Ani revisiting a topic with genuinely different framing is NOT a
    /// near-duplicate. Same subject (vanilla cream soda) but different
    /// surrounding language → trigram overlap stays below threshold.
    /// </summary>
    [Fact]
    public void IsNearDuplicate_SameSubjectDifferentFraming_IsNotCaught()
    {
        var newThought = "vanilla cream soda always tastes like a quiet afternoon to me";
        var recentThought = "the bookstore goes still around three o'clock and that's when i open the soda";

        NGramSimilarity.IsNearDuplicate(newThought, new[] { recentThought })
            .Should().BeFalse("legitimate topic continuity with different framing must NOT be flagged as degeneracy");
    }

    /// <summary>
    /// SPEC: an empty references list means "first thought, nothing to compare
    /// against" — never flag as duplicate.
    /// </summary>
    [Fact]
    public void IsNearDuplicate_EmptyReferences_ReturnsFalse()
    {
        NGramSimilarity.IsNearDuplicate("any candidate text here at all", Array.Empty<string>())
            .Should().BeFalse();
    }

    /// <summary>
    /// SPEC: short candidate (fewer than 3 tokens) returns false — can't form
    /// trigrams, can't be a meaningful comparison. The save proceeds; if the
    /// thought is genuinely a 1-2 word fragment, the cycle's other guards
    /// (valence threshold, etc.) handle it.
    /// </summary>
    [Fact]
    public void IsNearDuplicate_ShortCandidate_ReturnsFalse()
    {
        NGramSimilarity.IsNearDuplicate("hi there", new[] { "hi there friend i missed you" })
            .Should().BeFalse("candidate with fewer than 3 tokens cannot form trigrams; default false");
    }

    /// <summary>
    /// SPEC: threshold parameter is honored. At threshold = 0.10, even modest
    /// overlap fires. At threshold = 0.99, only near-identical content fires.
    /// Pins the threshold contract for any future tuning.
    /// </summary>
    [Fact]
    public void IsNearDuplicate_ThresholdParameter_IsHonored()
    {
        var candidate = "the morning was quiet and the books smelled like rain";
        var reference = "the morning was quiet and the rain was falling steady";

        // Some shared trigrams ("the morning was", "morning was quiet", "was quiet and"),
        // some divergent. Should sit between high and low thresholds.
        NGramSimilarity.IsNearDuplicate(candidate, new[] { reference }, threshold: 0.10)
            .Should().BeTrue("low threshold catches modest overlap");
        NGramSimilarity.IsNearDuplicate(candidate, new[] { reference }, threshold: 0.99)
            .Should().BeFalse("high threshold requires near-identity");
    }

    /// <summary>
    /// SPEC: any reference matching at threshold flags the candidate, even if
    /// other references in the list are unrelated. The guard is "is this a
    /// duplicate of ANY recent thought," not "all of them."
    /// </summary>
    [Fact]
    public void IsNearDuplicate_MatchesAnyReferenceAboveThreshold()
    {
        var candidate = "duck norris is on the mantle watching the room go quiet";
        var unrelated1 = "the weather report said snow this weekend would be heavy";
        var unrelated2 = "i finished reading that book about astronauts in space last week";
        var matchingRef = "duck norris is on the mantle watching the room go quiet";

        NGramSimilarity.IsNearDuplicate(candidate, new[] { unrelated1, unrelated2, matchingRef })
            .Should().BeTrue("any single reference at-or-above threshold flags the candidate");
    }
}
