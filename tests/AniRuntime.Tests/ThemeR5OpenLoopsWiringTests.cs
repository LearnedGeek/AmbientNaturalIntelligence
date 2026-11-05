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

    // ── Reply — open-loops section appears/disappears ──────────────
    // (Outreach tests removed when OutreachMessagePromptCommand was retired
    // under Theme K.3; Audit Root B 2026-11-05. Composition-time injection
    // of [OPEN LOOPS] was retired with the composer — the live outreach
    // path calls ChatAsync with an empty system prompt and recentThought
    // as the user message, with no open-loops rendering. The tests below
    // cover Reply composition only; there is no equivalent outreach
    // composition-time assertion to carry forward.)

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
