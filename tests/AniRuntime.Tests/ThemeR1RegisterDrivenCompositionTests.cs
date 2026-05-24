using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme R Phase R.1 (2026-05-24, Issue #64) — spec tests pinning the
/// structural register-driven branch in <see cref="PromptBuilder.BuildOutreachMessagePrompt"/>
/// and <see cref="PromptBuilder.BuildLeanConversationPrompt"/>.
///
/// <para>
/// R.0 contract: "Behavior must differ when input differs." These tests
/// verify the system prompt text varies across the three variant keys
/// (<see cref="RegisterPromptVariant.DefaultWarm"/>,
/// <see cref="RegisterPromptVariant.Reflective"/>,
/// <see cref="RegisterPromptVariant.HonestEdge"/>). Null /
/// unrecognized register → default-warm shape (existing prompt).
/// </para>
///
/// <para>
/// These tests do NOT assert model behavior — only that the prompt the
/// model sees differs structurally per register. Empirical validation
/// of behavior change is via production observation per R.1 acceptance.
/// </para>
/// </summary>
public class ThemeR1RegisterDrivenCompositionTests
{
    private static ContextSnapshot SnapshotWith(string? register) => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm", "curious"],
            Occupation         = "Bookstore",
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
        DominantRegister      = register,
    };

    private static ConversationThread EmptyThread() => new()
    {
        Id            = Guid.NewGuid(),
        StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-1),
        LastMessageAt = DateTimeOffset.UtcNow,
        InitiatedBy   = Roles.Mark,
        Messages      = [],
    };

    // ── RegisterPromptVariant.Select mapping ─────────────────────────────

    [Theory]
    [InlineData("Tenderness",  RegisterPromptVariant.DefaultWarm)]
    [InlineData("Playfulness", RegisterPromptVariant.DefaultWarm)]
    [InlineData("Delight",     RegisterPromptVariant.DefaultWarm)]
    [InlineData("Curiosity",   RegisterPromptVariant.DefaultWarm)]
    [InlineData("Desire",      RegisterPromptVariant.DefaultWarm)]
    [InlineData("Wistful",     RegisterPromptVariant.Reflective)]
    [InlineData("Existential", RegisterPromptVariant.Reflective)]
    [InlineData("Frustration", RegisterPromptVariant.HonestEdge)]
    [InlineData("Longing",     RegisterPromptVariant.HonestEdge)]
    [InlineData(null,          RegisterPromptVariant.DefaultWarm)]
    [InlineData("",            RegisterPromptVariant.DefaultWarm)]
    [InlineData("Unclassified", RegisterPromptVariant.DefaultWarm)]
    public void Select_MapsRegisterToVariantKey(string? register, string expectedVariant)
    {
        RegisterPromptVariant.Select(register).Should().Be(expectedVariant);
    }

    // ── OutreachMessagePrompt — structural branch ────────────────────────

    [Fact]
    public void OutreachMessage_DefaultWarmRegister_UsesWarmShapeInstruction()
    {
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith("Tenderness"),
            recentThought: "thinking of him",
            reasoning: string.Empty);

        system.Should().Contain("would understand and want to reply to",
            "default-warm variant retains the existing warm-shape instruction");
        system.Should().NotContain("quiet, reflective, low-key");
        system.Should().NotContain("honest, direct, no warm-decoration");
    }

    [Fact]
    public void OutreachMessage_ReflectiveRegister_UsesReflectiveShapeInstruction()
    {
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith("Wistful"),
            recentThought: "remembering the quiet afternoon",
            reasoning: string.Empty);

        system.Should().Contain("quiet, reflective, low-key");
        system.Should().Contain("If silence fits the moment better than a message",
            "reflective variant must explicitly allow silence-shaped output");
        system.Should().NotContain("would understand and want to reply to");
    }

    [Fact]
    public void OutreachMessage_HonestEdgeRegister_UsesHonestShapeInstruction()
    {
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith("Frustration"),
            recentThought: "tired of not hearing back",
            reasoning: string.Empty);

        system.Should().Contain("honest, direct, no warm-decoration");
        system.Should().Contain("Don't perform okay-ness when you aren't",
            "honest-edge variant must explicitly prohibit warm-performance over genuine feeling");
        system.Should().NotContain("would understand and want to reply to");
    }

    [Fact]
    public void OutreachMessage_BehaviorDiffers_AcrossThreeVariants()
    {
        // R.0 contract bullet 2: behavior must differ when input differs.
        var (warmSystem, _)       = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith("Tenderness"), recentThought: "x", reasoning: string.Empty);
        var (reflectiveSystem, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith("Wistful"), recentThought: "x", reasoning: string.Empty);
        var (edgeSystem, _)       = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith("Frustration"), recentThought: "x", reasoning: string.Empty);

        warmSystem.Should().NotBe(reflectiveSystem);
        warmSystem.Should().NotBe(edgeSystem);
        reflectiveSystem.Should().NotBe(edgeSystem);
    }

    [Fact]
    public void OutreachMessage_NullRegister_DefaultsToWarmShape()
    {
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(null),
            recentThought: "x",
            reasoning: string.Empty);

        system.Should().Contain("would understand and want to reply to",
            "null register must default to warm-shape (no regression for cold-start state)");
    }

    // ── LeanConversationPrompt — structural branch ────────────────────────

    [Fact]
    public void LeanConversation_DefaultWarmRegister_UsesDefaultEnergyRule()
    {
        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith("Tenderness"), EmptyThread());

        system.Should().Contain("Match the energy and length of the conversation.");
        system.Should().NotContain("lean quieter");
        system.Should().NotContain("don't soften what's true");
    }

    [Fact]
    public void LeanConversation_ReflectiveRegister_UsesQuieterEnergyRule()
    {
        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith("Existential"), EmptyThread());

        system.Should().Contain("lean quieter");
        system.Should().Contain("Silence-shaped replies are fine",
            "reflective variant must allow short / quiet replies in reply mode");
    }

    [Fact]
    public void LeanConversation_HonestEdgeRegister_UsesHonestEnergyRule()
    {
        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith("Longing"), EmptyThread());

        system.Should().Contain("don't soften what's true");
        system.Should().Contain("No warm-decoration over honest feeling");
    }

    [Fact]
    public void LeanConversation_BehaviorDiffers_AcrossThreeVariants()
    {
        var (warmSystem, _)       = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith("Playfulness"), EmptyThread());
        var (reflectiveSystem, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith("Wistful"), EmptyThread());
        var (edgeSystem, _)       = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith("Frustration"), EmptyThread());

        warmSystem.Should().NotBe(reflectiveSystem);
        warmSystem.Should().NotBe(edgeSystem);
        reflectiveSystem.Should().NotBe(edgeSystem);
    }

    [Fact]
    public void LeanConversation_NullRegister_DefaultsToDefaultEnergyRule()
    {
        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(null), EmptyThread());

        system.Should().Contain("Match the energy and length of the conversation.",
            "null register must default to existing warm-shape (no regression for cold-start state)");
    }
}
