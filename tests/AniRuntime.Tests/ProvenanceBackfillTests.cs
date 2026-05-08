using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Unit tests for the Epistemic Grounding backfill heuristic.
/// Verifies that each expected source_name / MemoryType combination routes to
/// the correct tier according to the architectural principle:
///   Facts = user-asserted or externally observed
///   Episodic = verbatim conversation
///   Interior = Ani's own generated content
/// </summary>
public class ProvenanceBackfillTests
{
    // ─── Facts tier ────────────────────────────────────────────────────

    [Fact]
    public void CharacterSeed_RoutesToFacts()
    {
        ProvenanceBackfill.ClassifyProvenance("character-seed", MemoryType.Semantic)
            .Should().Be(EpistemicTier.Facts);
    }

    [Fact]
    public void TwilioInbound_RoutesToFacts()
    {
        ProvenanceBackfill.ClassifyProvenance("twilio-inbound", MemoryType.Episodic)
            .Should().Be(EpistemicTier.Facts);
    }

    [Theory]
    [InlineData("perception")]
    [InlineData("perception-time")]
    [InlineData("perception-weather")]
    public void PerceptionSources_RouteToFacts(string source)
    {
        ProvenanceBackfill.ClassifyProvenance(source, MemoryType.Perception)
            .Should().Be(EpistemicTier.Facts);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("time-perception")]
    [InlineData("weather")]
    [InlineData("twilio-weather")]
    [InlineData("world-weather")]
    public void TimeAndWeatherSources_RouteToFacts(string source)
    {
        ProvenanceBackfill.ClassifyProvenance(source, MemoryType.Perception)
            .Should().Be(EpistemicTier.Facts);
    }

    [Theory]
    [InlineData("rss")]
    [InlineData("rss-tech")]
    [InlineData("rss-news")]
    public void RssSources_RouteToFacts(string source)
    {
        ProvenanceBackfill.ClassifyProvenance(source, MemoryType.Perception)
            .Should().Be(EpistemicTier.Facts);
    }

    [Fact]
    public void ContactState_RoutesToFacts()
    {
        ProvenanceBackfill.ClassifyProvenance("contact-state", MemoryType.Perception)
            .Should().Be(EpistemicTier.Facts);
    }

    [Theory]
    [InlineData("calendar")]
    [InlineData("calendar-event")]
    public void CalendarSources_RouteToFacts(string source)
    {
        ProvenanceBackfill.ClassifyProvenance(source, MemoryType.Perception)
            .Should().Be(EpistemicTier.Facts);
    }

    // ─── Episodic tier ─────────────────────────────────────────────────

    [Fact]
    public void Conversation_2ArgOverload_FallsToEpisodic()
    {
        // 2-arg overload (no content) preserves prior behavior — conversation
        // → Episodic without prefix split. Backward compatibility for callers
        // that haven't migrated to passing content.
        ProvenanceBackfill.ClassifyProvenance("conversation", MemoryType.Episodic)
            .Should().Be(EpistemicTier.Episodic);
    }

    // ─── Conversation prefix split (Tier Contract §2, locked May 8) ──────
    //
    // Mark-side conversation records → Facts (Mark-asserted content).
    // Ani-side conversation records  → Episodic (Ani-said events).
    // Load-bearing for the kitchen-lights/Bob-Swanson failure pattern.

    [Theory]
    [InlineData("Mark said: \"Talk about the kitchen lights?\"")]
    [InlineData("Mark texted: \"Were we talking about kitchen lights?\"")]
    public void Conversation_MarkSidePrefix_RoutesToFacts(string content)
    {
        ProvenanceBackfill.ClassifyProvenance("conversation", MemoryType.Episodic, content)
            .Should().Be(EpistemicTier.Facts,
                "Mark-side conversation content is Mark-asserted; Facts tier");
    }

    [Theory]
    [InlineData("I said to Mark: \"hey, just thinking about you\"")]
    [InlineData("I reached out to Mark: \"good morning love\"")]
    public void Conversation_AniSidePrefix_RoutesToEpisodic(string content)
    {
        ProvenanceBackfill.ClassifyProvenance("conversation", MemoryType.Episodic, content)
            .Should().Be(EpistemicTier.Episodic,
                "Ani-side conversation content is Ani-said event; Episodic tier");
    }

    [Fact]
    public void Conversation_NullContent_FallsToEpisodic()
    {
        // Backward-compatibility path: when content isn't available, conversation
        // routes to Episodic. Same as 2-arg overload.
        ProvenanceBackfill.ClassifyProvenance("conversation", MemoryType.Episodic, content: null)
            .Should().Be(EpistemicTier.Episodic);
    }

    [Fact]
    public void Conversation_UnrecognizedPrefix_FallsToEpisodic()
    {
        // Mixed-side conversation summary records (e.g. "Conversation (5 messages):
        // ...") don't match either canonical prefix; safe destination is Episodic.
        ProvenanceBackfill.ClassifyProvenance(
            "conversation", MemoryType.Episodic,
            "Conversation (5 messages): a brief exchange about coffee")
            .Should().Be(EpistemicTier.Episodic,
                "unrecognized prefix falls to Episodic — never contaminates Facts");
    }

    [Theory]
    [InlineData("outreach")]
    [InlineData("outreach-dispatched")]
    public void OutreachSources_RouteToEpisodic(string source)
    {
        ProvenanceBackfill.ClassifyProvenance(source, MemoryType.Episodic)
            .Should().Be(EpistemicTier.Episodic);
    }

    // ─── Interior tier ─────────────────────────────────────────────────

    [Fact]
    public void WorldExperience_RoutesToInterior()
    {
        // Per the design doc "World-Experience Routing" section:
        // world-experience records are reflective elaborations that reference
        // facts stored elsewhere. They never originate facts.
        ProvenanceBackfill.ClassifyProvenance("world-experience", MemoryType.Episodic)
            .Should().Be(EpistemicTier.Interior);
    }

    [Fact]
    public void Reflection_RoutesToInterior()
    {
        ProvenanceBackfill.ClassifyProvenance("reflection", MemoryType.Semantic)
            .Should().Be(EpistemicTier.Interior);
    }

    [Fact]
    public void InnerThoughtType_RoutesToInteriorRegardlessOfSource()
    {
        // Type-based routing — InnerThought always goes Interior even if source is unset
        ProvenanceBackfill.ClassifyProvenance(null, MemoryType.InnerThought)
            .Should().Be(EpistemicTier.Interior);
        ProvenanceBackfill.ClassifyProvenance("", MemoryType.InnerThought)
            .Should().Be(EpistemicTier.Interior);
        ProvenanceBackfill.ClassifyProvenance("some-other-source", MemoryType.InnerThought)
            .Should().Be(EpistemicTier.Interior);
    }

    // ─── Case sensitivity ──────────────────────────────────────────────

    [Fact]
    public void SourceName_IsCaseInsensitive()
    {
        ProvenanceBackfill.ClassifyProvenance("CHARACTER-SEED", MemoryType.Semantic)
            .Should().Be(EpistemicTier.Facts);
        ProvenanceBackfill.ClassifyProvenance("Twilio-Inbound", MemoryType.Episodic)
            .Should().Be(EpistemicTier.Facts);
        ProvenanceBackfill.ClassifyProvenance(" conversation ", MemoryType.Episodic)
            .Should().Be(EpistemicTier.Episodic);
    }

    // ─── Defaults ──────────────────────────────────────────────────────

    [Fact]
    public void UnknownSource_DefaultsToEpisodic()
    {
        // Unknown sources default to Episodic — safest because it:
        //   1. Preserves the memory as "something that happened"
        //   2. Does NOT contaminate the Facts pool
        //   3. Does NOT grant interior creative latitude
        ProvenanceBackfill.ClassifyProvenance("some-new-source", MemoryType.Episodic)
            .Should().Be(EpistemicTier.Episodic);
    }

    [Fact]
    public void NullSource_NonInnerThoughtType_DefaultsToEpisodic()
    {
        ProvenanceBackfill.ClassifyProvenance(null, MemoryType.Episodic)
            .Should().Be(EpistemicTier.Episodic);
    }

    [Fact]
    public void EmptySource_NonInnerThoughtType_DefaultsToEpisodic()
    {
        ProvenanceBackfill.ClassifyProvenance("", MemoryType.Episodic)
            .Should().Be(EpistemicTier.Episodic);
    }

    // ─── MemoryRecord overload ─────────────────────────────────────────

    [Fact]
    public void ClassifyProvenance_AcceptsMemoryRecord()
    {
        var record = new MemoryRecord
        {
            SourceName = "character-seed",
            Type = MemoryType.Semantic,
            Content = "Mark teaches at WCTC"
        };
        ProvenanceBackfill.ClassifyProvenance(record).Should().Be(EpistemicTier.Facts);
    }
}
