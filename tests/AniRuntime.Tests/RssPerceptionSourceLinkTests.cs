using System.Xml;
using AniRuntime.Perception;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// 2026-09-12 — regression coverage for
/// <see cref="RssPerceptionSource.ExtractPreferredLink"/>. Prior to this,
/// the RSS Atom fallback simply took the first &lt;link&gt; child, which
/// on multi-link Atom entries (rel="self" / rel="enclosure" preceding
/// rel="alternate") stored a feed API or media URL rather than the article
/// URL that the downstream reactive-share source-persistence needs.
/// </summary>
public class RssPerceptionSourceLinkTests
{
    private static XmlNode ParseItem(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc.DocumentElement!;
    }

    [Fact]
    public void ExtractPreferredLink_Rss20_ReturnsElementText()
    {
        var item = ParseItem(
            "<item>" +
                "<title>Story</title>" +
                "<link>https://outlet.example/story-42</link>" +
            "</item>");

        RssPerceptionSource.ExtractPreferredLink(item)
            .Should().Be("https://outlet.example/story-42");
    }

    [Fact]
    public void ExtractPreferredLink_AtomSingleLink_ReturnsHref()
    {
        var item = ParseItem(
            "<entry>" +
                "<title>Story</title>" +
                "<link href=\"https://outlet.example/story-42\"/>" +
            "</entry>");

        RssPerceptionSource.ExtractPreferredLink(item)
            .Should().Be("https://outlet.example/story-42");
    }

    [Fact]
    public void ExtractPreferredLink_AtomMultiLink_PrefersAlternateOverSelfAndEnclosure()
    {
        // The scenario from Copilot's review comment: rel="self" appears
        // first, rel="enclosure" second, and rel="alternate" is the actual
        // article URL Mark needs to hand back when asked for the source.
        var item = ParseItem(
            "<entry>" +
                "<title>Story</title>" +
                "<link rel=\"self\" href=\"https://outlet.example/api/entries/42\"/>" +
                "<link rel=\"enclosure\" href=\"https://outlet.example/media/story-42.mp3\"/>" +
                "<link rel=\"alternate\" href=\"https://outlet.example/story-42\"/>" +
            "</entry>");

        RssPerceptionSource.ExtractPreferredLink(item)
            .Should().Be("https://outlet.example/story-42",
                "rel=\"alternate\" is the article URL per RFC 4287 §4.2.7");
    }

    [Fact]
    public void ExtractPreferredLink_AtomAlternateAndUnqualified_PrefersExplicitAlternate()
    {
        var item = ParseItem(
            "<entry>" +
                "<title>Story</title>" +
                "<link href=\"https://outlet.example/story-42-fallback\"/>" +
                "<link rel=\"alternate\" href=\"https://outlet.example/story-42\"/>" +
            "</entry>");

        RssPerceptionSource.ExtractPreferredLink(item)
            .Should().Be("https://outlet.example/story-42",
                "explicit rel=\"alternate\" wins over an unqualified link even if unqualified appears first");
    }

    [Fact]
    public void ExtractPreferredLink_AtomUnqualifiedOnly_ReturnsHref()
    {
        // Absence of rel means alternate per RFC 4287 §4.2.7 — treat the
        // unqualified link the same as rel="alternate" when no explicit
        // alternate is present.
        var item = ParseItem(
            "<entry>" +
                "<title>Story</title>" +
                "<link rel=\"self\" href=\"https://outlet.example/api/entries/42\"/>" +
                "<link href=\"https://outlet.example/story-42\"/>" +
            "</entry>");

        RssPerceptionSource.ExtractPreferredLink(item)
            .Should().Be("https://outlet.example/story-42");
    }

    [Fact]
    public void ExtractPreferredLink_AtomNonStandardRelOnly_ReturnsHref()
    {
        // Fallback path — feed uses only non-standard rel values (e.g.
        // rel="self" only). Still better to hand back that URL than nothing;
        // downstream will just render a less-useful source line but won't
        // silently drop the source record.
        var item = ParseItem(
            "<entry>" +
                "<title>Story</title>" +
                "<link rel=\"self\" href=\"https://outlet.example/api/entries/42\"/>" +
            "</entry>");

        RssPerceptionSource.ExtractPreferredLink(item)
            .Should().Be("https://outlet.example/api/entries/42");
    }

    [Fact]
    public void ExtractPreferredLink_NoLinks_ReturnsNull()
    {
        var item = ParseItem(
            "<item>" +
                "<title>Story</title>" +
                "<description>no link</description>" +
            "</item>");

        RssPerceptionSource.ExtractPreferredLink(item).Should().BeNull();
    }

    [Fact]
    public void ExtractPreferredLink_EmptyHref_FallsThroughToOtherLinks()
    {
        var item = ParseItem(
            "<entry>" +
                "<title>Story</title>" +
                "<link rel=\"alternate\" href=\"\"/>" +
                "<link href=\"https://outlet.example/story-42\"/>" +
            "</entry>");

        RssPerceptionSource.ExtractPreferredLink(item)
            .Should().Be("https://outlet.example/story-42",
                "empty href on rel=\"alternate\" is treated as no link, precedence continues");
    }
}
