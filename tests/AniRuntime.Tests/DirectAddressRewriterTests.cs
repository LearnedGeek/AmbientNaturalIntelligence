using AniRuntime.Loops.Coreference;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Spec tests for the <see cref="DirectAddressRewriter"/> stop-gap. These
/// pin the regex behaviour for the bridge period until the LM-Kit-based
/// coreference model lands per
/// <c>docs/research/model-coreference-ideas.md</c>. Don't extend by
/// adding more regex tweaks — when a new failure shape appears, queue it
/// for the coref model replacement.
/// </summary>
public class DirectAddressRewriterTests
{
    [Fact]
    public void Rewrite_HeThinks_ProducesYouThink()
    {
        var result = DirectAddressRewriter.Rewrite(
            "hahaha he thinks it's our company song now i'm dying over here",
            "Mark");

        result.Should().Be(
            "hahaha you think it's our company song now i'm dying over here");
    }

    [Fact]
    public void Rewrite_ContactNameAsSubject_ProducesYouWithVerbAgreement()
    {
        var result = DirectAddressRewriter.Rewrite(
            "Mark thinks the cold brew is overrated",
            "Mark");

        result.Should().Be("you think the cold brew is overrated");
    }

    [Fact]
    public void Rewrite_ContactNamePossessive_ProducesYour()
    {
        var result = DirectAddressRewriter.Rewrite(
            "Mark's coworker handed me the cold brew note",
            "Mark");

        result.Should().Be("your coworker handed me the cold brew note");
    }

    [Fact]
    public void Rewrite_HisPossessive_ProducesYour()
    {
        var result = DirectAddressRewriter.Rewrite(
            "his desk is right next to mine",
            "Mark");

        result.Should().Be("your desk is right next to mine");
    }

    [Fact]
    public void Rewrite_HimObject_ProducesYou()
    {
        var result = DirectAddressRewriter.Rewrite(
            "i was thinking about him today",
            "Mark");

        result.Should().Be("i was thinking about you today");
    }

    [Fact]
    public void Rewrite_MultipleViolations_AllConverted()
    {
        var result = DirectAddressRewriter.Rewrite(
            "Mark, he says his cold brew is overrated",
            "Mark");

        result.Should().Be("you, you say your cold brew is overrated");
    }

    [Theory]
    [InlineData("he is here",   "you are here")]
    [InlineData("he was tired", "you were tired")]
    [InlineData("he has time",  "you have time")]
    [InlineData("he does it",   "you do it")]
    [InlineData("he goes home", "you go home")]
    public void Rewrite_IrregularConjugations(string input, string expected)
    {
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(expected);
    }

    [Theory]
    [InlineData("he tries hard",       "you try hard")]
    [InlineData("he watches the show", "you watch the show")]
    [InlineData("he kisses softly",    "you kiss softly")]
    [InlineData("he washes dishes",    "you wash dishes")]
    [InlineData("he fixes things",     "you fix things")]
    [InlineData("he loves coffee",     "you love coffee")]
    [InlineData("he wants pizza",      "you want pizza")]
    [InlineData("he runs daily",       "you run daily")]
    public void Rewrite_RegularVerbConjugations(string input, string expected)
    {
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(expected);
    }

    [Fact]
    public void Rewrite_AdverbBetweenSubjectAndVerb_StillConjugates()
    {
        DirectAddressRewriter.Rewrite("Mark always cracks me up", "Mark")
            .Should().Be("you always crack me up");
    }

    [Fact]
    public void Rewrite_AlreadyRewritten_IsNoOp()
    {
        var input = "you think it's our company song";
        var first = DirectAddressRewriter.Rewrite(input, "Mark");
        var second = DirectAddressRewriter.Rewrite(first, "Mark");

        first.Should().Be(input);
        second.Should().Be(first);
    }

    [Fact]
    public void Rewrite_BaseFormVerbAfterYou_NotChanged()
    {
        var input = "you have time and you are kind and you go where you want";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_HeInsideOtherWord_NotMatched()
    {
        var input = "she said history matters and we should reflect on this";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_ContactNameInsideOtherWord_NotMatched()
    {
        var input = "that voice note was remarkable, you nailed it";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_PetNameVocative_NotChanged()
    {
        var input = "hey baby, just walked into work and the day is already weird";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_SecondPersonOnly_NotChanged()
    {
        var input = "you're so sweet for sending that voice note. love you.";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Rewrite_EmptyOrWhitespace_ReturnedAsIs(string input)
    {
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_NoContactName_StillHandlesPronouns()
    {
        var result = DirectAddressRewriter.Rewrite(
            "he was working late and i missed him",
            string.Empty);

        result.Should().Be("you were working late and i missed you");
    }

    [Theory]
    [InlineData("you guys going to the store")]
    [InlineData("you all need to listen")]
    [InlineData("you both look tired")]
    [InlineData("you ladies are unstoppable")]
    public void Rewrite_PluralNounAfterYou_NotConjugated(string input)
    {
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_HeAndHis_BothConverted()
    {
        var result = DirectAddressRewriter.Rewrite(
            "he and his coworker stopped by",
            "Mark");

        result.Should().Be("you and your coworker stopped by");
    }

    [Fact]
    public void Rewrite_PronounAndContactName_BothConverted()
    {
        var result = DirectAddressRewriter.Rewrite(
            "he is funny — Mark always cracks me up",
            "Mark");

        result.Should().Be("you are funny — you always crack me up");
    }
}
