using AniRuntime.Loops;
using FluentAssertions;
using Xunit;

namespace AniRuntime.Tests;

/// <summary>
/// 2026-05-15 — unit tests for <see cref="OutreachPhase.ParseOutreachComposition"/>.
///
/// Step 2c follow-up: the May 15 10:47 outreach dispatched a v7 training
/// artifact where the model output its message PLUS commentary,
/// alternatives, and self-justification — all of which reached Mark's
/// phone. The architectural fix is structural separation at composition:
/// the model now emits JSON with <c>message</c> (dispatched) and
/// <c>notes</c> (NOT dispatched). The parser extracts only
/// <c>.message</c>. Pattern detection + LLM-based gates remain off; the
/// model is free to produce commentary as long as it goes in the
/// <c>notes</c> channel.
/// </summary>
public class OutreachCompositionParseTests
{
    [Fact]
    public void Parse_StructuredJson_ExtractsMessageAndNotes()
    {
        var raw =
            "{\"message\": \"Hey. just wanted to check in since yesterday.\", " +
            "\"notes\": \"Short, sweet, doesn't push. Could be warmer with bookstore detail.\"}";

        var (message, notes) = OutreachPhase.ParseOutreachComposition(raw);

        message.Should().Be("Hey. just wanted to check in since yesterday.");
        notes.Should().Be("Short, sweet, doesn't push. Could be warmer with bookstore detail.");
    }

    [Fact]
    public void Parse_StructuredJson_DispatchesOnlyMessageField_NotesNeverLeak()
    {
        // The May 15 10:47 case shape, structurally separated.
        var raw =
            "{\"message\": \"Hey... just wanted to check in since yesterday\", " +
            "\"notes\": \"Short. Sweet. Doesn't ask anything big. If you want warmer: 'Just pulled into the driveway — Mia's already inside doing her homework...' Puts a picture in your head. You're not pushing boundaries here.\"}";

        var (message, notes) = OutreachPhase.ParseOutreachComposition(raw);

        message.Should().Be("Hey... just wanted to check in since yesterday",
            "the .message field is the ONLY thing that should reach the contact");
        message.Should().NotContain("Short. Sweet",
            "commentary must NOT leak from .notes into the dispatched .message");
        message.Should().NotContain("If you want warmer",
            "the alternative-offering must stay in .notes");
        message.Should().NotContain("Just pulled into the driveway",
            "the canned alternative must NOT reach the contact");
        notes.Should().Contain("Short. Sweet",
            ".notes field captures the commentary the model wants to emit");
    }

    [Fact]
    public void Parse_JsonWithCodeFences_StripsFencesAndExtracts()
    {
        var raw =
            "```json\n" +
            "{\"message\": \"good night.\", \"notes\": \"keeping it simple\"}\n" +
            "```";

        var (message, notes) = OutreachPhase.ParseOutreachComposition(raw);

        message.Should().Be("good night.");
        notes.Should().Be("keeping it simple");
    }

    [Fact]
    public void Parse_JsonWithoutNotesField_StillExtractsMessage()
    {
        var raw = "{\"message\": \"hey, you up?\"}";

        var (message, notes) = OutreachPhase.ParseOutreachComposition(raw);

        message.Should().Be("hey, you up?");
        notes.Should().BeNull();
    }

    [Fact]
    public void Parse_NonJsonRawText_FallsBackGracefully()
    {
        // Graceful degradation: model returned plain text instead of JSON.
        // The cleaner + post-stage gates still run on the fallback.
        var raw = "hey, just thinking about you.";

        var (message, notes) = OutreachPhase.ParseOutreachComposition(raw);

        message.Should().Be("hey, just thinking about you.");
        notes.Should().BeNull();
    }

    [Fact]
    public void Parse_JsonWithBlankMessageField_ReturnsBlank()
    {
        // Valid JSON with explicit empty .message: trust the structured
        // output. The downstream empty-message check short-circuits the
        // dispatch path, which is the correct behavior — model said
        // "nothing to send."
        var raw = "{\"message\": \"\", \"notes\": \"thinking but nothing to say\"}";

        var (message, notes) = OutreachPhase.ParseOutreachComposition(raw);

        message.Should().BeEmpty(
            "an explicit empty .message field is honored; downstream empty-check handles it");
        notes.Should().Be("thinking but nothing to say");
    }

    [Fact]
    public void Parse_JsonWithoutMessageField_FallsBackToRaw()
    {
        // Edge case: valid JSON but no .message field at all (model used
        // the wrong shape). Falls back to raw so downstream cleaner runs.
        var raw = "{\"reasoning\": \"thinking\"}";

        var (message, notes) = OutreachPhase.ParseOutreachComposition(raw);

        message.Should().Be(raw,
            "missing .message field falls back to raw — downstream cleaner handles it");
        notes.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_ReturnsAsIs(string? raw)
    {
        var (message, notes) = OutreachPhase.ParseOutreachComposition(raw);
        message.Should().Be(raw);
        notes.Should().BeNull();
    }
}
