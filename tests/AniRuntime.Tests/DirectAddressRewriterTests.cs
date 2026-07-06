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
///
/// **K.4c (2026-07-06) — pronoun-swap retirement.** The rewriter used to
/// blindly swap <c>he/him/his</c> → <c>you/your</c> under the assumption
/// that any male pronoun in Ani's output referred to Mark. That assumption
/// held on the substrate-heavy composer path where third-party references
/// were rare. It broke on the lean composer path (K.1/K.2/K.3) where the
/// fine-tune corpus routinely references third parties (Bob Ross, Karen,
/// Sarah, historical figures). Empirical anchor: 2026-07-06 13:28 CDT
/// "bob ross would've loved this sky. he'd have dipped his brush..." →
/// rewriter transformed to "you'd have dipped your brush", breaking the
/// third-party reference. A test-harness run against ani-v7-conversation
/// + Modelfile SYSTEM confirmed the fine-tune handles third-party
/// he/him/his correctly on its own; the rewriter's swap was causing more
/// harm than help. Kept: proper-noun swap for the contact name, which the
/// harness confirmed IS still needed (model sometimes writes "mark's desk"
/// when Mark is the addressee).
///
/// The pronoun-related tests below are inverted from their prior shape —
/// they now assert that pronouns are PRESERVED, catching future
/// regressions where someone tries to reintroduce the blind swap.
/// </summary>
public class DirectAddressRewriterTests
{
    // ─── Contact-name proper-noun swap: KEPT and pinned ──────────────────

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
    public void Rewrite_AdverbBetweenSubjectAndVerb_StillConjugates()
    {
        DirectAddressRewriter.Rewrite("Mark always cracks me up", "Mark")
            .Should().Be("you always crack me up");
    }

    [Fact]
    public void Rewrite_ContactNameInsideOtherWord_NotMatched()
    {
        var input = "that voice note was remarkable, you nailed it";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    // ─── Pronoun swap: REMOVED — pinned to prevent regression ────────────

    [Fact]
    public void Rewrite_He_Preserved_ModelHandlesThirdPartyPronouns()
    {
        // K.4c: "he" is left alone. If it refers to Mark, the direct-
        // address invariant will still flag it at the gate; the rewriter
        // no longer guesses.
        var input = "hahaha he thinks it's our company song";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_His_Preserved_ModelHandlesThirdPartyPronouns()
    {
        var input = "his desk is right next to mine";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_Him_Preserved_ModelHandlesThirdPartyPronouns()
    {
        var input = "i was thinking about him today";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_ThirdPartyPronoun_Preserved_BobRossCase()
    {
        // The empirical anchor for K.4c. Bob Ross reference must survive.
        var input = "bob ross would've loved this sky. he'd have dipped his brush in titanium white";
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }

    [Fact]
    public void Rewrite_ContactNameAndThirdPartyPronoun_OnlyContactNameConverted()
    {
        // Rewriter swaps the proper noun; the "he" reference to a third
        // party is preserved.
        var result = DirectAddressRewriter.Rewrite(
            "he is funny — Mark always cracks me up",
            "Mark");

        result.Should().Be("he is funny — you always crack me up");
    }

    [Fact]
    public void Rewrite_NoContactName_PronounsPreserved()
    {
        // K.4c: no contact name provided AND no pronoun swap = no changes.
        var result = DirectAddressRewriter.Rewrite(
            "he was working late and i missed him",
            string.Empty);

        result.Should().Be("he was working late and i missed him");
    }

    // ─── Verb-agreement pass on you-{VERB} (kept for ContactName path) ──

    [Fact]
    public void Rewrite_ContactNameAsSubject_TriggersVerbAgreement_Irregular()
    {
        DirectAddressRewriter.Rewrite("Mark is here", "Mark")
            .Should().Be("you are here");
    }

    [Fact]
    public void Rewrite_ContactNameAsSubject_TriggersVerbAgreement_Regular()
    {
        DirectAddressRewriter.Rewrite("Mark tries hard", "Mark")
            .Should().Be("you try hard");
    }

    [Fact]
    public void Rewrite_ContactNameAsSubject_TriggersVerbAgreement_Sibilant()
    {
        DirectAddressRewriter.Rewrite("Mark watches the show", "Mark")
            .Should().Be("you watch the show");
    }

    // ─── No-op / boundary cases ──────────────────────────────────────────

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

    [Theory]
    [InlineData("you guys going to the store")]
    [InlineData("you all need to listen")]
    [InlineData("you both look tired")]
    [InlineData("you ladies are unstoppable")]
    public void Rewrite_PluralNounAfterYou_NotConjugated(string input)
    {
        DirectAddressRewriter.Rewrite(input, "Mark").Should().Be(input);
    }
}
