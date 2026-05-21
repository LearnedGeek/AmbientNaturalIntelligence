using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Context;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Coverage for <see cref="ConversationContextBuilder"/>, the third sub-builder
/// of the ContextBuilder SRP decomposition (extracted 2026-05-19 per
/// `ANI-Testability-Architecture-Plan.md` §2). Tests previously lived in
/// <c>ContextBuilderStructuredSummaryTests</c> and exercised the same logic
/// through the orchestrator; they're now direct unit tests against the
/// extracted sub-builder.
/// </summary>
public class ConversationContextBuilderTests : AniTestBase
{
    private readonly Mock<IConversationService> _mockConversations = new(MockBehavior.Strict);
    private readonly Mock<IClosedConversationStore> _mockClosedStore = new(MockBehavior.Strict);

    private static IOptions<AniOptions> Options() => Microsoft.Extensions.Options.Options.Create(
        new AniOptions { GuardRefactorBaselineLoggingEnabled = false });

    private ConversationContextBuilder Build(
        IConversationService?       conversation = null,
        IClosedConversationStore?   closedStore  = null)
        => new(Options(),
               NullLogger<ConversationContextBuilder>.Instance,
               conversation: conversation,
               closedStore:  closedStore);

    private static CharacterStateDoc DefaultChar() =>
        new() { Name = "Ani", PrimaryContactName = "Mark" };

    // ─── RecentConversationSummary (prose extraction) ─────────────────────

    [Fact]
    public async Task BuildAsync_RecentConversationSummary_ExtractedFromEpisodicByPrefix()
    {
        var recent = new List<MemoryRecord>
        {
            new()
            {
                Type    = MemoryType.Episodic,
                Content = $"{MemoryPrefixes.ConversationSummary}\nMark and Ani caught up about the day.",
            },
            new()
            {
                Type    = MemoryType.Episodic,
                Content = "Some other episodic memory that should not match.",
            },
        };

        var result = await Build().BuildAsync(DefaultChar(), recent, CancellationToken.None);

        result.RecentConversationSummary.Should().StartWith(MemoryPrefixes.ConversationSummary);
    }

    [Fact]
    public async Task BuildAsync_RecentConversationSummary_NullWhenNoMatch()
    {
        var recent = new List<MemoryRecord>
        {
            new() { Type = MemoryType.Episodic, Content = "just some episode" },
            new() { Type = MemoryType.InnerThought, Content = "just a thought" },
        };

        var result = await Build().BuildAsync(DefaultChar(), recent, CancellationToken.None);

        result.RecentConversationSummary.Should().BeNull();
    }

    // ─── StructuredConversationSummary ─────────────────────────────────────

    [Fact]
    public async Task BuildAsync_StructuredSummary_NullWhenConversationServiceMissing()
    {
        var result = await Build(conversation: null).BuildAsync(
            DefaultChar(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.StructuredConversationSummary.Should().BeNull(
            "no IConversationService was injected — feature gracefully degrades");
    }

    [Fact]
    public async Task BuildAsync_StructuredSummary_NullWhenNoRecentThread()
    {
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new List<ConversationThread>());

        var result = await Build(_mockConversations.Object).BuildAsync(
            DefaultChar(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.StructuredConversationSummary.Should().BeNull();
    }

    [Fact]
    public async Task BuildAsync_StructuredSummary_PopulatedFromMostRecentThread()
    {
        var t1 = new DateTimeOffset(2026, 04, 27, 06, 54, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(1);
        var thread = new ConversationThread
        {
            Id            = Guid.NewGuid(),
            StartedAt     = t1,
            LastMessageAt = t2,
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Mark, Content = "Hey good morning! How is your day looking?", SentAt = t1 },
                new() { Role = Roles.Ani,  Content = "your morning looks peaceful from what i can see...", SentAt = t2 },
            },
        };
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new List<ConversationThread> { thread });

        var result = await Build(_mockConversations.Object).BuildAsync(
            DefaultChar(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.StructuredConversationSummary.Should().NotBeNull();
        var summary = result.StructuredConversationSummary!;
        summary.Turns.Should().HaveCount(2);
        summary.Turns[0].Speaker.Should().Be("Mark");
        summary.Turns[0].Content.Should().Be("Hey good morning! How is your day looking?");
        summary.Turns[0].At.Should().Be(t1);
        summary.Turns[1].Speaker.Should().Be("Ani");
        summary.Turns[1].Content.Should().Be("your morning looks peaceful from what i can see...");
        summary.Turns[1].At.Should().Be(t2);
        summary.FirstTurnAt.Should().Be(t1);
        summary.LastTurnAt.Should().Be(t2);
    }

    [Fact]
    public async Task BuildAsync_StructuredSummary_MapsRoleToCharacterDisplayName()
    {
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var thread = new ConversationThread
        {
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Mark, Content = "hi",      SentAt = t1 },
                new() { Role = Roles.Ani,  Content = "hi back", SentAt = t1.AddSeconds(30) },
            },
        };
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new List<ConversationThread> { thread });

        var renamed = new CharacterStateDoc { Name = "Aria", PrimaryContactName = "M" };

        var result = await Build(_mockConversations.Object).BuildAsync(
            renamed, Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.StructuredConversationSummary!.Turns[0].Speaker.Should().Be("M");
        result.StructuredConversationSummary!.Turns[1].Speaker.Should().Be("Aria");
    }

    [Fact]
    public async Task BuildAsync_StructuredSummary_NullWhenThreadHasNoMessages()
    {
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new List<ConversationThread> { thread });

        var result = await Build(_mockConversations.Object).BuildAsync(
            DefaultChar(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.StructuredConversationSummary.Should().BeNull(
            "an empty thread is not a meaningful summary — leave the field null");
    }

    [Fact]
    public async Task BuildAsync_StructuredSummary_FailureInConversationService_DegradesGracefully()
    {
        // The free-prose summary remains the load-bearing surface during the
        // J.2 migration. A failure in the structured path must not break the
        // cycle.
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ThrowsAsync(new InvalidOperationException("simulated DB failure"));

        var result = await Build(_mockConversations.Object).BuildAsync(
            DefaultChar(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.Should().NotBeNull();
        result.StructuredConversationSummary.Should().BeNull();
    }

    // ─── RecentClosedConversation (Vibe Loop V1.4) ────────────────────────

    [Fact]
    public async Task BuildAsync_RecentClosed_NullWhenClosedStoreMissing()
    {
        var result = await Build(closedStore: null).BuildAsync(
            DefaultChar(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.RecentClosedConversation.Should().BeNull();
    }

    [Fact]
    public async Task BuildAsync_RecentClosed_LoadedFromStore()
    {
        var record = new ClosedConversationRecord
        {
            ThreadId = Guid.NewGuid(),
            Gist     = "A warm evening of co-imagined cooking.",
            ClosedAt = DateTimeOffset.UtcNow.AddHours(-2),
        };
        _mockClosedStore.Setup(s => s.GetRecentAsync(1, It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new List<ClosedConversationRecord> { record });

        var result = await Build(closedStore: _mockClosedStore.Object).BuildAsync(
            DefaultChar(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.RecentClosedConversation.Should().BeSameAs(record);
    }

    [Fact]
    public async Task BuildAsync_RecentClosed_DegradesToNullOnFailure()
    {
        _mockClosedStore.Setup(s => s.GetRecentAsync(1, It.IsAny<CancellationToken>()))
                        .ThrowsAsync(new InvalidOperationException("simulated store failure"));

        var result = await Build(closedStore: _mockClosedStore.Object).BuildAsync(
            DefaultChar(), Array.Empty<MemoryRecord>(), CancellationToken.None);

        result.RecentClosedConversation.Should().BeNull(
            "outreach falls back to structured-summary path when the closed-store fails");
    }
}
