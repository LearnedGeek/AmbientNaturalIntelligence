using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.2 step 2 (Apr 27, 2026): coverage for ContextBuilder
/// populating <see cref="ContextSnapshot.StructuredConversationSummary"/>
/// alongside the free-prose RecentConversationSummary. The structured form
/// is sourced directly from IConversationService — no parsing of prose.
/// </summary>
public class ContextBuilderStructuredSummaryTests : AniTestBase
{
    // Theme K Phase K.1 (Apr 28, 2026): strict mock — every call must be
    // explicitly set up. ContextBuilder only calls GetRecentThreadsAsync(1, ct)
    // on this surface, so each test sets that one up.
    private readonly Mock<IConversationService> _mockConversations = new(MockBehavior.Strict);

    private ContextBuilder Build(IConversationService? conversation = null)
    {
        // Same default mock setup the cycle-processor tests rely on.
        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetOpenLoopsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<OpenLoop>());
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetEmotionalStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new EmotionalState());
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalStateSnapshot>());
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new RelationshipHealth { LastCalculated = DateTimeOffset.UtcNow });
        MockMemory.Setup(m => m.GetDesireStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(FreshDesireState());

        var diag = new Mock<IDiagnosticService>();
        diag.SetupGet(d => d.LatestReport).Returns((DiagnosticReport?)null);

        var desire = new DesireEngine(
            MockMemory.Object, MockMemory.Object, DefaultOptions,
            NullLogger<DesireEngine>.Instance);

        return new ContextBuilder(
            MockMemory.Object, MockMemory.Object, MockMemory.Object, MockMemory.Object,
            MockOllama.Object, desire, diag.Object, DefaultOptions,
            NullLogger<ContextBuilder>.Instance,
            originTracker: null,
            conversation: conversation);
    }

    [Fact]
    public async Task BuildContextSnapshot_StructuredSummary_NullWhenConversationServiceMissing()
    {
        var builder = Build(conversation: null);

        var snapshot = await builder.BuildContextSnapshotAsync(
            new List<PerceptionEvent>(), CancellationToken.None);

        snapshot.StructuredConversationSummary.Should().BeNull(
            "no IConversationService was injected — feature gracefully degrades");
    }

    [Fact]
    public async Task BuildContextSnapshot_StructuredSummary_NullWhenNoRecentThread()
    {
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new List<ConversationThread>());
        var builder = Build(_mockConversations.Object);

        var snapshot = await builder.BuildContextSnapshotAsync(
            new List<PerceptionEvent>(), CancellationToken.None);

        snapshot.StructuredConversationSummary.Should().BeNull();
    }

    [Fact]
    public async Task BuildContextSnapshot_StructuredSummary_PopulatedFromMostRecentThread()
    {
        var t1 = new DateTimeOffset(2026, 04, 27, 06, 54, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(1);
        var thread = new ConversationThread
        {
            Id = Guid.NewGuid(),
            StartedAt = t1,
            LastMessageAt = t2,
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Mark, Content = "Hey good morning! How is your day looking?", SentAt = t1 },
                new() { Role = Roles.Ani,  Content = "your morning looks peaceful from what i can see...", SentAt = t2 },
            },
        };
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new List<ConversationThread> { thread });
        var builder = Build(_mockConversations.Object);

        var snapshot = await builder.BuildContextSnapshotAsync(
            new List<PerceptionEvent>(), CancellationToken.None);

        snapshot.StructuredConversationSummary.Should().NotBeNull();
        var summary = snapshot.StructuredConversationSummary!;
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
    public async Task BuildContextSnapshot_StructuredSummary_MapsRoleToCharacterDisplayName()
    {
        // Mark renames the contact / companion mid-conversation: the speaker
        // tag in the structured summary follows the character state's display
        // names, mirroring what BuildThreadSummary does for the prose form.
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var thread = new ConversationThread
        {
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Mark, Content = "hi", SentAt = t1 },
                new() { Role = Roles.Ani,  Content = "hi back", SentAt = t1.AddSeconds(30) },
            },
        };
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new List<ConversationThread> { thread });
        var builder = Build(_mockConversations.Object);

        // Override AFTER Build — the helper sets a default character state and
        // we want a non-default one for this test. Latest Setup wins in Moq.
        MockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new CharacterStateDoc { Name = "Aria", PrimaryContactName = "M" });

        var snapshot = await builder.BuildContextSnapshotAsync(
            new List<PerceptionEvent>(), CancellationToken.None);

        snapshot.StructuredConversationSummary!.Turns[0].Speaker.Should().Be("M");
        snapshot.StructuredConversationSummary!.Turns[1].Speaker.Should().Be("Aria");
    }

    [Fact]
    public async Task BuildContextSnapshot_StructuredSummary_NullWhenThreadHasNoMessages()
    {
        var thread = new ConversationThread { Messages = new List<ConversationMessage>() };
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new List<ConversationThread> { thread });
        var builder = Build(_mockConversations.Object);

        var snapshot = await builder.BuildContextSnapshotAsync(
            new List<PerceptionEvent>(), CancellationToken.None);

        snapshot.StructuredConversationSummary.Should().BeNull(
            "an empty thread is not a meaningful summary — leave the field null");
    }

    [Fact]
    public async Task BuildContextSnapshot_StructuredSummary_FailureInConversationService_DegradesGracefully()
    {
        // The free-prose summary remains the load-bearing surface during the
        // J.2 migration. A failure in the structured path must not break the
        // cycle.
        _mockConversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                          .ThrowsAsync(new InvalidOperationException("simulated DB failure"));
        var builder = Build(_mockConversations.Object);

        var snapshot = await builder.BuildContextSnapshotAsync(
            new List<PerceptionEvent>(), CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot.StructuredConversationSummary.Should().BeNull();
    }
}
