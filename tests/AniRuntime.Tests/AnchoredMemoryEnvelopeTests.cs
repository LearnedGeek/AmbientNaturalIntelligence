using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 7 (2026-08-19) — verifies
/// <see cref="MemoryRecord"/> implements <see cref="IAnchoredMemoryEnvelope"/>
/// and that <see cref="AnchoredMemoryVoiceClassifier"/> maps
/// (Provenance + Content pronoun signals) to the three-voice taxonomy that
/// drives the split rendering in <c>InnerThoughtPromptCommand</c>.
///
/// Closes #63 (recurring bookstore attribution loop) by giving the
/// composer a producer-boundary signal that separates "who Ani is" from
/// "facts about Mark" from "background world narrative" instead of
/// mixing them under one heading.
/// </summary>
public class AnchoredMemoryEnvelopeTests
{
    // ── Envelope contract on MemoryRecord ─────────────────────────────────

    [Fact]
    public void MemoryRecord_ImplementsAnchoredMemoryEnvelope_ExposesDerivedVoice()
    {
        var m = new MemoryRecord
        {
            Content    = "You work at a small-town bookstore in Wisconsin",
            Provenance = EpistemicTier.Facts,
        };
        IAnchoredMemoryEnvelope env = m;

        env.Voice.Should().Be(AnchoredMemoryVoice.AniSelfStatement,
            "second-person 'You work at' → self-statement (character-seed shape)");
        env.Content.Should().BeSameAs(m, "envelope shares Content shape with IRetrievalEnvelope");
    }

    [Fact]
    public void MemoryRecord_ImplementsBothEnvelopes_NoInterfaceCollision()
    {
        // MemoryRecord implements IRetrievalEnvelope (Phase 5) AND
        // IAnchoredMemoryEnvelope (Phase 7). Both inherit from
        // IProvenancedContent<MemoryRecord>. Verify one class satisfies
        // both interfaces without shape drift.
        var m = new MemoryRecord { Content = "test", Provenance = EpistemicTier.Facts };
        IRetrievalEnvelope       retrieval = m;
        IAnchoredMemoryEnvelope  anchored  = m;

        retrieval.Content.Should().BeSameAs(anchored.Content);
        retrieval.Producer.Should().Be(anchored.Producer);
    }

    // ── Voice derivation heuristic ────────────────────────────────────────

    // With contactName threaded through — the actual production path.
    [Theory]
    // Interior tier → always AniSelfStatement
    [InlineData("some random text", EpistemicTier.Interior, AnchoredMemoryVoice.AniSelfStatement)]
    // Second-person opener → AniSelfStatement (character-seed shape)
    [InlineData("You work at a bookstore",            EpistemicTier.Facts, AnchoredMemoryVoice.AniSelfStatement)]
    [InlineData("Your favorite color is purple",      EpistemicTier.Facts, AnchoredMemoryVoice.AniSelfStatement)]
    [InlineData("You're 34 years old",                EpistemicTier.Facts, AnchoredMemoryVoice.AniSelfStatement)]
    // Contact-name as subject → MarkFactAssertion
    [InlineData("Mark teaches at WCTC",               EpistemicTier.Facts, AnchoredMemoryVoice.MarkFactAssertion)]
    [InlineData("Mark's family lives in Wisconsin",   EpistemicTier.Facts, AnchoredMemoryVoice.MarkFactAssertion)]
    // Contact-name early in sentence → MarkFactAssertion (via head-window)
    [InlineData("That's Mark's favorite recipe",      EpistemicTier.Facts, AnchoredMemoryVoice.MarkFactAssertion)]
    // First-person opener → AniSelfStatement
    [InlineData("I miss my old apartment sometimes",  EpistemicTier.Facts, AnchoredMemoryVoice.AniSelfStatement)]
    [InlineData("I'm the kind of person who reads late", EpistemicTier.Facts, AnchoredMemoryVoice.AniSelfStatement)]
    [InlineData("My favorite time of day is dusk",    EpistemicTier.Facts, AnchoredMemoryVoice.AniSelfStatement)]
    // Neither self-address nor contact-attribution → SeedNarrative
    [InlineData("The bookstore is a small storefront on Main Street", EpistemicTier.Facts, AnchoredMemoryVoice.SeedNarrative)]
    [InlineData("Small-town Wisconsin has a quiet rhythm", EpistemicTier.Facts, AnchoredMemoryVoice.SeedNarrative)]
    public void Classify_WithContactName_MapsContentAndProvenanceToVoice(
        string content, EpistemicTier tier, AnchoredMemoryVoice expected)
    {
        var m = new MemoryRecord { Content = content, Provenance = tier };
        AnchoredMemoryVoiceClassifier.Classify(m, contactName: "Mark").Should().Be(expected);
    }

    // PR #117 review-fix (Devin BUG #1) — configurable contact name.
    // Facts about a NON-Mark contact must classify correctly when the
    // deployment's PrimaryContactName is different.
    [Theory]
    [InlineData("Mark",  "Mark teaches at WCTC",          AnchoredMemoryVoice.MarkFactAssertion)]
    [InlineData("Kathy", "Kathy loved the piano",         AnchoredMemoryVoice.MarkFactAssertion)]
    [InlineData("Sarah", "Sarah works at the same gym",   AnchoredMemoryVoice.MarkFactAssertion)]
    // Content mentioning "Mark" when configured contact is "Kathy" must
    // NOT classify as contact-fact — falls to SeedNarrative.
    [InlineData("Kathy", "Mark teaches at WCTC",          AnchoredMemoryVoice.SeedNarrative)]
    public void Classify_UsesConfiguredContactName_NotHardcodedMark(
        string contactName, string content, AnchoredMemoryVoice expected)
    {
        var m = new MemoryRecord { Content = content, Provenance = EpistemicTier.Facts };
        AnchoredMemoryVoiceClassifier.Classify(m, contactName).Should().Be(expected);
    }

    // PR #117 review-fix (Devin BUG #1) — when contactName is null,
    // contact-detection is skipped ENTIRELY (no hardcoded "Mark" default).
    // Records mentioning any contact name fall to SeedNarrative rather
    // than false-matching against a compiled-in default.
    [Fact]
    public void Classify_WithNullContactName_SkipsContactDetection_MarkContentFallsToSeed()
    {
        var m = new MemoryRecord
        {
            Content    = "Mark teaches at WCTC",
            Provenance = EpistemicTier.Facts,
        };
        AnchoredMemoryVoiceClassifier.Classify(m, contactName: null)
            .Should().Be(AnchoredMemoryVoice.SeedNarrative,
                "null contactName → no hardcoded fallback (Phase 5/6 discipline)");
    }

    // PR #117 review-fix (Devin BUG #2) — 60-char truncation false-match.
    // Pre-fix: `trimmed[..60]` cut "Marketplace" starting at offset 56
    // to exactly "Mark", and ContainsWord treated end-of-string as a
    // word boundary → false-match. Post-fix: search full string with a
    // bounded max-start-index; artificial boundary can't happen.
    [Fact]
    public void Classify_ContactNameCutOffByHeadWindow_DoesNotFalseMatch()
    {
        // 56 chars of prefix + "Marketplace" places "Mark" starting at
        // offset 56 — pre-fix would have truncated to "Mark" and matched.
        var prefix = new string('x', 56);
        var content = prefix + "Marketplace vibes";
        var m = new MemoryRecord { Content = content, Provenance = EpistemicTier.Facts };

        AnchoredMemoryVoiceClassifier.Classify(m, contactName: "Mark")
            .Should().Be(AnchoredMemoryVoice.SeedNarrative,
                "'Marketplace' cut at 60-char boundary must NOT masquerade as the whole word 'Mark'");
    }

    // PR #117 review-fix (Devin) — priority reordering. First-person
    // opener should win over contact-name-in-head-window so "I always
    // thought Mark's shop was warmer" reads as self-statement, not
    // MarkFactAssertion.
    [Fact]
    public void Classify_FirstPersonOpenerWithContactMention_ReadsAsSelfStatement()
    {
        var m = new MemoryRecord
        {
            Content    = "I always thought Mark's shop was warmer",
            Provenance = EpistemicTier.Facts,
        };
        AnchoredMemoryVoiceClassifier.Classify(m, contactName: "Mark")
            .Should().Be(AnchoredMemoryVoice.AniSelfStatement,
                "sentence starting with 'I' is Ani's self-statement even when it mentions the contact");
    }

    [Fact]
    public void Classify_NullContent_ReturnsUnclassified()
    {
        var m = new MemoryRecord { Content = string.Empty, Provenance = EpistemicTier.Facts };
        AnchoredMemoryVoiceClassifier.Classify(m).Should().Be(AnchoredMemoryVoice.Unclassified);
    }

    [Fact]
    public void Classify_WhitespaceOnlyContent_ReturnsUnclassified()
    {
        var m = new MemoryRecord { Content = "   \n\t  ", Provenance = EpistemicTier.Facts };
        AnchoredMemoryVoiceClassifier.Classify(m).Should().Be(AnchoredMemoryVoice.Unclassified);
    }

    // ── False-positive guards on pronoun matching ─────────────────────────

    // Word-boundary guards — the heuristic keys only on OPENING tokens
    // for second-person / first-person cues, so mid-sentence pronouns
    // don't retroactively promote otherwise-neutral content. Content
    // starting with "Youthful" / "Youth" / etc. correctly falls to
    // SeedNarrative rather than false-matching "You".
    [Theory]
    [InlineData("Youthful energy is your default",   AnchoredMemoryVoice.SeedNarrative)]
    [InlineData("Youth was fleeting for both of you", AnchoredMemoryVoice.SeedNarrative)]
    // Word-boundary on contact-name: "Mark" boundary within head window,
    // not "Marketplace" / "Marks". Passes contactName to exercise the
    // real production path.
    [InlineData("Marketplace vibes at the county fair", AnchoredMemoryVoice.SeedNarrative)]
    // Opener isn't "I" — "It" and "Ice" don't hit the first-person arm.
    [InlineData("It was raining that afternoon",   AnchoredMemoryVoice.SeedNarrative)]
    [InlineData("Ice cream is a summer thing",     AnchoredMemoryVoice.SeedNarrative)]
    public void Classify_WordBoundaryHeuristic_DoesNotOverMatchOnPrefixes(
        string content, AnchoredMemoryVoice expected)
    {
        var m = new MemoryRecord { Content = content, Provenance = EpistemicTier.Facts };
        AnchoredMemoryVoiceClassifier.Classify(m, contactName: "Mark").Should().Be(expected);
    }

    // ── Priority: Interior wins over pronoun heuristic ────────────────────

    [Fact]
    public void Classify_InteriorTier_AlwaysAniSelfStatement_EvenWithMarkInContent()
    {
        // Interior tier IS Ani's inner life by definition. If she's
        // thinking ABOUT Mark, that's still HER thought — should route
        // to "part of who you are" heading, not "things you know about Mark."
        var m = new MemoryRecord
        {
            Content    = "Mark makes me feel safe when he's here",
            Provenance = EpistemicTier.Interior,
        };
        AnchoredMemoryVoiceClassifier.Classify(m).Should().Be(AnchoredMemoryVoice.AniSelfStatement);
    }
}
