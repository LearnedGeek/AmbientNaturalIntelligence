using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme R Phase R.6 (2026-05-24, Issue #64) — spec tests pinning the
/// structural contribution-trajectory branch in Outreach + Reply composers.
///
/// Per R.0 contract: high vs low recent volatility produces different
/// prompts. Null trajectory → no extra rule (existing prompt only).
/// </summary>
public class ThemeR6ContributionTrajectoryTests
{
    private static ContextSnapshot SnapshotWith(EmotionalContributionTrajectory? trajectory) => new()
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
        ContributionTrajectory  = trajectory,
    };

    private static ConversationThread EmptyThread() => new()
    {
        Id            = Guid.NewGuid(),
        StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-1),
        LastMessageAt = DateTimeOffset.UtcNow,
        InitiatedBy   = Roles.Mark,
        Messages      = [],
    };

    // ── Outreach — volatility branch ─────────────────────────────────

    [Fact]
    public void Outreach_HighVolatility_AddsLightTouchRule()
    {
        var trajectory = new EmotionalContributionTrajectory(
            RecentVolatility: 0.7f,
            DominantRegistersRecent: ["Tenderness", "Wistful"]);
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(trajectory), recentThought: "x", reasoning: string.Empty);

        system.Should().Contain("fluctuating recently");
        system.Should().Contain("Light touch");
    }

    [Fact]
    public void Outreach_LowVolatility_AddsConfidentContinuationRule()
    {
        var trajectory = new EmotionalContributionTrajectory(
            RecentVolatility: 0.1f,
            DominantRegistersRecent: ["Tenderness"]);
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(trajectory), recentThought: "x", reasoning: string.Empty);

        system.Should().Contain("stable recently");
        system.Should().Contain("lean into the register confidently");
    }

    [Fact]
    public void Outreach_NullTrajectory_NoExtraRule()
    {
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(null), recentThought: "x", reasoning: string.Empty);

        system.Should().NotContain("fluctuating recently");
        system.Should().NotContain("stable recently");
    }

    [Fact]
    public void Outreach_BehaviorDiffers_HighVsLowVolatility()
    {
        var (high, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new EmotionalContributionTrajectory(0.7f, ["Tenderness"])),
            recentThought: "x", reasoning: string.Empty);
        var (low, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new EmotionalContributionTrajectory(0.1f, ["Tenderness"])),
            recentThought: "x", reasoning: string.Empty);

        high.Should().NotBe(low,
            "R.0 contract: high vs low volatility must produce different prompts");
    }

    // ── Reply — same branch ────────────────────────────────────────

    [Fact]
    public void Reply_HighVolatility_AddsLightTouchRule()
    {
        var trajectory = new EmotionalContributionTrajectory(
            RecentVolatility: 0.6f,
            DominantRegistersRecent: ["Frustration", "Wistful"]);
        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(trajectory), EmptyThread());

        system.Should().Contain("fluctuating recently");
        system.Should().Contain("keep the reply light");
    }

    [Fact]
    public void Reply_LowVolatility_AddsConfidentContinuationRule()
    {
        var trajectory = new EmotionalContributionTrajectory(
            RecentVolatility: 0.05f,
            DominantRegistersRecent: ["Curiosity"]);
        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(trajectory), EmptyThread());

        system.Should().Contain("stable recently");
        system.Should().Contain("confident continuation");
    }

    [Fact]
    public void Reply_BehaviorDiffers_HighVsLowVolatility()
    {
        var (high, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(new EmotionalContributionTrajectory(0.7f, ["Tenderness"])),
            EmptyThread());
        var (low, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(new EmotionalContributionTrajectory(0.1f, ["Tenderness"])),
            EmptyThread());

        high.Should().NotBe(low);
    }

    // ── Boundary case at threshold (0.4 exact) ──────────────────────

    [Fact]
    public void Trajectory_VolatilityAtThreshold_TreatedAsLow()
    {
        // Threshold rule: > 0.4 is high, <= 0.4 is low.
        var trajectory = new EmotionalContributionTrajectory(
            RecentVolatility: 0.4f,
            DominantRegistersRecent: ["Tenderness"]);
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(trajectory), recentThought: "x", reasoning: string.Empty);

        system.Should().Contain("stable recently",
            "0.4 exact is the boundary; treated as low (stable)");
    }
}
