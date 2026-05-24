using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme R Phase R.5 (2026-05-24, Issue #64) — spec tests pinning the
/// structural Open-Loops consumption in Outreach + Reply composers.
/// Generalizes the InnerThoughtPromptCommand pattern that already
/// consumed OpenLoops.
///
/// Per R.0 contract: when input differs (open loops present vs absent),
/// the composer's output prompt must differ structurally — the OPEN
/// LOOPS section appears/disappears based on input.
/// </summary>
public class ThemeR5OpenLoopsWiringTests
{
    private static ContextSnapshot SnapshotWith(List<OpenLoop> openLoops) => new()
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
        OpenLoops             = openLoops,
        Perceptions           = [],
        RecentHistory         = [],
        SimilarRecentThoughts = [],
        BuiltAt               = DateTimeOffset.UtcNow,
    };

    private static ConversationThread EmptyThread() => new()
    {
        Id            = Guid.NewGuid(),
        StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-1),
        LastMessageAt = DateTimeOffset.UtcNow,
        InitiatedBy   = Roles.Mark,
        Messages      = [],
    };

    // ── Outreach — open-loops section appears/disappears ─────────────

    [Fact]
    public void Outreach_OpenLoopsPresent_SectionAppears()
    {
        var loops = new List<OpenLoop>
        {
            new() { Description = "follow up on Spanish class outcome" },
            new() { Description = "ask about Mia's recital" },
        };
        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(loops), recentThought: "x", reasoning: string.Empty);

        user.Should().Contain("[OPEN LOOPS] unresolved threads with Mark");
        user.Should().Contain("follow up on Spanish class outcome");
        user.Should().Contain("prefer that over an invented topic");
    }

    [Fact]
    public void Outreach_OpenLoopsAbsent_SectionDoesNotAppear()
    {
        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith([]), recentThought: "x", reasoning: string.Empty);

        user.Should().NotContain("[OPEN LOOPS]");
    }

    [Fact]
    public void Outreach_BehaviorDiffers_PresentVsAbsent()
    {
        var (_, withLoops) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith([new() { Description = "x" }]),
            recentThought: "x", reasoning: string.Empty);
        var (_, withoutLoops) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith([]), recentThought: "x", reasoning: string.Empty);

        withLoops.Should().NotBe(withoutLoops,
            "R.0 contract: presence of open-loops must produce different prompt");
    }

    [Fact]
    public void Outreach_OpenLoopsCappedAtThree()
    {
        var loops = Enumerable.Range(1, 5)
            .Select(i => new OpenLoop { Description = $"loop {i}" })
            .ToList();
        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(loops), recentThought: "x", reasoning: string.Empty);

        user.Should().Contain("loop 1");
        user.Should().Contain("loop 3");
        user.Should().NotContain("loop 4",
            "open-loop section caps at 3 to keep prompt budget bounded");
        user.Should().NotContain("loop 5");
    }

    [Fact]
    public void Outreach_OpenLoopsWithEmptyDescription_Filtered()
    {
        var loops = new List<OpenLoop>
        {
            new() { Description = "" },
            new() { Description = "real loop" },
            new() { Description = "   " },
        };
        var (_, user) = PromptBuilder.BuildOutreachMessagePrompt(
            SnapshotWith(loops), recentThought: "x", reasoning: string.Empty);

        user.Should().Contain("real loop");
        user.Should().Contain("[OPEN LOOPS]");
    }

    // ── Reply — same shape ─────────────────────────────────────────

    [Fact]
    public void Reply_OpenLoopsPresent_SectionAppears()
    {
        var loops = new List<OpenLoop>
        {
            new() { Description = "follow up on the conversation about Mia" },
        };
        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith(loops), EmptyThread());

        user.Should().Contain("[OPEN LOOPS] unresolved threads with Mark");
        user.Should().Contain("follow up on the conversation about Mia");
        user.Should().Contain("rather than inventing a topic");
    }

    [Fact]
    public void Reply_OpenLoopsAbsent_SectionDoesNotAppear()
    {
        var (_, user) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith([]), EmptyThread());

        user.Should().NotContain("[OPEN LOOPS]");
    }

    [Fact]
    public void Reply_BehaviorDiffers_PresentVsAbsent()
    {
        var (_, withLoops) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith([new() { Description = "x" }]), EmptyThread());
        var (_, withoutLoops) = PromptBuilder.BuildLeanConversationPrompt(
            SnapshotWith([]), EmptyThread());

        withLoops.Should().NotBe(withoutLoops);
    }
}
