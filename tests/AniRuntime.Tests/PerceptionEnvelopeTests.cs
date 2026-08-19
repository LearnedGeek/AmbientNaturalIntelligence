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
            Summary    = "hey babe, back from teaching",
            OccurredAt = now.AddMinutes(-5),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p, now, contactName: "Mark");

        rendered.Should().Contain("You received a text from Mark",
            "twilio-inbound with a contactName uses the configured name");
        rendered.Should().Contain("5m ago");
        rendered.Should().Contain(p.Summary);
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
    [InlineData("weather",              "Weather right now")]
    [InlineData("rss",                  "News right now")]
    [InlineData("time",                 "Time-of-day right now")]
    [InlineData("register-saturation",  "Interior signal — register saturation")]
    [InlineData("retrieval-self-dominance", "Interior signal — self-dominance in retrieval")]
    [InlineData("outage",               "World-quiet signal")]
    public void FormatPerceptionLine_KnownSources_RenderWithSourceSpecificFrame(
        string source, string expectedFrame)
    {
        var p = new PerceptionEvent
        {
            SourceName = source,
            Summary    = "test summary",
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-3),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p);
        rendered.Should().Contain(expectedFrame,
            $"source '{source}' should render with framing '{expectedFrame}'");
        rendered.Should().Contain("test summary");
    }

    [Fact]
    public void FormatPerceptionLine_JustNow_UsesJustNowPhrase()
    {
        var now = new DateTimeOffset(2026, 08, 19, 10, 0, 0, TimeSpan.Zero);
        var p = new PerceptionEvent
        {
            SourceName = "weather",
            Summary    = "clear",
            OccurredAt = now.AddSeconds(-30),
        };

        var rendered = PromptBuilder.FormatPerceptionLine(p, now);
        rendered.Should().Contain("Weather right now");
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
