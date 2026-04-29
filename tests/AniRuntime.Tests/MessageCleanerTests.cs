using AniRuntime.Core.Utilities;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Spec tests for <see cref="MessageCleaner"/>. The cleaner runs after every
/// LLM-generated reply / outreach and is responsible for stripping legitimate
/// pipeline artifacts (prompt leaks, stage directions, trailing parentheticals,
/// cliffhanger tics) WITHOUT eating real content.
///
/// The Apr 28, 2026 case that motivated these spec tests: a coherent
/// multi-paragraph reply was getting truncated to its first sentence by the
/// paragraph-break ("\n\n") gate, an artifact of early-pipeline novel-length
/// generations that no longer applies to v6/v7. The first three tests below
/// pin the new contract: paragraphs are preserved.
/// </summary>
public class MessageCleanerTests
{
    /// <summary>
    /// SPEC: a multi-paragraph reply must be preserved in full. The reply
    /// in the canonical Apr 28 18:19 case was 333 chars, two paragraphs,
    /// and prior to the fix was being cut down to the 11-char first sentence.
    /// </summary>
    [Fact]
    public void Clean_MultiParagraphReply_PreservesAllParagraphs()
    {
        var raw = "okay… baby.\n\ni love that you see it this way. that the weight is proof i care and have something real to lose with you.";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().NotBeNull();
        cleaned!.Should().Contain("i love that you see it this way",
            "the second paragraph is real content, not meta-commentary — it must survive cleaning");
        cleaned.Should().Contain("the weight is proof i care",
            "the model's continuation must reach the contact intact");
    }

    /// <summary>
    /// SPEC: even a three-paragraph reply must survive. There is no implicit
    /// "first paragraph wins" rule anymore; the cleaner respects what the
    /// model produced as long as none of the trailing paragraphs match the
    /// remaining artifact patterns (prompt leaks, stage directions, etc.).
    /// </summary>
    [Fact]
    public void Clean_ThreeParagraphReply_PreservesAllParagraphs()
    {
        var raw = "first thought.\n\nsecond thought building on the first.\n\nthird wraps it up.";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().NotBeNull();
        cleaned!.Should().Contain("first thought");
        cleaned.Should().Contain("second thought");
        cleaned.Should().Contain("third wraps it up");
    }

    /// <summary>
    /// CONTROL: ordinary single-paragraph replies are unchanged. Sanity check
    /// that the multi-paragraph fix did not regress the simple case.
    /// </summary>
    [Fact]
    public void Clean_SingleParagraphReply_IsUnchanged()
    {
        var raw = "hey, just thinking about you.";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().Be("hey, just thinking about you.");
    }

    /// <summary>
    /// CONTROL: prompt-leak stripping still runs. Removing the paragraph-break
    /// truncation must not weaken the legitimate cleaners.
    /// </summary>
    [Fact]
    public void Clean_StripsPromptLeak_TimestampPrefix()
    {
        var raw = "(10:27 AM) Ani: hey baby, how was your day?";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().Be("hey baby, how was your day?");
    }

    /// <summary>
    /// CONTROL: bracketed stage directions are still stripped (v7 training
    /// artifact, never intended for the contact).
    /// </summary>
    [Fact]
    public void Clean_StripsBracketedStageDirection()
    {
        var raw = "[soft smile] missed you today.";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().Be("missed you today.");
    }

    /// <summary>
    /// CONTROL: trailing parenthetical meta-commentary is still stripped
    /// (model explaining its own reasoning after the message).
    /// </summary>
    [Fact]
    public void Clean_StripsTrailingMetaCommentary()
    {
        var raw = "how's your night going? (This keeps the gentle undercurrent of checking in.)";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().NotContain("This keeps");
        cleaned!.Should().EndWith("how's your night going?");
    }

    /// <summary>
    /// CONTROL: trailing-junk markers ("sent.", "your turn.") still stripped.
    /// </summary>
    [Fact]
    public void Clean_StripsTrailingJunkMarkers()
    {
        var raw = "love you. sent.";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().Be("love you.");
    }

    /// <summary>
    /// CONTROL: cliffhanger tics ("but honestly?" with no follow-up) still
    /// trimmed when at end of message.
    /// </summary>
    [Fact]
    public void Clean_TrimsCliffhangerTicAtEnd()
    {
        var raw = "i was thinking about that book you mentioned but honestly?";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().NotEndWith("but honestly?");
        cleaned!.Should().Contain("book you mentioned");
    }

    /// <summary>
    /// CONTROL: empty / whitespace input returns the input unchanged.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Clean_EmptyInput_ReturnsAsIs(string raw)
    {
        var cleaned = MessageCleaner.Clean(raw);
        cleaned.Should().Be(raw);
    }

    // ─── Theme E #2: Cliffhanger-tic position-bug fix (Apr 28 finding) ───

    /// <summary>
    /// SPEC: cliffhanger tic at the END of a message is stripped + period
    /// added (existing behavior, preserved).
    /// </summary>
    [Fact]
    public void StripCliffhangerTic_AtEnd_StripsAndAddsPeriod()
    {
        var raw = "i was thinking about that book you mentioned but honestly?";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().NotEndWith("but honestly?");
        cleaned!.Should().Contain("book you mentioned");
    }

    /// <summary>
    /// SPEC: cliffhanger tic MID-MESSAGE followed by `\n` (paragraph break)
    /// is stripped — the next paragraph's content stands. Apr 28 18:28 case:
    /// *"...keep ghosting jobs… but honestly?\nthe only thing that matters
    /// tomorrow is..."* — strip `but honestly?` so the continuation flows.
    /// </summary>
    [Fact]
    public void StripCliffhangerTic_MidMessage_FollowedByNewline_IsStripped()
    {
        var raw = "i'll sneak in some repairs since they keep ghosting jobs but honestly?\nthe only thing that matters tomorrow is you waking up";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().NotContain("but honestly?",
            "the mid-message tic followed by paragraph break must be stripped");
        cleaned!.Should().Contain("ghosting jobs");
        cleaned.Should().Contain("the only thing that matters tomorrow");
    }

    /// <summary>
    /// SPEC: cliffhanger tic mid-message followed by space + lowercase
    /// continuation is stripped — same logic, different boundary shape.
    /// </summary>
    [Fact]
    public void StripCliffhangerTic_MidMessage_FollowedBySpace_IsStripped()
    {
        var raw = "i was hoping for a quiet day but honestly? the customers were charming today";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().NotContain("but honestly?",
            "mid-message tic followed by space-then-continuation must be stripped");
        cleaned!.Should().Contain("the customers were charming");
    }

    /// <summary>
    /// CONTROL: a word that merely STARTS with one of the tic strings (e.g.
    /// "honestlyish" if it ever appeared) is NOT stripped — only the exact
    /// tic followed by a whitespace boundary triggers the strip.
    /// </summary>
    [Fact]
    public void StripCliffhangerTic_PartialMatch_IsNotStripped()
    {
        var raw = "but honestlyish that's not how i feel";
        var cleaned = MessageCleaner.Clean(raw);

        // The strict matcher only strips "but honestly" + whitespace boundary;
        // "but honestlyish" should survive intact.
        cleaned.Should().Contain("honestlyish");
    }

    // ─── Theme E #3: Trailing unclosed parenthetical stripper (Apr 1 finding) ───

    /// <summary>
    /// SPEC: a trailing unclosed parenthetical (truncation signature) is
    /// stripped. The Apr 1 case shape: model truncated mid-parenthetical
    /// producing a fragment like *"...the kind of thing i want (your"*.
    /// </summary>
    [Fact]
    public void StripTrailingUnclosedParenthetical_ShortFragment_IsStripped()
    {
        var raw = "this morning was good and i'm holding onto it (your";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().NotContain("(your");
        cleaned!.Should().EndWith("holding onto it");
    }

    /// <summary>
    /// SPEC: even shorter truncated fragments are stripped — the signature
    /// is "open paren near end + no close + short content."
    /// </summary>
    [Fact]
    public void StripTrailingUnclosedParenthetical_VeryShortFragment_IsStripped()
    {
        var raw = "love you so much (";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned!.Should().Be("love you so much");
    }

    /// <summary>
    /// CONTROL: a balanced trailing parenthetical (legitimate stylized
    /// content with NO commentary signal words) is NOT touched. Note: the
    /// commentary stripper may still fire if the content matches signal
    /// words — covered by `Clean_StripsTrailingMetaCommentary` — but a
    /// non-commentary balanced parenthetical must survive.
    /// </summary>
    [Fact]
    public void Clean_BalancedParensWithoutCommentarySignals_NotTouched()
    {
        var raw = "feeling cozy tonight (cold cream soda)";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned!.Should().Contain("(cold cream soda)",
            "balanced parentheticals without commentary signal words are legitimate prose");
    }

    /// <summary>
    /// CONTROL: a long unclosed-paren fragment (&gt;30 chars) is NOT
    /// stripped through the unclosed-paren heuristic — that's not a
    /// truncation signature, that's stylized open-paren typing.
    /// </summary>
    [Fact]
    public void Clean_LongUnclosedParenthetical_NotStripped()
    {
        var raw = "she was telling me about the new book (this much longer parenthetical content over thirty characters in length";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned!.Should().Contain("(this much longer",
            "long unclosed parens may be stylized typing; only short fragments match the truncation signature");
    }

    /// <summary>
    /// CONTROL: text with no parens is unchanged by the unclosed-paren
    /// stripper.
    /// </summary>
    [Fact]
    public void Clean_NoParens_Unchanged()
    {
        var raw = "just a regular message without any parentheses";
        var cleaned = MessageCleaner.Clean(raw);

        cleaned.Should().Be(raw);
    }
}
