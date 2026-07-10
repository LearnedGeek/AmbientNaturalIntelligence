using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// 2026-07-10 — pins the JSON-prefix extraction that lets ParseEmotionalShift
/// survive LLM output where the model emits valid JSON followed by an
/// Analysis: paragraph. Empirical anchor: 2026-07-10 10:26:57 debug log
/// where the 8B model returned <c>{ ... }  Analysis: Register: ...</c>
/// and strict <c>JsonDocument.Parse</c> failed, forcing a fallback to
/// zero-delta Unclassified for the whole cycle.
/// </summary>
public class EmotionalShiftParserTests
{
    [Fact]
    public void ExtractJsonPrefix_StripsTrailingProse()
    {
        var raw = """
            { "register": "Playfulness", "warmth": 0.93, "energy": -0.17, "worry": 0.69, "playfulness": -0.28, "severity": 1.0 }

            Analysis:
            Register: Playfulness
            The initial message from Mark is playful teasing about someone.
            """;

        var extracted = EmotionalProcessor.ExtractJsonPrefix(raw);

        extracted.Should().StartWith("{");
        extracted.Should().EndWith("}");
        extracted.Should().NotContain("Analysis:");
    }

    [Fact]
    public void ExtractJsonPrefix_HandlesFencedJson()
    {
        var raw = "```json\n{ \"register\": \"Warmth\", \"warmth\": 0.05 }\n```\nSome commentary.";
        var extracted = EmotionalProcessor.ExtractJsonPrefix(raw);
        extracted.Should().StartWith("{").And.EndWith("}");
    }

    [Fact]
    public void ExtractJsonPrefix_HandlesNestedObjects()
    {
        var raw = "{ \"a\": { \"b\": 1, \"c\": 2 }, \"d\": 3 } trailing text";
        var extracted = EmotionalProcessor.ExtractJsonPrefix(raw);
        extracted.Should().Be("{ \"a\": { \"b\": 1, \"c\": 2 }, \"d\": 3 }");
    }

    [Fact]
    public void ExtractJsonPrefix_HandlesStringsWithBraces()
    {
        // A JSON string literal containing '{' or '}' must NOT confuse the
        // balance tracker.
        var raw = "{ \"quote\": \"she said {maybe}\", \"n\": 1 }  \nAnalysis text";
        var extracted = EmotionalProcessor.ExtractJsonPrefix(raw);
        extracted.Should().Be("{ \"quote\": \"she said {maybe}\", \"n\": 1 }");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json at all just text")]
    public void ExtractJsonPrefix_NoObject_ReturnsInput(string input)
    {
        // Falls back to the whole string when no balanced object is found;
        // the caller's JsonDocument.Parse will surface the same error it
        // would have hit anyway.
        EmotionalProcessor.ExtractJsonPrefix(input).Should().Be(input);
    }
}
