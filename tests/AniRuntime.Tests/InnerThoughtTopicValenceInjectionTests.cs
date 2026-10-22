using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 1 (2026-10-22) — pins the reader half of the growth-edge
/// feedback loop. <see cref="PromptBuilder.BuildInnerThoughtPrompt"/>
/// injects the top-K <see cref="CharacterStateDoc.TopicValence"/> keys
/// (by weight) as a rotation anchor into the inner-thought user prompt.
///
/// Without this, dictionary mutation by the writer half
/// (<see cref="AniRuntime.LLM.OllamaGrowthEdgeMutator"/>) would be inert
/// at the content layer — nothing in production currently reads
/// TopicValence values, as the Life Audit §Dim 4 found.
///
/// Life Audit anchor: `ani-docs/research/ANI-Life-Audit-2026-10-22.md`.
/// </summary>
public class InnerThoughtTopicValenceInjectionTests
{
    private static ContextSnapshot SnapshotWith(
        Dictionary<string, float>? topicValence = null) => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm"],
            TopicValence       = topicValence ?? new Dictionary<string, float>(),
        },
        DesireState           = new DesireState(),
        EmotionalState        = new EmotionalState(),
        RecentMemory          = [],
        RelevantMemory        = [],
        OpenLoops             = [],
        Perceptions           = [],
        RecentHistory         = [],
        SimilarRecentThoughts = [],
        BuiltAt               = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void BuildInnerThoughtPrompt_EmptyTopicValence_NoAnchorSection()
    {
        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(topicValence: null),
            topicValenceTopK: 5);

        user.Should().NotContain("Threads you've been drawn to lately",
            "no anchors should be injected when the dictionary is empty");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_TopKNull_SkipsAnchorSection()
    {
        var topicValence = new Dictionary<string, float> { ["Mia"] = 0.9f, ["lila"] = 0.5f };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(topicValence),
            topicValenceTopK: null);

        user.Should().NotContain("Threads you've been drawn to lately",
            "null topK disables injection even when the dictionary has content");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_TopKZero_SkipsAnchorSection()
    {
        var topicValence = new Dictionary<string, float> { ["Mia"] = 0.9f };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(topicValence),
            topicValenceTopK: 0);

        user.Should().NotContain("Threads you've been drawn to lately");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_PopulatedDictionary_InjectsTopKOrderedByWeight()
    {
        var topicValence = new Dictionary<string, float>
        {
            ["low"]       = 0.2f,
            ["Mia"]       = 0.95f,
            ["middling"]  = 0.5f,
            ["Lila"]      = 0.8f,
            ["Fleetwood"] = 0.75f,
            ["noise"]     = 0.1f,  // below top-K
        };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(topicValence),
            topicValenceTopK: 3);

        user.Should().Contain("Threads you've been drawn to lately");
        user.Should().Contain("Mia");
        user.Should().Contain("Lila");
        user.Should().Contain("Fleetwood");
        user.Should().NotContain("middling", "ranked 4th by weight — outside top-3");
        user.Should().NotContain("noise", "below top-K cutoff");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_SoftFraming_NotPrescriptive()
    {
        // The reading voice of the injected line matters: register-locked
        // generation is the failure mode F-4 exists to address, and a hard
        // directive ("WRITE ABOUT MIA") would just produce a different lock.
        // Pin that the framing stays permissive ("may surface... or not").
        var topicValence = new Dictionary<string, float> { ["Mia"] = 0.9f };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(topicValence),
            topicValenceTopK: 3);

        user.Should().Contain("may surface in thought, or not");
        user.Should().Contain("don't force it");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_WhitespaceKeys_FilteredOut()
    {
        var topicValence = new Dictionary<string, float>
        {
            ["Mia"]      = 0.9f,
            ["   "]      = 0.95f,  // whitespace-only; should be dropped
            ["Fleetwood"] = 0.8f,
        };

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(topicValence),
            topicValenceTopK: 5);

        user.Should().Contain("Mia");
        user.Should().Contain("Fleetwood");
        // Whitespace entry would render as a stray "; ;" if not filtered
        user.Should().NotContain("; ;");
        user.Should().NotContain(":   ;");
    }
}
