using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 8e (2026-08-19) — verifies
/// <see cref="RecentOutreachContextEnvelope"/> implements
/// <see cref="IRecentOutreachContextEnvelope"/> correctly, exposes the
/// wrapped <see cref="RecentOutreachContext"/> via the UnansweredCount
/// passthrough, and produces the canonical single-value SourceType tag
/// (<c>recent-outreach-context.recent-episodic</c>) matching the sibling
/// envelope kebab-case convention.
/// </summary>
public class RecentOutreachContextEnvelopeTests
{
    private static RecentOutreachContext Sample(int unansweredCount = 0)
        => new()
        {
            RecentMessages = new List<OutreachRecord>
            {
                new() { Message = "hey — thinking of you", SentAt = DateTimeOffset.UtcNow.AddHours(-2), WasAnswered = false },
            },
            UnansweredCount           = unansweredCount,
            TimeSinceLastSend         = TimeSpan.FromHours(2),
            TimeSinceLastContactReply = TimeSpan.FromHours(5),
        };

    [Fact]
    public void Envelope_WrapsContext_ExposesUnansweredCountPassthrough()
    {
        var context = Sample(unansweredCount: 3);
        var env     = new RecentOutreachContextEnvelope { Context = context };

        env.UnansweredCount.Should().Be(3);
        env.Context.Should().BeSameAs(context);
    }

    [Fact]
    public void SourceType_IsCanonicalRecentEpisodicTag()
    {
        // Single-producer surface: hardcoded SourceType, no source enum yet.
        // Kebab-case per sibling-envelope convention (frame.ani-interior,
        // world-seed.circadian, closed-conversation.valid).
        IProvenancedContent<RecentOutreachContext> env = new RecentOutreachContextEnvelope
        {
            Context = Sample(),
        };
        env.SourceType.Should().Be("recent-outreach-context.recent-episodic");
    }

    [Fact]
    public void Producer_Identifies_StateContextBuilder()
    {
        IProvenancedContent<RecentOutreachContext> env = new RecentOutreachContextEnvelope
        {
            Context = Sample(),
        };
        env.Producer.Should().Be("StateContextBuilder");
    }

    [Fact]
    public async Task CreatedAt_IsStableAcrossReads()
    {
        // Sibling-impl discipline (PR #112): captured once at construction.
        IProvenancedContent<RecentOutreachContext> env = new RecentOutreachContextEnvelope
        {
            Context = Sample(),
        };

        var first = env.CreatedAt;
        await Task.Delay(20);
        var second = env.CreatedAt;

        second.Should().Be(first,
            "CreatedAt must be captured once at construction, not recomputed on each read");
    }

    [Fact]
    public void SemanticKey_IsNull()
    {
        // RecentOutreachContext has no embedding surface.
        IProvenancedContent<RecentOutreachContext> env = new RecentOutreachContextEnvelope
        {
            Context = Sample(),
        };
        env.SemanticKey.Should().BeNull();
    }

    [Fact]
    public void Content_ReturnsWrappedContext_SingleSourceOfTruth()
    {
        var context = Sample();
        IProvenancedContent<RecentOutreachContext> env = new RecentOutreachContextEnvelope
        {
            Context = context,
        };
        env.Content.Should().BeSameAs(context,
            "Content is the canonical read path; Context is construction shorthand pointing at the same object");
    }
}
