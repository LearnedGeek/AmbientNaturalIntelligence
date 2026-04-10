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
    public void Conversation_RoutesToEpisodic()
    {
        ProvenanceBackfill.ClassifyProvenance("conversation", MemoryType.Episodic)
            .Should().Be(EpistemicTier.Episodic);
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
