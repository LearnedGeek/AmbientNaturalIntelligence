using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 6 (2026-08-19) — verifies
/// <see cref="PerceptionEvent"/> implements <see cref="IPerceptionEnvelope"/>
/// correctly and that <see cref="PromptBuilder.FormatPerceptionLine"/>
/// splits the pre-Phase-6 flat (Background: ...) blob into per-source
/// framing lines that distinguish inbound texts from weather from
/// interior signals.
/// </summary>
public class PerceptionEnvelopeTests
{
    // ── Envelope contract on PerceptionEvent ─────────────────────────────

    [Fact]
    public void PerceptionEvent_ImplementsPerceptionEnvelope_ExposesSelfAsContent()
    {
        var p = new PerceptionEvent
        {
            Summary    = "the sky just went pink",
            SourceName = "weather",
            Category   = PerceptionCategory.Environment,
        };
        IPerceptionEnvelope env = p;

        env.Content.Should().BeSameAs(p, "envelope wraps the full PerceptionEvent — Content is self-reference");
        env.Category.Should().Be(PerceptionCategory.Environment);
        env.Producer.Should().Be("PerceptionSource:weather");
    }

    [Theory]
    [InlineData(PerceptionCategory.Communication, "twilio-inbound", "perception.communication.twilio-inbound")]
    [InlineData(PerceptionCategory.Environment,   "weather",        "perception.environment.weather")]
    [InlineData(PerceptionCategory.Content,       "rss",            "perception.content.rss")]
    [InlineData(PerceptionCategory.Internal,      "register-saturation", "perception.internal.register-saturation")]
    public void PerceptionEvent_SourceType_ComposesFromCategoryAndSourceName(
        PerceptionCategory cat, string source, string expected)
    {
        IProvenancedContent<PerceptionEvent> env = new PerceptionEvent
        {
            Category   = cat,
            SourceName = source,
        };
        env.SourceType.Should().Be(expected);
    }

    [Fact]
    public void PerceptionEvent_CreatedAt_MapsToOccurredAt()
    {
        var t = DateTimeOffset.Parse("2026-08-19T10:00:00Z");
        IProvenancedContent<PerceptionEvent> env = new PerceptionEvent { OccurredAt = t };
        env.CreatedAt.Should().Be(t);
    }

    [Fact]
    public void PerceptionEvent_Producer_IncludesSourceName()
    {
        IProvenancedContent<PerceptionEvent> env = new PerceptionEvent { SourceName = "weather" };
        env.Producer.Should().Be("PerceptionSource:weather",
            "producer identity carries the specific source so downstream can distinguish RSS from Weather from Twilio");
    }

    // ── FormatPerceptionLine — per-source, per-category framing ─────────

    [Fact]
    public void FormatPerceptionLine_TwilioInbound_WithContactName_NamesTheContact()
    {
        var now = new DateTimeOffset(2026, 08, 19, 10, 0, 0, TimeSpan.Zero);
        var p = new PerceptionEvent
        {
            Category   = PerceptionCategory.Communication,
            SourceName = "twilio-inbound",
            // Summary carries the "{contact} texted: \"...\"" prefix set by
            // MemoryPrefixes.FormatContactPerception at the perception source.
            Summary    = "Mark texted: \"hey babe, back from teaching\"",
            OccurredAt = now.AddMinutes(-5),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p, now, contactName: "Mark");

        rendered.Should().Contain("You received a text from Mark",
            "twilio-inbound with a contactName uses the configured name");
        rendered.Should().Contain("5m ago");
        // PR #116 review (Devin) — the summary prefix "Mark texted: " must
        // be stripped when the frame already names the contact, so the
        // rendered form doesn't double-attribute.
        rendered.Should().NotContain("Mark texted:",
            "double-attribution: framing names the contact, so the redundant summary prefix must be stripped");
        rendered.Should().Contain("hey babe, back from teaching",
            "the actual message body still appears after the frame + colon");
    }

    [Fact]
    public void FormatPerceptionLine_TwilioInbound_UsesSnapshotBuiltAtWhenPassed()
    {
        // PR #116 review (Serge + Devin) — regression that the caller threads
        // snapshot.BuiltAt through so all temporal renderings in the prompt
        // use one consistent clock.
        var builtAt = new DateTimeOffset(2026, 08, 19, 10, 0, 0, TimeSpan.Zero);
        var p = new PerceptionEvent
        {
            Category   = PerceptionCategory.Communication,
            SourceName = "twilio-inbound",
            Summary    = "Mark texted: \"hi\"",
            OccurredAt = builtAt.AddMinutes(-12),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p, builtAt, contactName: "Mark");
        rendered.Should().Contain("12m ago",
            "when snapshot.BuiltAt is passed, age is computed against it — deterministic and consistent with other temporal lines in the prompt");
    }

    // PR #116 review-fix regression coverage for StripContactTextedPrefix.
    [Theory]
    [InlineData("Mark texted: \"hi\"",     "Mark",  "\"hi\"")]
    [InlineData("Mark texted: \"multi word body\"", "Mark", "\"multi word body\"")]
    [InlineData("Kathy texted: \"hey\"",   "Kathy", "\"hey\"")]
    // Contact-name at record time differs from currently-configured contact
    // — permissive strip via the "{word} texted: " marker still fires.
    [InlineData("Mia texted: \"body\"",    "Mark",  "\"body\"")]
    [InlineData("Mia texted: \"body\"",    null,    "\"body\"")]
    // Non-word prefix (multi-word contact name, whitespace, etc.) is NOT
    // stripped — preserves summary integrity for edge cases.
    [InlineData("Not a texted prefix",     "Mark",  "Not a texted prefix")]
    public void StripContactTextedPrefix_StripsPrefixWhenPresent(
        string input, string? contactName, string expected)
    {
        PromptBuilder.StripContactTextedPrefix(input, contactName).Should().Be(expected);
    }

    [Fact]
    public void FormatPerceptionLine_TwilioInbound_WithoutContactName_UsesNeutralPhrasing()
    {
        var now = new DateTimeOffset(2026, 08, 19, 10, 0, 0, TimeSpan.Zero);
        var p = new PerceptionEvent
        {
            Category   = PerceptionCategory.Communication,
            SourceName = "twilio-inbound",
            Summary    = "hey babe",
            OccurredAt = now.AddMinutes(-2),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p, now);  // no contactName

        rendered.Should().Contain("inbound text",
            "neutral phrasing when contactName is not threaded through — never hardcode 'Mark'");
        rendered.Should().NotContain("Mark");
    }

    [Theory]
    // PR #116 review (Devin BUG) — rss/weather/time frames now carry age
    // rather than the hardcoded "right now" phrase. Stale RSS items no
    // longer render as fresh events.
    [InlineData("weather",              "Weather")]
    [InlineData("rss",                  "News")]
    [InlineData("time",                 "Time-of-day")]
    [InlineData("register-saturation",  "Interior signal — register saturation")]
    [InlineData("retrieval-self-dominance", "Interior signal — self-dominance in retrieval")]
    [InlineData("outage",               "World-quiet signal")]
    public void FormatPerceptionLine_KnownSources_RenderWithSourceSpecificFrame(
        string source, string expectedFrame)
    {
        var now = new DateTimeOffset(2026, 08, 19, 10, 0, 0, TimeSpan.Zero);
        var p = new PerceptionEvent
        {
            SourceName = source,
            Summary    = "test summary",
            OccurredAt = now.AddMinutes(-3),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p, now);
        rendered.Should().Contain(expectedFrame,
            $"source '{source}' should render with framing '{expectedFrame}'");
        rendered.Should().Contain("(3m ago)",
            "PR #116 fix — every source now carries age; hardcoded 'right now' was the same temporal-misattribution failure the PR set out to fix");
        rendered.Should().Contain("test summary");
    }

    // PR #116 review (Devin BUG) — regression for stale RSS.
    [Fact]
    public void FormatPerceptionLine_StaleRssItem_RendersWithAge_NotRightNow()
    {
        // Real production scenario: an RSS feed item published this morning
        // survives the _lastSeen filter and reaches the composer 4 hours
        // later. Pre-fix rendered "News right now"; post-fix carries age.
        var now = new DateTimeOffset(2026, 08, 19, 14, 0, 0, TimeSpan.Zero);
        var p = new PerceptionEvent
        {
            SourceName = "rss",
            Summary    = "[NPR] some article",
            OccurredAt = now.AddHours(-4),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p, now);
        rendered.Should().Contain("News (4h ago)");
        rendered.Should().NotContain("right now",
            "stale RSS items must NOT render as fresh — that was the pre-fix temporal-misattribution bug");
    }

    [Fact]
    public void FormatPerceptionLine_UnknownSource_UsesGenericPerceptionFrame()
    {
        var p = new PerceptionEvent
        {
            SourceName = "some-future-source",
            Summary    = "something happened",
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p);
        rendered.Should().Contain("Perception — some-future-source",
            "unknown sources fall through to the generic frame so new sources render sensibly before their mapping ships");
    }

    [Fact]
    public void FormatPerceptionLine_InternalCategory_UsesInteriorFrame()
    {
        var p = new PerceptionEvent
        {
            Category   = PerceptionCategory.Internal,
            SourceName = "some-new-internal-signal",
            Summary    = "self-observation",
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p);
        rendered.Should().StartWith("(Interior signal — some-new-internal-signal",
            "unknown Internal-category sources still get the interior framing rather than the generic Perception fallback");
    }
}
