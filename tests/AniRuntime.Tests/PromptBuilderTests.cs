using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

public class PromptBuilderTests
{
    private static ContextSnapshot MinimalSnapshot() => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits = ["warm", "curious"],
            Occupation = "Bookstore",
        },
        DesireState = new DesireState(),
        EmotionalState = new EmotionalState(),
        RecentMemory = [],
        RelevantMemory = [],
        OpenLoops = [],
        Perceptions = [],
        RecentHistory = [],
        SimilarRecentThoughts = [],
        BuiltAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void BuildReflectionPrompt_IncludesThoughtInUser()
    {
        var snapshot = MinimalSnapshot();
        var thought = "rain on the window sounds like someone tapping";

        var (system, user) = PromptBuilder.BuildReflectionPrompt(thought, snapshot);

        user.Should().Contain(thought);
        system.Should().Contain("Ani");
        system.Should().Contain("1-2 sentences");
    }

    [Fact]
    public void BuildReflectionPrompt_IncludesMoodWhenNotable()
    {
        var snapshot = MinimalSnapshot();
        snapshot.EmotionalState = new EmotionalState
        {
            Warmth = 0.9f, WarmthBaseline = 0.6f,
        };

        var (_, user) = PromptBuilder.BuildReflectionPrompt("a quiet thought", snapshot);

        user.Should().Contain("warm");
    }

    [Fact]
    public void BuildReflectionPrompt_IncludesRelevantMemories()
    {
        var snapshot = MinimalSnapshot();
        snapshot.RelevantMemory =
        [
            new MemoryRecord
            {
                Type = MemoryType.Episodic,
                Content = "Mark mentioned he loves the sound of rain",
            }
        ];

        var (_, user) = PromptBuilder.BuildReflectionPrompt("rain tapping on glass", snapshot);

        user.Should().Contain("rain");
        user.Should().Contain("Things that might connect");
    }

    [Fact]
    public void BuildReflectionPrompt_IncludesOpenLoops()
    {
        var snapshot = MinimalSnapshot();
        snapshot.OpenLoops =
        [
            new OpenLoop { Description = "Mark's dentist appointment tomorrow" }
        ];

        var (_, user) = PromptBuilder.BuildReflectionPrompt("thinking about tomorrow", snapshot);

        user.Should().Contain("dentist");
    }

    [Fact]
    public void BuildReflectionPrompt_ExcludesInnerThoughtsFromMemories()
    {
        var snapshot = MinimalSnapshot();
        snapshot.RelevantMemory =
        [
            new MemoryRecord { Type = MemoryType.InnerThought, Content = "should not appear" },
            new MemoryRecord { Type = MemoryType.Episodic, Content = "should appear" },
        ];

        var (_, user) = PromptBuilder.BuildReflectionPrompt("a thought", snapshot);

        user.Should().Contain("should appear");
        user.Should().NotContain("should not appear");
    }

    // ── Nature Grounding (Self-Concept Block) ─────────────────────────────────

    [Fact]
    public void BuildInnerThoughtPrompt_IncludesNatureGrounding_WhenPresent()
    {
        var snapshot = MinimalSnapshot();
        snapshot.CharacterState.NatureGrounding =
        [
            "I exist as a presence, not a place.",
            "The imagining and the reaching are different things.",
        ];

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        system.Should().Contain("What you know about yourself:");
        system.Should().Contain("I exist as a presence, not a place.");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_OmitsNatureGrounding_WhenEmpty()
    {
        var snapshot = MinimalSnapshot();
        snapshot.CharacterState.NatureGrounding = [];

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        system.Should().NotContain("What you know about yourself:");
    }

    // ── Feature 22: Coherence Gate Physical Plausibility ──────────────────────

    [Fact]
    public void BuildCoherenceEvaluationPrompt_ContainsPhysicalPlausibilityCheck()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "I just found a corner of my backyard", "imagining a quiet space", "Mark");

        system.Should().Contain("PHYSICAL PLAUSIBILITY CHECK");
        system.Should().Contain("non-embodied");
        system.Should().Contain("Invalid (embodiment claim)");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesValidImaginativeExamples()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("I keep imagining what your kitchen smells like");
        system.Should().Contain("In my mind I'm sitting across from you");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesInvalidEmbodimentExamples()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("I just found the perfect corner of my backyard");
        system.Should().Contain("I'm sitting outside watching the sunrise");
        system.Should().Contain("I made coffee this morning");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_PhysicalClaimRoutesDoorC()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        // The prompt instructs the LLM to route physical claims to Door C
        system.Should().Contain("invalid physical claim");
        system.Should().Contain("Door C");
        system.Should().Contain("SUPPRESS");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_StillContainsThreeDoorClassification()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("DOOR A");
        system.Should().Contain("DOOR B");
        system.Should().Contain("DOOR C");
        system.Should().Contain("Grounded reference");
        system.Should().Contain("Standalone creative");
        system.Should().Contain("Inner thought leaked");
    }
}
