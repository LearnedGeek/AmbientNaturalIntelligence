using AniRuntime.Voice;
using FluentAssertions;

namespace AniRuntime.Tests;

public class TokenBufferTests
{
    [Fact]
    public void Add_SingleSentence_YieldsOnPeriod()
    {
        var buffer = new TokenBuffer();

        buffer.Add("Hello").Should().BeNull();
        buffer.Add(" world").Should().BeNull();
        var result = buffer.Add(".");

        result.Should().Be("Hello world.");
    }

    [Fact]
    public void Add_ExclamationMark_YieldsSentence()
    {
        var buffer = new TokenBuffer();

        buffer.Add("Wow").Should().BeNull();
        var result = buffer.Add("!");

        result.Should().Be("Wow!");
    }

    [Fact]
    public void Add_QuestionMark_YieldsSentence()
    {
        var buffer = new TokenBuffer();

        buffer.Add("How are you").Should().BeNull();
        var result = buffer.Add("?");

        result.Should().Be("How are you?");
    }

    [Fact]
    public void Add_Ellipsis_DoesNotTriggerEarlyYield()
    {
        var buffer = new TokenBuffer();

        buffer.Add("Well").Should().BeNull();
        buffer.Add("...").Should().BeNull();
        buffer.Add(" I think").Should().BeNull();
        var result = buffer.Add(".");

        result.Should().Be("Well... I think.");
    }

    [Fact]
    public void Add_WordOverflow_ForcesYield()
    {
        var buffer = new TokenBuffer(maxWords: 5);

        buffer.Add("one ").Should().BeNull();
        buffer.Add("two ").Should().BeNull();
        buffer.Add("three ").Should().BeNull();
        buffer.Add("four ").Should().BeNull();
        var result = buffer.Add("five six ");

        result.Should().NotBeNull();
        result.Should().Contain("one");
    }

    [Fact]
    public void Add_EmDash_YieldsSentence()
    {
        var buffer = new TokenBuffer();

        buffer.Add("Wait").Should().BeNull();
        var result = buffer.Add("—");

        result.Should().Be("Wait—");
    }

    [Fact]
    public void Add_EmptyToken_ReturnsNull()
    {
        var buffer = new TokenBuffer();

        buffer.Add("").Should().BeNull();
        buffer.Add(null!).Should().BeNull();
    }

    [Fact]
    public void Flush_ReturnsRemainingText()
    {
        var buffer = new TokenBuffer();

        buffer.Add("partial sentence");
        var result = buffer.Flush();

        result.Should().Be("partial sentence");
    }

    [Fact]
    public void Flush_EmptyBuffer_ReturnsNull()
    {
        var buffer = new TokenBuffer();
        buffer.Flush().Should().BeNull();
    }

    [Fact]
    public void Add_MultipleSentences_YieldsEachIndependently()
    {
        var buffer = new TokenBuffer();

        var first = buffer.Add("First.");
        first.Should().Be("First.");

        var second = buffer.Add("Second.");
        second.Should().Be("Second.");
    }

    [Fact]
    public void Add_DoubleDash_YieldsSentence()
    {
        var buffer = new TokenBuffer();

        buffer.Add("Something").Should().BeNull();
        var result = buffer.Add("--");

        result.Should().Be("Something--");
    }
}
