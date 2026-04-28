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
}
