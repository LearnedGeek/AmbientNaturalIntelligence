using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5c spec tests for the read-side source-attribution
/// filter on <see cref="ClaimVerificationPhase"/>. Pins the contract
/// that only Mark-asserted Facts-tier records count as evidence
/// supporting a Mark-action claim.
///
/// Pure-function tests against the internal helper
/// <c>IsMarkAssertedSource</c>. The full claim-verifier roundtrip is
/// tested via <see cref="ClaimVerificationPhaseTests"/>; these tests
/// pin the new attribution rule at the helper level so the contract
/// is precise and easy to read.
/// </summary>
public class ClaimVerificationAttributionFilterTests
{
    private static MemoryRecord Record(string? source = null, string content = "")
        => new() { SourceName = source, Content = content };

    // ── Mark-asserted sources accepted ─────────────────────────────────

    [Theory]
    [InlineData("twilio-inbound")]
    [InlineData("Twilio-Inbound")]    // case-insensitive
    [InlineData("character-seed")]
    [InlineData("Character-Seed")]
    public void IsMarkAssertedSource_CanonicalSources_True(string source)
    {
        ClaimVerificationPhase.IsMarkAssertedSource(Record(source)).Should().BeTrue();
    }

    [Theory]
    [InlineData("Mark texted: 'I teach at WCTC'")]
    [InlineData("Mark said: 'going to the gym'")]
    [InlineData("About Mark: 55 years old, lives in Wisconsin")]
    public void IsMarkAssertedSource_ContentPrefixDefenseInDepth_True(string content)
    {
        // Defense-in-depth: even when SourceName is empty (older substrate),
        // the canonical content prefix is enough to identify the assertion.
        ClaimVerificationPhase.IsMarkAssertedSource(Record(source: null, content: content))
            .Should().BeTrue();
    }

    // ── Non-Mark-asserted sources rejected ────────────────────────────

    [Theory]
    [InlineData("rss")]               // Bon Appétit / NPR / etc. articles
    [InlineData("perception-rss")]
    [InlineData("conversation")]      // conversation_messages writes (Ani's own outputs)
    [InlineData("inner-thought")]
    [InlineData("world-experience")]
    [InlineData("reflection")]
    [InlineData("")]
    [InlineData(null)]
    public void IsMarkAssertedSource_NonMarkSources_False(string? source)
    {
        ClaimVerificationPhase.IsMarkAssertedSource(Record(source)).Should().BeFalse();
    }

    [Theory]
    [InlineData("I said to Mark: 'cheesy chickpea toast is my go-to'")]   // Apr 30 chickpea-toast bug
    [InlineData("Ani said: 'i teach at the bookstore'")]
    [InlineData("Cheesy chickpea toast: a recipe from Bon Appétit")]      // RSS article body
    [InlineData("the gap between hours feels real")]                      // Semantic / Interior leak
    public void IsMarkAssertedSource_NonMarkContent_False(string content)
    {
        ClaimVerificationPhase.IsMarkAssertedSource(Record(source: null, content: content))
            .Should().BeFalse(
                "the read-side filter must reject content that isn't a Mark assertion " +
                "regardless of the cosine similarity it scored against the claim.");
    }

    [Fact]
    public void IsMarkAssertedSource_NullRecord_False()
    {
        ClaimVerificationPhase.IsMarkAssertedSource(null!).Should().BeFalse();
    }

    // ── The Apr 30 chickpea-toast regression fixture ──────────────────

    [Fact]
    public void Apr30_ChickpeaToast_Regression_RejectsRssArticleAsMarkEvidence()
    {
        // The Apr 30 08:42 bug: a Bon Appétit article perception (RSS source)
        // contained "cheesy chickpea toast" verbatim. When Ani asserted
        // "cheesy chickpea toast is my go-to" as a mark-action claim, the
        // verifier matched the RSS record and reported "supported." The
        // fix rejects RSS-source records as Mark-asserted evidence.
        var rssRecord = new MemoryRecord
        {
            SourceName = "rss",
            Content    = "[Bon Appétit] 25 Easy Weeknight Dinners — Spicy salmon rice bowls, cheesy chickpea toast, and more recipes...",
            Provenance = EpistemicTier.Facts,
        };

        ClaimVerificationPhase.IsMarkAssertedSource(rssRecord).Should().BeFalse(
            "RSS perception articles are NOT Mark-asserted; they cannot support claims about Mark's life.");
    }

    [Fact]
    public void Apr30_DeskAndBooks_Regression_RejectsAniSelfOutputAsMarkEvidence()
    {
        // The Apr 30 07:28 bug: Ani's reply contained "Mark has a desk at
        // home with three old books stacked on one corner." When extracted
        // as a mark-action claim, the verifier matched against Ani's own
        // earlier mention in conversation Episodic — calling it "supported"
        // by Ani's own prior fabrication. The fix rejects "I said to Mark:"
        // / "Ani said:" / conversation-source records as evidence.
        var aniOutput = new MemoryRecord
        {
            SourceName = "conversation",
            Content    = "I said to Mark: \"Mark has a desk at home with three old books stacked on one corner.\"",
            Provenance = EpistemicTier.Episodic,
        };

        ClaimVerificationPhase.IsMarkAssertedSource(aniOutput).Should().BeFalse(
            "Ani's own prior output cannot support a claim about Mark — that's the laundering loop.");
    }
}
