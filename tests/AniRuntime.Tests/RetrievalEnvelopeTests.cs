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

    [Theory]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "rss",             "news")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "twilio-inbound",  "text from Mark")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "weather",         "weather")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "time",            "time-of-day")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "temporal-gap",    "temporal gap")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "contact-state",   "contact state")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "register-saturation", "register saturation")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Perception,   "outage",          "outage signal")]
    [InlineData(EpistemicTier.Facts,    MemoryType.Semantic,     null,              "character seed")]
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

    // ── FormatMemoryWithTime — [FROM: …] prefix presence ───────────────────

    [Fact]
    public void FormatMemoryWithTime_PrependsFromAttributionTag()
    {
        var now = new DateTimeOffset(2026, 08, 18, 22, 0, 0, TimeSpan.Zero);
        var mark = new MemoryRecord
        {
            Content    = "hey babe, back from teaching",
            OccurredAt = now.AddHours(-2),
            Provenance = EpistemicTier.Facts,
            SourceName = "twilio-inbound",
        };

        var rendered = PromptBuilder.FormatMemoryWithTime(mark, now);

        rendered.Should().StartWith("[FROM: text from Mark] ",
            "F-1 Phase 5 attribution tag comes before the temporal phrase");
        rendered.Should().Contain(mark.Content,
            "content must still appear after the tag + temporal phrase");
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
        rendered.Should().StartWith("[FROM: your prior thought] ");
    }
}
