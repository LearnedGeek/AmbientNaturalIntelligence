using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using AniRuntime.LLM.Prompts;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Life Engine A.−1 (2026-11-05, Issue TBD) — pin the flag-gate behavior
/// for the R.4 motivation emphasis and R.6 contribution-trajectory rules
/// across the three composers (InnerThought, OutreachMessage,
/// LeanConversation). Opus 5.5 review of ANI-Life-Engine-Design.md
/// identified these rules as a positive-feedback loop that has been
/// running in production since May 2026 because autonomy≡0 forces
/// Relatedness to win MotivationEmphasis.Select every cycle, and R.6's
/// "lean in" rule tells the model to stay locked in a locked register.
///
/// Default (flag true) preserves the pre-A.−1 behaviour the Theme R4/R6
/// test suites already pin. With the flag false, the emphasis / trajectory
/// rule must NOT appear in the system prompt regardless of vector or
/// trajectory state. Prod appsettings.json ships with both flags false
/// for the 72h A.−1 observation window.
///
/// See spec/ANI-Life-Engine-Design.md §6 Candidate A.−1.
/// </summary>
public class LifeEngineANeg1FlagGateTests
{
    private static ContextSnapshot SnapshotWith(
        MotivationVector? vector = null,
        EmotionalContributionTrajectory? trajectory = null) => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm", "curious"],
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
        MotivationVector      = vector,
        ContributionTrajectory = trajectory,
    };

    private static ConversationThread EmptyThread() => new()
    {
        Id            = Guid.NewGuid(),
        StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-1),
        LastMessageAt = DateTimeOffset.UtcNow,
        InitiatedBy   = Roles.Mark,
        Messages      = [],
    };

    // ── R.4 emphasis: InnerThought composer ──────────────────────────

    [Fact]
    public void InnerThought_RelatednessVector_FlagOn_InjectsEmphasisRule()
    {
        var vec = new MotivationVector(Relatedness: 1.0f, Autonomy: 0.0f, Competence: 0.0f);
        var snap = SnapshotWith(vector: vec);

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(
            snap, motivationEmphasisEnabled: true);

        system.Should().Contain("Relatedness is high in you right now");
    }

    [Fact]
    public void InnerThought_RelatednessVector_FlagOff_OmitsEmphasisRule()
    {
        var vec = new MotivationVector(Relatedness: 1.0f, Autonomy: 0.0f, Competence: 0.0f);
        var snap = SnapshotWith(vector: vec);

        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(
            snap, motivationEmphasisEnabled: false);

        system.Should().NotContain("Relatedness is high in you right now");
        system.Should().NotContain("Autonomy is high");
        system.Should().NotContain("Competence is high");
    }

    // ── R.4 emphasis: OutreachMessage composer ───────────────────────

    [Fact]
    public void OutreachMessage_RelatednessVector_FlagOn_InjectsEmphasisRule()
    {
        var vec = new MotivationVector(Relatedness: 1.0f, Autonomy: 0.0f, Competence: 0.0f);
        var snap = SnapshotWith(vector: vec);

        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            snap, recentThought: "a thought", reasoning: "a feeling",
            motivationEmphasisEnabled: true);

        system.Should().Contain("reaching FOR connection");
    }

    [Fact]
    public void OutreachMessage_RelatednessVector_FlagOff_OmitsEmphasisRule()
    {
        var vec = new MotivationVector(Relatedness: 1.0f, Autonomy: 0.0f, Competence: 0.0f);
        var snap = SnapshotWith(vector: vec);

        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            snap, recentThought: "a thought", reasoning: "a feeling",
            motivationEmphasisEnabled: false);

        system.Should().NotContain("reaching FOR connection");
        system.Should().NotContain("autonomous state");
        system.Should().NotContain("sharing-from-skill state");
    }

    // ── R.4 emphasis: LeanConversation composer ──────────────────────

    [Fact]
    public void LeanConversation_RelatednessVector_FlagOn_InjectsEmphasisRule()
    {
        var vec = new MotivationVector(Relatedness: 1.0f, Autonomy: 0.0f, Competence: 0.0f);
        var snap = SnapshotWith(vector: vec);

        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            snap, EmptyThread(), motivationEmphasisEnabled: true);

        system.Should().Contain("reaching-for-connection state");
    }

    [Fact]
    public void LeanConversation_RelatednessVector_FlagOff_OmitsEmphasisRule()
    {
        var vec = new MotivationVector(Relatedness: 1.0f, Autonomy: 0.0f, Competence: 0.0f);
        var snap = SnapshotWith(vector: vec);

        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            snap, EmptyThread(), motivationEmphasisEnabled: false);

        system.Should().NotContain("reaching-for-connection state");
        system.Should().NotContain("autonomous state");
        system.Should().NotContain("sharing-from-skill state");
    }

    // ── R.6 trajectory: OutreachMessage composer ─────────────────────

    [Fact]
    public void OutreachMessage_StableTrajectory_FlagOn_InjectsLeanInRule()
    {
        var traj = new EmotionalContributionTrajectory(
            RecentVolatility: 0.1f, DominantRegistersRecent: ["Yearning"]);
        var snap = SnapshotWith(trajectory: traj);

        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            snap, recentThought: "a thought", reasoning: "a feeling",
            contributionTrajectoryEnabled: true);

        system.Should().Contain("lean into the register confidently");
    }

    [Fact]
    public void OutreachMessage_StableTrajectory_FlagOff_OmitsLeanInRule()
    {
        var traj = new EmotionalContributionTrajectory(
            RecentVolatility: 0.1f, DominantRegistersRecent: ["Yearning"]);
        var snap = SnapshotWith(trajectory: traj);

        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            snap, recentThought: "a thought", reasoning: "a feeling",
            contributionTrajectoryEnabled: false);

        system.Should().NotContain("lean into the register confidently");
        system.Should().NotContain("fluctuating recently");
    }

    // ── R.6 trajectory: LeanConversation composer ────────────────────

    [Fact]
    public void LeanConversation_StableTrajectory_FlagOn_InjectsLeanInRule()
    {
        var traj = new EmotionalContributionTrajectory(
            RecentVolatility: 0.1f, DominantRegistersRecent: ["Yearning"]);
        var snap = SnapshotWith(trajectory: traj);

        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            snap, EmptyThread(), contributionTrajectoryEnabled: true);

        system.Should().Contain("stable recently");
    }

    [Fact]
    public void LeanConversation_StableTrajectory_FlagOff_OmitsLeanInRule()
    {
        var traj = new EmotionalContributionTrajectory(
            RecentVolatility: 0.1f, DominantRegistersRecent: ["Yearning"]);
        var snap = SnapshotWith(trajectory: traj);

        var (system, _) = PromptBuilder.BuildLeanConversationPrompt(
            snap, EmptyThread(), contributionTrajectoryEnabled: false);

        system.Should().NotContain("stable recently");
        system.Should().NotContain("fluctuating recently");
    }

    // ── Flag defaults preserve pre-A.−1 behaviour ────────────────────

    [Fact]
    public void InnerThought_DefaultFlag_PreservesEmphasisRule()
    {
        var vec = new MotivationVector(Relatedness: 1.0f, Autonomy: 0.0f, Competence: 0.0f);
        var snap = SnapshotWith(vector: vec);

        // No flag arg — default true path exercised.
        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(snap);

        system.Should().Contain("Relatedness is high in you right now");
    }
}
