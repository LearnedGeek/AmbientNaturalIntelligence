using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P4 (2026-08-22) — verifies
/// <see cref="PromptBuilder.FormatMemoryWithTime"/> renders attribution
/// alongside provenance per the design plan:
///
/// <c>[FROM: &lt;source&gt; | AUTHORED: &lt;actor&gt; (| TRUST: &lt;t&gt;)] (&lt;temporal&gt;) &lt;content&gt;</c>
///
/// TRUST segment is omitted when verified (default assumption reduces
/// prompt noise). Non-verified trust values render explicitly so composer
/// LLMs weight the record accordingly — the 12:04-shape corruption class
/// (Interior records with pre-F-2 embedded "you said X" claims) is
/// flagged this way via <c>TRUST: unverified-historical</c>.
/// </summary>
public class AttributionRenderingTests
{
    private static MemoryRecord Sample(
        AttributedTo attributedTo,
        string       trust,
        string       sourceName = "twilio-inbound",
        EpistemicTier provenance = EpistemicTier.Facts) => new()
    {
        Content          = "hey babe, back from teaching",
        OccurredAt       = DateTimeOffset.UtcNow.AddMinutes(-10),
        Provenance       = provenance,
        SourceName       = sourceName,
        AttributedTo     = attributedTo,
        AttributionTrust = trust,
    };

    [Fact]
    public void MarkVerified_RendersFromPlusAuthored_TrustOmitted()
    {
        var record = Sample(AttributedTo.Mark, trust: "verified");

        var rendered = PromptBuilder.FormatMemoryWithTime(record, DateTimeOffset.UtcNow, contactName: "Mark");

        rendered.Should().StartWith("[FROM: text from Mark | AUTHORED: Mark] ",
            "verified trust is the default assumption — TRUST segment omitted to reduce prompt noise");
    }

    [Fact]
    public void AniVerified_RendersAuthoredAni_TrustOmitted()
    {
        var record = Sample(AttributedTo.Ani, trust: "verified", sourceName: null,
                             provenance: EpistemicTier.Episodic);

        var rendered = PromptBuilder.FormatMemoryWithTime(record, DateTimeOffset.UtcNow);

        rendered.Should().StartWith("[FROM: conversation | AUTHORED: Ani] ");
    }

    [Fact]
    public void InteriorAniUnverifiedHistorical_RendersTrustExplicitly()
    {
        // 12:04-shape corruption class: Interior record whose internal
        // content claims cannot be retroactively verified. TRUST segment
        // present so composer LLMs weight this record with caution.
        var record = Sample(
            AttributedTo.Ani,
            trust: "unverified-historical",
            sourceName: null,
            provenance: EpistemicTier.Interior);

        var rendered = PromptBuilder.FormatMemoryWithTime(record, DateTimeOffset.UtcNow);

        rendered.Should().StartWith("[FROM: your prior thought | AUTHORED: Ani | TRUST: unverified-historical] ",
            "unverified-historical trust flags the 12:04 substrate-feedback class explicitly");
    }

    [Fact]
    public void UnknownUnverified_RendersBothExplicitly()
    {
        // Manual-curation tail: heuristic couldn't infer author, trust
        // stays default 'unverified'. Composer sees both explicit tags.
        var record = Sample(
            AttributedTo.Unknown,
            trust: "unverified",
            sourceName: null,
            provenance: EpistemicTier.Episodic);

        var rendered = PromptBuilder.FormatMemoryWithTime(record, DateTimeOffset.UtcNow);

        rendered.Should().StartWith("[FROM: conversation | AUTHORED: unknown | TRUST: unverified] ");
    }

    [Fact]
    public void WorldSource_RendersAuthoredWorld()
    {
        var record = new MemoryRecord
        {
            Content          = "[NPR News] West Virginia paid digital nomads to move there during COVID",
            OccurredAt       = DateTimeOffset.UtcNow.AddHours(-2),
            Provenance       = EpistemicTier.Facts,
            SourceName       = "rss",
            AttributedTo     = AttributedTo.World,
            AttributionTrust = "verified",
        };

        var rendered = PromptBuilder.FormatMemoryWithTime(record, DateTimeOffset.UtcNow);

        rendered.Should().StartWith("[FROM: news | AUTHORED: world] ",
            "world sources (RSS, weather, etc.) render as AUTHORED: world");
    }

    [Theory]
    [InlineData(AttributedTo.Mark,    "Mark")]
    [InlineData(AttributedTo.Ani,     "Ani")]
    [InlineData(AttributedTo.World,   "world")]
    [InlineData(AttributedTo.Unknown, "unknown")]
    public void FormatAuthor_MapsEnumToLabel(AttributedTo attr, string expected)
    {
        // Direct test of the label mapping. Pinned so a future enum
        // extension (Contact-other, Composite, etc.) has to also decide
        // what its rendered label is.
        PromptBuilder.FormatAuthor(attr).Should().Be(expected);
    }

    [Fact]
    public void ContentAndTemporalPreserved_AttributionAddedInsideTag()
    {
        // Regression: the attribution segment must live INSIDE the [FROM: ...]
        // brackets, not between the bracket and the temporal phrase. Content
        // must still appear after the closing bracket + temporal phrase.
        var record = Sample(AttributedTo.Mark, trust: "verified");

        var rendered = PromptBuilder.FormatMemoryWithTime(record, DateTimeOffset.UtcNow, contactName: "Mark");

        rendered.Should().Contain("] (just now) hey babe, back from teaching",
            "attribution segment must live INSIDE the brackets; content and temporal shape unchanged");
    }
}
