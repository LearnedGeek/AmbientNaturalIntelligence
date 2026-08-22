using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 5 (2026-08-18) — verifies
/// <see cref="MemoryRecord"/> implements <see cref="IRetrievalEnvelope"/>
/// correctly and that <see cref="PromptBuilder.FormatMemorySource"/> maps
/// <see cref="MemoryRecord.Provenance"/> + <see cref="MemoryRecord.SourceName"/>
/// combinations to the human-readable source phrases used by the
/// <c>[FROM: ...]</c> attribution tag prefix.
/// </summary>
public class RetrievalEnvelopeTests
{
    // ── Envelope contract on MemoryRecord ─────────────────────────────────

    [Fact]
    public void MemoryRecord_ImplementsRetrievalEnvelope_ExposesRecordAsContent()
    {
        var m = new MemoryRecord
        {
            Content    = "hey babe, how was work?",
            Provenance = EpistemicTier.Episodic,
            SourceName = "twilio-inbound",
        };
        IRetrievalEnvelope env = m;

        env.Content.Should().BeSameAs(m, "envelope wraps the full MemoryRecord — Content is self-reference");
        env.Provenance.Should().Be(EpistemicTier.Episodic);
        env.Producer.Should().Be("RetrievalContextBuilder");
    }

    [Theory]
    [InlineData(EpistemicTier.Facts,    "rss",             "retrieval.facts.rss")]
    [InlineData(EpistemicTier.Facts,    "twilio-inbound",  "retrieval.facts.twilio-inbound")]
    [InlineData(EpistemicTier.Facts,    null,              "retrieval.facts.unknown")]
    [InlineData(EpistemicTier.Interior, null,              "retrieval.interior.unknown")]
    [InlineData(EpistemicTier.Episodic, "ani",             "retrieval.episodic.ani")]
    public void MemoryRecord_SourceType_ComposesFromProvenanceAndSourceName(
        EpistemicTier tier, string? source, string expected)
    {
        IProvenancedContent<MemoryRecord> env = new MemoryRecord
        {
            Provenance = tier,
            SourceName = source,
        };
        env.SourceType.Should().Be(expected);
    }

    [Fact]
    public void MemoryRecord_SemanticKey_UsesExistingEmbeddingField()
    {
        var vector = new float[] { 0.1f, 0.2f, 0.3f };
        IProvenancedContent<MemoryRecord> env = new MemoryRecord { Embedding = vector };
        env.SemanticKey.Should().BeSameAs(vector);
    }

    [Fact]
    public void MemoryRecord_CreatedAt_UsesExistingField()
    {
        var t = DateTimeOffset.Parse("2026-08-15T10:00:00Z");
        IProvenancedContent<MemoryRecord> env = new MemoryRecord { CreatedAt = t };
        env.CreatedAt.Should().Be(t);
    }

    // ── FormatMemorySource — attribution mapping ───────────────────────────

    // PR #115 review fixes (Devin BUG + Serge minor):
    // - twilio-inbound with null contactName → "inbound text" (neutral),
    //   was hardcoded "text from Mark"
    // - Facts+null-source → "fact" (was mislabeled "character seed")
    // - Facts+"character-seed" → "character seed" (was falling through to fact)
    // - retrieval-self-dominance arm now covered
    [Theory]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "rss",             "news")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "twilio-inbound",  "inbound text")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "weather",         "weather")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "time",            "time-of-day")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "temporal-gap",    "temporal gap")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "contact-state",   "contact state")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "register-saturation", "register saturation")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "retrieval-self-dominance", "self-dominance signal")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "outage",          "outage signal")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Semantic,     "character-seed",  "character seed")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Semantic,     null,              "fact")]
    [InlineData(EpistemicTier.Interior, MemoryType.InnerThought, null,              "your prior thought")]
    [InlineData(EpistemicTier.Interior, MemoryType.InnerThought, "ani",             "your prior thought")]
    [InlineData(EpistemicTier.Episodic, MemoryType.InnerThought, "ani",             "your prior thought")]
    [InlineData(EpistemicTier.Episodic, MemoryType.Episodic,     "mark",            "conversation")]
    [InlineData(EpistemicTier.Episodic, MemoryType.Episodic,     "ani",             "conversation")]
    public void FormatMemorySource_MapsCombosToHumanReadableAttribution(
        EpistemicTier tier, MemoryType type, string? source, string expected)
    {
        var m = new MemoryRecord { Provenance = tier, Type = type, SourceName = source };
        PromptBuilder.FormatMemorySource(m).Should().Be(expected);
    }

    // PR #115 review (Devin BUG #2) — when a contactName is threaded through,
    // twilio-inbound renders "text from {contactName}". When contactName is
    // null or empty, falls back to the neutral "inbound text" (previous test).
    [Theory]
    [InlineData("Mark",   "text from Mark")]
    [InlineData("Kathy",  "text from Kathy")]
    [InlineData("Sarah",  "text from Sarah")]
    public void FormatMemorySource_TwilioInbound_UsesConfiguredContactNameWhenProvided(
        string contactName, string expected)
    {
        var m = new MemoryRecord
        {
            Provenance = EpistemicTier.Facts,
            Type       = MemoryType.Perception,
            SourceName = "twilio-inbound",
        };
        PromptBuilder.FormatMemorySource(m, contactName).Should().Be(expected);
    }

    // ── FormatMemoryWithTime — [FROM: …] prefix presence ───────────────────

    [Fact]
    public void FormatMemoryWithTime_PrependsFromAttributionTag_WithContactName()
    {
        var now = new DateTimeOffset(2026, 08, 18, 22, 0, 0, TimeSpan.Zero);
        var inbound = new MemoryRecord
        {
            Content    = "hey babe, back from teaching",
            OccurredAt = now.AddHours(-2),
            Provenance = EpistemicTier.Facts,
            SourceName = "twilio-inbound",
        };

        var rendered = PromptBuilder.FormatMemoryWithTime(inbound, now, contactName: "Mark");

        // F-2 Phase 1 P4 (2026-08-22): tag now includes AUTHORED segment
        // after FROM. StartWith on the FROM prefix (no trailing "] ") to
        // stay attribution-agnostic — a companion test below pins the
        // AUTHORED shape.
        rendered.Should().StartWith("[FROM: text from Mark",
            "F-1 Phase 5 attribution tag uses configured contactName when threaded through");
        rendered.Should().Contain(inbound.Content,
            "content must still appear after the tag + temporal phrase");
    }

    [Fact]
    public void FormatMemoryWithTime_PrependsFromAttributionTag_NeutralWhenContactNameMissing()
    {
        var now = new DateTimeOffset(2026, 08, 18, 22, 0, 0, TimeSpan.Zero);
        var inbound = new MemoryRecord
        {
            Content    = "hey babe, back from teaching",
            OccurredAt = now.AddHours(-2),
            Provenance = EpistemicTier.Facts,
            SourceName = "twilio-inbound",
        };

        var rendered = PromptBuilder.FormatMemoryWithTime(inbound, now);  // no contactName

        rendered.Should().StartWith("[FROM: inbound text",
            "neutral phrasing when contactName is not threaded through — never hardcode 'Mark'");
    }

    [Fact]
    public void FormatMemoryWithTime_InteriorMemory_TaggedAsPriorThought()
    {
        var now = DateTimeOffset.Now;
        var thought = new MemoryRecord
        {
            Content    = "sitting in the bookstore with the windows open",
            OccurredAt = now.AddHours(-4),
            Provenance = EpistemicTier.Interior,
            Type       = MemoryType.InnerThought,
        };

        var rendered = PromptBuilder.FormatMemoryWithTime(thought, now);
        rendered.Should().StartWith("[FROM: your prior thought");
    }
}
