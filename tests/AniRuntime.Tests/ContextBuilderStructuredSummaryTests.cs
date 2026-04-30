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

    // ── Apr 30, 2026 — Facts-tier retrieval IN conversation mode ──────────
    //
    // Pre-Apr-30 bug: tier-scoped retrieval was bypassed entirely in
    // conversation mode, leaving the composition prompt's WHAT IS TRUE
    // section empty. Mark-asserted facts (WCTC, profession, schedule)
    // existed in the Facts tier with importance 1.0 from prior perception
    // writes but were structurally invisible during a live conversation.
    // The model filled the gap with invention (Apr 30 13:17–14:29 tags:
    // "no memory and confabulation", "snow / time / meds / not teaching",
    // "tea? snow? what kids?"). These tests pin the corrected contract.

    [Fact]
    public async Task BuildContextSnapshot_ConversationMode_FactsTierIsRetrieved()
    {
        // SPEC: in conversation mode WITH perceptions (the normal inbound
        // path), Facts-tier retrieval MUST run with the perception summary
        // as the search query. Pre-Apr-30 this was bypassed.
        var wctcFact = new MemoryRecord
        {
            Id         = Guid.NewGuid(),
            Type       = MemoryType.Episodic,
            Content    = "Mark texted: 'haha it's ok hon.. and at night I teach at Waukesha County Technical College which I just shorten to WCTC.'",
            Importance = 1.0f,
            Provenance = EpistemicTier.Facts,
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-35),
        };
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ScoredMemory(wctcFact, 0.85f, 0.82f) });

        var builder = Build(conversation: null);

        var perceptions = new List<PerceptionEvent>
        {
            new()
            {
                Category = PerceptionCategory.Communication,
                Summary  = "I totally wasn't thinking about the fact that I have to teach tonight! Dang!",
                OccurredAt = DateTimeOffset.UtcNow,
            },
        };

        var snapshot = await builder.BuildContextSnapshotAsync(
            perceptions, CancellationToken.None, conversationMode: true);

        snapshot.GroundedFacts.Should().Contain(m => m.Id == wctcFact.Id,
            "Apr 30 fix: conversation-mode Facts retrieval is the upstream cure for the WCTC / profession / teaching-schedule confabulation class.");

        // SPEC contract documentation: the Facts-tier search MUST run with
        // the perception summary as the query when in conversation mode.
        MockMemory.Verify(m => m.SearchByTierAsync(
            It.Is<string>(q => q.Contains("teach tonight")),
            EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Facts-tier search must run with the perception summary as query in conversation mode");
    }

    [Fact]
    public async Task BuildContextSnapshot_ConversationMode_InteriorTierStillSkipped()
    {
        // SPEC: Interior tier remains gated on !conversationMode. Inner-thought
        // and mood-reflection content can pollute conversation composition with
        // stale mood drift; the conversation provides its own emotional context
        // inline. Skipping Interior is the one half of the Apr 30 fix that
        // stayed (Facts opened up; Interior stays closed).
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var builder = Build(conversation: null);

        var perceptions = new List<PerceptionEvent>
        {
            new()
            {
                Category = PerceptionCategory.Communication,
                Summary  = "any plans for tonight?",
                OccurredAt = DateTimeOffset.UtcNow,
            },
        };

        await builder.BuildContextSnapshotAsync(
            perceptions, CancellationToken.None, conversationMode: true);

        MockMemory.Verify(m => m.SearchByTierAsync(
            It.IsAny<string>(), EpistemicTier.Interior, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Interior tier search must NOT run in conversation mode — inner-thought substrate would pollute composition with stale mood drift.");
    }

    [Fact]
    public async Task BuildContextSnapshot_AmbientMode_BothTiersRetrieved()
    {
        // CONTROL: outside conversation mode (ambient cycles), both Facts AND
        // Interior should still flow as before. The Apr 30 fix only changed
        // the conversation-mode behaviour for Facts; ambient behaviour is
        // unchanged.
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Interior, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var builder = Build(conversation: null);

        var perceptions = new List<PerceptionEvent>
        {
            new()
            {
                Category = PerceptionCategory.Internal,
                Summary  = "an ambient observation",
                OccurredAt = DateTimeOffset.UtcNow,
            },
        };

        await builder.BuildContextSnapshotAsync(
            perceptions, CancellationToken.None, conversationMode: false);

        MockMemory.Verify(m => m.SearchByTierAsync(
            It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
        MockMemory.Verify(m => m.SearchByTierAsync(
            It.IsAny<string>(), EpistemicTier.Interior, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Ambient mode preserves the pre-Apr-30 dual-tier retrieval behaviour.");
    }

    [Fact]
    public async Task BuildContextSnapshot_ConversationMode_NoPerceptions_FallsBackToRecentFacts()
    {
        // Edge case: conversation-mode cycle with no perceptions is unusual
        // (should not happen in normal SMS flow) but possible (e.g., probe).
        // When it does happen, fall back to recent Facts via GetByTierAsync.
        var fact = new MemoryRecord
        {
            Id = Guid.NewGuid(),
            Content = "Mark texted earlier",
            Provenance = EpistemicTier.Facts,
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        MockMemory.Setup(m => m.GetByTierAsync(
                EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { fact });

        var builder = Build(conversation: null);

        var snapshot = await builder.BuildContextSnapshotAsync(
            new List<PerceptionEvent>(), CancellationToken.None, conversationMode: true);

        snapshot.GroundedFacts.Should().Contain(m => m.Id == fact.Id);

        MockMemory.Verify(m => m.GetByTierAsync(
            EpistemicTier.Interior, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Interior fallback skipped in conversation mode, same rule as the perception-query path.");
    }
}
