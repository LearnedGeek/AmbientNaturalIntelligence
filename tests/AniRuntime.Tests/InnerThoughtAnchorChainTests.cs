using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 4a.2b (2026-10-22) — pins the InnerThoughtPromptCommand
/// render contract for the "Threads of thought you've been following"
/// section. The chain was being produced and persisted for 7 months
/// (1,694 anchors in production) but only one link was ever reaching
/// the composer. This surfacing is the structural unlock.
/// </summary>
public class InnerThoughtAnchorChainTests
{
    private static ContextSnapshot SnapshotWith(List<string>? chain = null) => new()
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
        BuiltAt               = new DateTimeOffset(2026, 10, 22, 11, 0, 0, TimeSpan.Zero),
        RecentAnchorChain     = chain ?? new List<string>(),
    };

    [Fact]
    public void BuildInnerThoughtPrompt_EmptyChain_NoSection()
    {
        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith());
        user.Should().NotContain("Threads of thought you've been following");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_PopulatedChain_SectionRendered()
    {
        var snapshot = SnapshotWith(new List<string>
        {
            "the shelf i built",
            "radiator doing its thump-thump-thump",
            "my chest is still healing",
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Threads of thought you've been following");
        user.Should().Contain("the shelf i built");
        user.Should().Contain("radiator");
        user.Should().Contain("my chest is still healing");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_ChainCappedAt5()
    {
        // Composer takes at most 5 entries from the chain even if more
        // were hydrated. Caller (ContextBuilder) typically caps at the
        // configured AssociativeAnchorChainDepth, but composer defends.
        var snapshot = SnapshotWith(new List<string>
        {
            "a", "b", "c", "d", "e", "f", "g",
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Threads of thought you've been following");
        // First 5 should appear; last 2 ('f', 'g') should not
        var section = user.Substring(user.IndexOf("Threads of thought"));
        section.Should().NotContain("; f;");
        section.Should().NotContain("; g;");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_WhitespaceEntries_Filtered()
    {
        var snapshot = SnapshotWith(new List<string>
        {
            "   ",
            "the shelf i built",
            "",
            "the silence",
        });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("Threads of thought you've been following");
        user.Should().Contain("the shelf i built");
        user.Should().Contain("the silence");
        user.Should().NotContain(";  ;");
        user.Should().NotContain("; ;");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_SoftFraming_Preserved()
    {
        var snapshot = SnapshotWith(new List<string> { "the shelf i built" });

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        user.Should().Contain("may surface again or lead somewhere new");
        user.Should().Contain("don't force it");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_ChainAndTopicValenceCoexist()
    {
        // Phase 1 TopicValence top-K and Phase 4a.2b anchor chain render
        // as separate sections — different grain of signal (slow preference
        // drift vs short-term thought-fragments). Both should appear.
        var snapshot = SnapshotWith(new List<string> { "the shelf i built" });
        snapshot.CharacterState.TopicValence = new Dictionary<string, float>
        {
            ["Mia"] = 0.9f,
        };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot, topicValenceTopK: 3);

        user.Should().Contain("Threads you've been drawn to lately",
            "Phase 1 TopicValence section renders");
        user.Should().Contain("Mia");
        user.Should().Contain("Threads of thought you've been following",
            "Phase 4a.2b anchor chain section renders");
        user.Should().Contain("the shelf i built");
    }
}
