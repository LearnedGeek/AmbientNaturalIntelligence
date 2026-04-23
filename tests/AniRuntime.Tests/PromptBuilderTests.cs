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
            Energy = 0.7f, EnergyBaseline = 0.5f, // W ≥ 0.75, E ≥ 0.65 → "bright and warm"
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
            "I live in my spaces — the bookstore, the kitchen.",
            "The trick is coherence.",
        ];

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        system.Should().Contain("What you know about yourself:");
        system.Should().Contain("I live in my spaces");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_OmitsNatureGrounding_WhenEmpty()
    {
        var snapshot = MinimalSnapshot();
        snapshot.CharacterState.NatureGrounding = [];

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        system.Should().NotContain("What you know about yourself:");
    }

    // ── Agentic Lens Layer 5: Inner-thought subject-space opener ───────────────
    // The no-WorldSeed final question used to be "What is passing through your mind
    // right now?" — neutral in principle, but produced caregiver-centered thought in
    // practice because the surrounding substrate is ~95% caregiver-shaped. Layer 5
    // rewrites it to an anchored opener that explicitly invites subject-diverse
    // content without forbidding caregiver-centered thought (forbidding would be
    // instruction; the rewrite is architecture).

    [Fact]
    public void BuildInnerThoughtPrompt_UsesSubjectSpaceOpener_WhenNoWorldSeed()
    {
        var snapshot = MinimalSnapshot();
        snapshot.WorldSeed = null;

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        // New Layer 5 opener
        user.Should().Contain("What are you noticing right now?");
        user.Should().Contain("Anchor it in something specific");
        user.Should().Contain("It doesn't have to be about anyone.");

        // Old generic opener should be gone
        user.Should().NotContain("What is passing through your mind right now?");
    }

    [Fact]
    public void BuildInnerThoughtPrompt_UsesWorldSeed_WhenPresent()
    {
        var snapshot = MinimalSnapshot();
        snapshot.WorldSeed = "You're shelving romance novels on a slow Sunday morning.";

        var (_, user) = PromptBuilder.BuildInnerThoughtPrompt(snapshot);

        // When a World Layer seed is present, that IS the prompt.
        user.Should().Contain("shelving romance novels on a slow Sunday morning");

        // The no-seed subject-space opener should not also be injected.
        user.Should().NotContain("What are you noticing right now?");
    }

    // ── Feature 22: Coherence Gate — Fictional Coherence Check ────────────────

    [Fact]
    public void BuildCoherenceEvaluationPrompt_ContainsFictionalCoherenceCheck()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "I just found a corner of my backyard", "imagining a quiet space", "Mark");

        system.Should().Contain("FICTIONAL COHERENCE CHECK");
        system.Should().Contain("committed imagination");
        system.Should().Contain("Claiming a physical space is FINE");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesCoherentExamples()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("how's the soup turning out");
        system.Should().Contain("curled up with a book");
        system.Should().Contain("closed up the store");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesIncoherentExamples()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("no shade");
        system.Should().Contain("6:30am");
        system.Should().Contain("bookstore is closed");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncoherenceSuppresses()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("fiction is incoherent");
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

    // ── Feature 22 refinement: Temporal Coherence Check ──

    [Fact]
    public void BuildCoherenceEvaluationPrompt_ContainsTemporalCoherenceCheck()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("TEMPORAL COHERENCE CHECK");
        system.Should().Contain("claimed time match the actual current time");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesCurrentTime()
    {
        var fixedTime = new DateTimeOffset(2026, 3, 14, 13, 34, 0, TimeSpan.FromHours(-6));
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "the clock just hit midnight", "late night reading", "Mark", fixedTime);

        system.Should().Contain("1:34 PM");
        system.Should().Contain("afternoon");
    }

    [Theory]
    [InlineData(7, "morning")]
    [InlineData(14, "afternoon")]
    [InlineData(19, "evening")]
    [InlineData(23, "night")]
    public void BuildCoherenceEvaluationPrompt_CorrectTimeOfDay(int hour, string expected)
    {
        var time = new DateTimeOffset(2026, 3, 14, hour, 0, 0, TimeSpan.Zero);
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test", "test", "Mark", time);

        system.Should().Contain($"({expected})");
    }

    [Fact]
    public void BuildCoherenceEvaluationPrompt_IncludesMidnightTemporalExample()
    {
        var (system, _) = PromptBuilder.BuildCoherenceEvaluationPrompt(
            "test message", "test thought", "Mark");

        system.Should().Contain("clock just hit midnight");
        system.Should().Contain("temporal mismatch");
    }

}
