using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme R Phase R.3 (2026-05-24, Issue #64) — spec tests pinning that
/// <c>snapshot.VibeRecommendedRegister</c> OVERRIDES
/// <c>snapshot.DominantRegister</c> at variant selection. Strategy-
/// effectiveness signal (V1.5 vibe loop) wins over current-state signal
/// (most-recent inner-thought register).
///
/// Per R.0 contract: composer behavior must differ structurally when
/// input differs. R.3-specific contract: when only DominantRegister
/// is set vs when both are set with VibeRecommended differing, the
/// composer's output prompt must differ.
/// </summary>
public class ThemeR3VibeLoopDrivingTests
{
    private static ContextSnapshot SnapshotWith(string? dominant, string? vibe) => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm", "curious"],
            Occupation         = "Bookstore",
        },
        DesireState             = new DesireState(),
        EmotionalState          = new EmotionalState(),
        RecentMemory            = [],
        RelevantMemory          = [],
        OpenLoops               = [],
        Perceptions             = [],
        RecentHistory           = [],
        SimilarRecentThoughts   = [],
        BuiltAt                 = DateTimeOffset.UtcNow,
        DominantRegister        = dominant,
        VibeRecommendedRegister = vibe,
    };

    private static ConversationThread EmptyThread() => new()
    {
        Id            = Guid.NewGuid(),
        StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-1),
        LastMessageAt = DateTimeOffset.UtcNow,
        InitiatedBy   = Roles.Mark,
        Messages      = [],
    };

    // ── Outreach composer — vibe overrides dominant ─────────────────

    // ── Reply composer — vibe overrides dominant ────────────────────

    [Fact]
    public void Reply_VibeRecommended_OverridesDominantRegister()
    {
        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(dominant: "Tenderness", vibe: "Wistful"),
            EmptyThread());

        system.Should().Contain("lean quieter",
            "Vibe recommendation Wistful overrides Dominant Tenderness — Reflective variant wins");
    }

    [Fact]
    public void Reply_VibeNull_FallsBackToDominantRegister()
    {
        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(dominant: "Frustration", vibe: null),
            EmptyThread());

        system.Should().Contain("don't soften what's true",
            "Null vibe → fall back to dominant (Frustration → HonestEdge)");
    }

    [Fact]
    public void Reply_BehaviorDiffers_SameDominantDifferentVibe()
    {
        var (warmVibe, _)       = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(dominant: "Curiosity", vibe: "Playfulness"), EmptyThread());
        var (reflectiveVibe, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(dominant: "Curiosity", vibe: "Existential"), EmptyThread());
        var (edgeVibe, _)       = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(dominant: "Curiosity", vibe: "Longing"), EmptyThread());

        warmVibe.Should().NotBe(reflectiveVibe);
        warmVibe.Should().NotBe(edgeVibe);
        reflectiveVibe.Should().NotBe(edgeVibe);
    }
}
