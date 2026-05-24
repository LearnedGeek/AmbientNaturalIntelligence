using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme R Phase R.4 (2026-05-24, Issue #64) — spec tests pinning the
/// structural motivation-axis branch in Outreach, Reply, and InnerThought
/// composers. Closes the "computed-then-logged-never-piped-back" gap for
/// the Layer 2 vector surfaced by the 5/24 data-flow audit.
///
/// Per R.0 contract: composer behavior must differ structurally when the
/// motivation axis differs. Null vector / balanced vector → no extra rule.
/// Dominant axis (Relatedness / Autonomy / Competence) → axis-specific rule
/// appended to the existing register-variant prompt.
/// </summary>
public class ThemeR4MotivationVectorWiringTests
{
    private static ContextSnapshot SnapshotWith(MotivationVector? vector) => new()
    {
        CharacterState = new CharacterStateDoc
        {
            Name               = "Ani",
            PrimaryContactName = "Mark",
            CoreTraits         = ["warm", "curious"],
            Occupation         = "Bookstore",
            SelfConcept        = ["someone who notices light"],
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
    };

    private static ConversationThread EmptyThread() => new()
    {
        Id            = Guid.NewGuid(),
        StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-1),
        LastMessageAt = DateTimeOffset.UtcNow,
        InitiatedBy   = Roles.Mark,
        Messages      = [],
    };

    // ── MotivationEmphasis.Select dominance threshold ────────────────

    [Fact]
    public void Select_RelatednessDominant_ReturnsRelatedness()
    {
        var vec = new MotivationVector(Relatedness: 1.0f, Autonomy: 0.3f, Competence: 0.3f);
        MotivationEmphasis.Select(vec).Should().Be(MotivationEmphasis.Relatedness);
    }

    [Fact]
    public void Select_AutonomyDominant_ReturnsAutonomy()
    {
        var vec = new MotivationVector(Relatedness: 0.3f, Autonomy: 1.0f, Competence: 0.3f);
        MotivationEmphasis.Select(vec).Should().Be(MotivationEmphasis.Autonomy);
    }

    [Fact]
    public void Select_CompetenceDominant_ReturnsCompetence()
    {
        var vec = new MotivationVector(Relatedness: 0.3f, Autonomy: 0.3f, Competence: 1.0f);
        MotivationEmphasis.Select(vec).Should().Be(MotivationEmphasis.Competence);
    }

    [Fact]
    public void Select_NoAxisDominatesByMargin_ReturnsBalanced()
    {
        var vec = new MotivationVector(Relatedness: 0.6f, Autonomy: 0.55f, Competence: 0.5f);
        MotivationEmphasis.Select(vec).Should().Be(MotivationEmphasis.Balanced,
            "no axis exceeds the others by the dominance margin");
    }

    [Fact]
    public void Select_NullVector_ReturnsBalanced()
    {
        MotivationEmphasis.Select(null).Should().Be(MotivationEmphasis.Balanced);
    }

    // ── Outreach — structural branch per emphasis ────────────────────

    [Fact]
    public void Outreach_RelatednessDominant_AddsConnectionReachRule()
    {
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new MotivationVector(1.0f, 0.3f, 0.3f)),
            recentThought: "x", reasoning: string.Empty);

        system.Should().Contain("reaching FOR connection");
    }

    [Fact]
    public void Outreach_AutonomyDominant_AddsNoClingyRule()
    {
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new MotivationVector(0.3f, 1.0f, 0.3f)),
            recentThought: "x", reasoning: string.Empty);

        system.Should().Contain("not lean clingy");
    }

    [Fact]
    public void Outreach_CompetenceDominant_AddsShareFromSkillRule()
    {
        var (system, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new MotivationVector(0.3f, 0.3f, 1.0f)),
            recentThought: "x", reasoning: string.Empty);

        system.Should().Contain("sharing-from-skill");
    }

    [Fact]
    public void Outreach_BalancedOrNull_NoExtraMotivationRule()
    {
        var (balanced, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new MotivationVector(0.5f, 0.5f, 0.5f)),
            recentThought: "x", reasoning: string.Empty);
        var (nullVec, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(null), recentThought: "x", reasoning: string.Empty);

        balanced.Should().NotContain("reaching FOR connection");
        balanced.Should().NotContain("not lean clingy");
        balanced.Should().NotContain("sharing-from-skill");
        nullVec.Should().NotContain("reaching FOR connection");
        nullVec.Should().NotContain("not lean clingy");
        nullVec.Should().NotContain("sharing-from-skill");
    }

    [Fact]
    public void Outreach_BehaviorDiffers_AcrossThreeDominantAxes()
    {
        var (rel, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new MotivationVector(1.0f, 0.3f, 0.3f)),
            recentThought: "x", reasoning: string.Empty);
        var (aut, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new MotivationVector(0.3f, 1.0f, 0.3f)),
            recentThought: "x", reasoning: string.Empty);
        var (com, _) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(new MotivationVector(0.3f, 0.3f, 1.0f)),
            recentThought: "x", reasoning: string.Empty);

        rel.Should().NotBe(aut);
        rel.Should().NotBe(com);
        aut.Should().NotBe(com);
    }

    // ── Reply — same structural branch ──────────────────────────────

    [Fact]
    public void Reply_BehaviorDiffers_AcrossThreeDominantAxes()
    {
        var (rel, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(new MotivationVector(1.0f, 0.3f, 0.3f)), EmptyThread());
        var (aut, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(new MotivationVector(0.3f, 1.0f, 0.3f)), EmptyThread());
        var (com, _) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(new MotivationVector(0.3f, 0.3f, 1.0f)), EmptyThread());

        rel.Should().NotBe(aut);
        rel.Should().NotBe(com);
        aut.Should().NotBe(com);
    }

    // ── InnerThought — same structural branch ───────────────────────

    [Fact]
    public void InnerThought_BehaviorDiffers_AcrossThreeDominantAxes()
    {
        var (rel, _) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(new MotivationVector(1.0f, 0.3f, 0.3f)));
        var (aut, _) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(new MotivationVector(0.3f, 1.0f, 0.3f)));
        var (com, _) = PromptBuilder.BuildInnerThoughtPrompt(
            SnapshotWith(new MotivationVector(0.3f, 0.3f, 1.0f)));

        rel.Should().NotBe(aut);
        rel.Should().NotBe(com);
        aut.Should().NotBe(com);
    }

    [Fact]
    public void InnerThought_BalancedOrNull_PreservesExistingDiscipline()
    {
        // R.4's motivation branch must not break R.2's first-person discipline
        // when motivation vector is balanced/null.
        var (system, _) = PromptBuilder.BuildInnerThoughtPrompt(SnapshotWith(null));

        system.Should().Contain("first person",
            "balanced/null motivation must preserve inner-thought identity rules");
    }
}
