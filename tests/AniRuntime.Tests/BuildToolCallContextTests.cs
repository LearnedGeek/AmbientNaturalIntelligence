using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #96 (2026-07-15) — pins the tool-call context builder used by
/// <see cref="ConversationReplyPipeline"/> to hand a short conversation
/// snapshot to <see cref="Core.Interfaces.IToolCallClassifier"/>.
///
/// The context is intentionally minimal (last two turns only) — the
/// classifier's job is to decide whether the CURRENT user message needs
/// a tool, not to reason about deep history. Broader retrieval-grounded
/// context lives on the character-model prompt path, not here.
/// </summary>
public class BuildToolCallContextTests
{
    [Fact]
    public void EmptyHistory_ReturnsEmptyString()
    {
        var snapshot = new ContextSnapshot { RecentHistory = new List<ChatMessage>() };
        ConversationReplyPipeline.BuildToolCallContext(snapshot).Should().BeEmpty();
    }

    [Fact]
    public void SingleTurn_ReturnedVerbatim()
    {
        var snapshot = new ContextSnapshot
        {
            RecentHistory = new List<ChatMessage>
            {
                new("user", "do you remember Peru"),
            },
        };
        var ctx = ConversationReplyPipeline.BuildToolCallContext(snapshot);
        ctx.Should().Contain("user: do you remember Peru");
    }

    [Fact]
    public void TakesLastTwoTurnsOnly_OldEntriesDropped()
    {
        var snapshot = new ContextSnapshot
        {
            RecentHistory = new List<ChatMessage>
            {
                new("user",      "way back message"),
                new("assistant", "very old reply"),
                new("user",      "hey"),
                new("assistant", "hi!"),
                new("user",      "do you remember when we went to Peru"),
            },
        };
        var ctx = ConversationReplyPipeline.BuildToolCallContext(snapshot);
        ctx.Should().Contain("do you remember when we went to Peru");
        ctx.Should().Contain("hi!");
        ctx.Should().NotContain("way back message");
        ctx.Should().NotContain("very old reply");
    }

    [Fact]
    public void LongMessages_TruncatedInContext()
    {
        var longContent = new string('x', 500);
        var snapshot = new ContextSnapshot
        {
            RecentHistory = new List<ChatMessage>
            {
                new("assistant", longContent),
                new("user", "recall that thing"),
            },
        };
        var ctx = ConversationReplyPipeline.BuildToolCallContext(snapshot);
        // Kept short — the classifier prompt shouldn't balloon on a
        // pathologically long prior turn.
        ctx.Length.Should().BeLessThan(500);
        ctx.Should().Contain("recall that thing");
    }
}
