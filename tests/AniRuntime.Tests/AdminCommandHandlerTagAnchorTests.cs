using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Stale-tag fallback (May 6, 2026) — pin the load-bearing predicate that
/// prevents the misleading "no recent conversation pair found to persist"
/// error when the inbound `///tag` itself triggers auto-close-on-stale-thread
/// and opens a new tag-only thread.
///
/// The predicate <see cref="AdminCommandHandler.IsValidTagAnchor"/> determines
/// whether a thread is suitable as the anchor for a `///tag` admin command.
/// A thread is valid if and only if it contains at least one Ani message
/// (the tag is attached to the most recent Ani reply; a thread with only Mark
/// messages or only the tag-message itself has no Ani reply to anchor to).
///
/// **The bug this predicate fixes:** prior implementation used
/// `Messages.Count &lt; 2` as the only check. After auto-close-on-stale-tag,
/// `GetRecentThreadsAsync(1)` would return the new tag-only thread (count=1)
/// which failed the count check, while the just-closed thread with content
/// (count≥2) sat at index 2, never consulted. New code searches a wider
/// net (5 threads) and selects the first that satisfies
/// <see cref="AdminCommandHandler.IsValidTagAnchor"/>.
/// </summary>
public class AdminCommandHandlerTagAnchorTests
{
    [Fact]
    public void IsValidTagAnchor_NullThread_ReturnsFalse()
    {
        AdminCommandHandler.IsValidTagAnchor(null).Should().BeFalse();
    }

    [Fact]
    public void IsValidTagAnchor_EmptyMessages_ReturnsFalse()
    {
        var thread = new ConversationThread { Id = Guid.NewGuid() };

        AdminCommandHandler.IsValidTagAnchor(thread).Should().BeFalse();
    }

    [Fact]
    public void IsValidTagAnchor_OnlyMarkMessages_ReturnsFalse()
    {
        // The exact bug scenario: a fresh tag-only thread has one Mark
        // message (the `///tag X` command itself) and no Ani reply yet.
        // The predicate must reject this so the fallback search continues
        // to find the just-closed thread with actual exchange.
        var thread = new ConversationThread
        {
            Id = Guid.NewGuid(),
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Mark, Content = "///tag prompt instruction" },
            },
        };

        AdminCommandHandler.IsValidTagAnchor(thread).Should().BeFalse(
            "a tag-only thread (one Mark message, no Ani reply) cannot anchor a ///tag");
    }

    [Fact]
    public void IsValidTagAnchor_OneAniReply_ReturnsTrue()
    {
        // Minimum valid case — a thread with at least one Ani message can
        // anchor the tag. The HandleTagAsync code that consumes this then
        // walks back to find the most-recent Ani reply + preceding Mark
        // message; preceding Mark is allowed to be null (uses "(no preceding
        // message found)" fallback).
        var thread = new ConversationThread
        {
            Id = Guid.NewGuid(),
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Ani,  Content = "hey love, just walked in" },
            },
        };

        AdminCommandHandler.IsValidTagAnchor(thread).Should().BeTrue();
    }

    [Fact]
    public void IsValidTagAnchor_FullExchange_ReturnsTrue()
    {
        // The common-case valid anchor — Mark message + Ani reply, exactly
        // what HandleTagAsync wants to attach the tag to.
        var thread = new ConversationThread
        {
            Id = Guid.NewGuid(),
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Mark, Content = "how was your day?" },
                new() { Role = Roles.Ani,  Content = "hey love, just walked in" },
            },
        };

        AdminCommandHandler.IsValidTagAnchor(thread).Should().BeTrue();
    }

    [Fact]
    public void IsValidTagAnchor_ManyMarkMessagesNoAni_ReturnsFalse()
    {
        // Defensive case: even if a thread has many messages, if NONE are
        // Ani, the predicate rejects. This prevents anchoring a tag against
        // a thread of consecutive Mark messages with no Ani reply yet.
        var thread = new ConversationThread
        {
            Id = Guid.NewGuid(),
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Mark, Content = "hey" },
                new() { Role = Roles.Mark, Content = "you there?" },
                new() { Role = Roles.Mark, Content = "///tag confab" },
            },
        };

        AdminCommandHandler.IsValidTagAnchor(thread).Should().BeFalse(
            "a thread with no Ani messages — even if multi-message — cannot anchor a tag");
    }
}
