using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 4a.1a (2026-10-22) — pins the InnerThoughtPromptCommand
/// render contract for the new "Things that have happened in the world
/// recently" section. Life Audit §Dim 1 showed 1,092 persisted RSS
/// Perception events with near-zero inner-thought penetration; this
/// section is the structural unlock.
/// </summary>
public class InnerThoughtWorldEventSurfacingTests
{
    private static ContextSnapshot SnapshotWith(List<MemoryRecord>? worldEvents = null) => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm"],
        },
        DesireState           = new DesireState(),
        EmotionalState        = new EmotionalState(),
        RecentMemory          = [],
        RelevantMemory        = [],
        OpenLoops             = [],
        Perceptions           = [],
        RecentHistory         = [],
        SimilarRecentThoughts = [],
        BuiltAt               = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero),
        RecentWorldEvents     = worldEvents ?? new List<MemoryRecord>(),
    };

    private static MemoryRecord WorldEvent(string content, DateTimeOffset at) => new()
    {
        Type       = MemoryType.Perception,
        Content    = content,
        SourceName = "rss",
        OccurredAt = at,
        Provenance = EpistemicTier.Facts,
    };

    [Fact]
    public void BuildInnerThoughtPrompt_EmptyWorldEvents_NoSectionRendered()
    {
        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith());
        user.Should().NotContain("Things that have happened in the world");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_WorldEventsPresent_SectionRendered()
    {
        var at = new DateTimeOffset(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);
        var snapshot = SnapshotWith(new List<MemoryRecord>
        {
            WorldEvent("[NPR News] Supreme Court appeals case update — ...", at),
            WorldEvent("[NPR News] Eurovision Song Contest results — ...", at.AddHours(-2)),
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Things that have happened in the world recently");
        user.Should().Contain("Supreme Court");
        user.Should().Contain("Eurovision");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_WorldEventsSection_UsesDoNotQuoteFraming()
    {
        // Preserve the F-1 Phase 5+ "do not quote phrases verbatim" discipline
        // that's already in the Recent / Relevant memory sections. World-events
        // are substrate for reflection, not quote-source material.
        var at = new DateTimeOffset(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);
        var snapshot = SnapshotWith(new List<MemoryRecord>
        {
            WorldEvent("[NPR News] Example story — ...", at),
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("do not quote phrases verbatim");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_WorldEventsSeparateFromPerceptionsSection()
    {
        // Life Audit §Dim 1 called out the provenance distinction between
        // current-cycle Perceptions (arriving now) and persisted
        // RecentWorldEvents (retrieved from memory). Rendering them under
        // distinct headings is load-bearing to that distinction.
        var at = new DateTimeOffset(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);
        var snapshot = SnapshotWith(new List<MemoryRecord>
        {
            WorldEvent("[NPR News] A persisted story from memory", at),
        });
        snapshot.Perceptions = new List<PerceptionEvent>
        {
            new()
            {
                SourceName = "rss",
                Category   = PerceptionCategory.Content,
                Summary    = "A current-cycle poll result",
                OccurredAt = at.AddMinutes(5),
            },
        };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Things that have happened in the world recently",
            "persisted world-events section renders");
        user.Should().Contain("Background right now",
            "current-cycle Perceptions section still renders separately");

        // Both items should be in the output, under different headings
        user.Should().Contain("persisted story from memory");
        user.Should().Contain("current-cycle poll result");
    }
}
