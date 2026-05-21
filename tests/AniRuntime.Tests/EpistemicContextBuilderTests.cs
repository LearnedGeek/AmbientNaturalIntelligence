using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Context;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Coverage for <see cref="EpistemicContextBuilder"/>, the fourth sub-builder
/// of the ContextBuilder SRP decomposition (extracted 2026-05-19 per
/// `ANI-Testability-Architecture-Plan.md` §2). Pins the Apr 30, 2026 fix
/// behavior (Facts-tier retrieval re-enabled in conversation mode while
/// Interior stays gated off) and the anchored-merge-into-Facts contract.
///
/// Tests previously lived in <c>ContextBuilderStructuredSummaryTests</c> and
/// exercised the same logic through the orchestrator; they're now direct
/// unit tests against the extracted sub-builder.
/// </summary>
public class EpistemicContextBuilderTests : AniTestBase
{
    private EpistemicContextBuilder Build() => new(
        MockMemory.Object,
        Options.Create(new AniOptions { MinCosineThresholdComposition = 0.0 }),
        NullLogger<EpistemicContextBuilder>.Instance);

    // ── Apr 30, 2026 — Facts-tier retrieval IN conversation mode ──────────
    //
    // Pre-Apr-30 bug: tier-scoped retrieval was bypassed entirely in
    // conversation mode, leaving the composition prompt's WHAT IS TRUE
    // section empty. Mark-asserted facts (WCTC, profession, schedule)
    // existed in the Facts tier with importance 1.0 from prior perception
    // writes but were structurally invisible during a live conversation.
    // The model filled the gap with invention (Apr 30 13:17–14:29 tags:
    // "no memory and confabulation", "snow / time / meds / not teaching").
    // These tests pin the corrected contract.

    [Fact]
    public async Task BuildAsync_ConversationMode_FactsTierIsRetrieved()
    {
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
                It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[] { new ScoredMemory(wctcFact, 0.85f, 0.82f) });

        var perceptions = new List<PerceptionEvent>
        {
            new()
            {
                Category = PerceptionCategory.Communication,
                Summary  = "I totally wasn't thinking about the fact that I have to teach tonight! Dang!",
                OccurredAt = DateTimeOffset.UtcNow,
            },
        };

        var result = await Build().BuildAsync(
            perceptions, Array.Empty<MemoryRecord>(), conversationMode: true, CancellationToken.None);

        result.GroundedFacts.Should().Contain(m => m.Id == wctcFact.Id,
            "Apr 30 fix: conversation-mode Facts retrieval is the upstream cure for the WCTC / profession / teaching-schedule confabulation class.");

        MockMemory.Verify(m => m.SearchByTierAsync(
            It.Is<string>(q => q.Contains("teach tonight")),
            EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>(),
            It.IsAny<float>()),
            Times.Once,
            "Facts-tier search must run with the perception summary as query in conversation mode");
    }

    [Fact]
    public async Task BuildAsync_ConversationMode_InteriorTierStillSkipped()
    {
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var perceptions = new List<PerceptionEvent>
        {
            new()
            {
                Category = PerceptionCategory.Communication,
                Summary  = "any plans for tonight?",
                OccurredAt = DateTimeOffset.UtcNow,
            },
        };

        await Build().BuildAsync(
            perceptions, Array.Empty<MemoryRecord>(), conversationMode: true, CancellationToken.None);

        MockMemory.Verify(m => m.SearchByTierAsync(
            It.IsAny<string>(), EpistemicTier.Interior, It.IsAny<int>(), It.IsAny<CancellationToken>(),
            It.IsAny<float>()),
            Times.Never,
            "Interior tier search must NOT run in conversation mode — inner-thought substrate would pollute composition with stale mood drift.");
    }

    [Fact]
    public async Task BuildAsync_AmbientMode_BothTiersRetrieved()
    {
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Interior, It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var perceptions = new List<PerceptionEvent>
        {
            new()
            {
                Category = PerceptionCategory.Internal,
                Summary  = "an ambient observation",
                OccurredAt = DateTimeOffset.UtcNow,
            },
        };

        await Build().BuildAsync(
            perceptions, Array.Empty<MemoryRecord>(), conversationMode: false, CancellationToken.None);

        MockMemory.Verify(m => m.SearchByTierAsync(
            It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>(),
            It.IsAny<float>()),
            Times.Once);
        MockMemory.Verify(m => m.SearchByTierAsync(
            It.IsAny<string>(), EpistemicTier.Interior, It.IsAny<int>(), It.IsAny<CancellationToken>(),
            It.IsAny<float>()),
            Times.Once,
            "Ambient mode preserves the pre-Apr-30 dual-tier retrieval behaviour.");
    }

    [Fact]
    public async Task BuildAsync_ConversationMode_NoPerceptions_FallsBackToRecentFacts()
    {
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

        var result = await Build().BuildAsync(
            Array.Empty<PerceptionEvent>(), Array.Empty<MemoryRecord>(),
            conversationMode: true, CancellationToken.None);

        result.GroundedFacts.Should().Contain(m => m.Id == fact.Id);

        MockMemory.Verify(m => m.GetByTierAsync(
            EpistemicTier.Interior, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Interior fallback skipped in conversation mode, same rule as the perception-query path.");
    }

    [Fact]
    public async Task BuildAsync_AnchoredMemories_AreMergedIntoFactsPool()
    {
        var anchoredId = Guid.NewGuid();
        var anchored = new MemoryRecord
        {
            Id      = anchoredId,
            Content = "anchored foundation memory",
        };
        var queryHit = new MemoryRecord
        {
            Id      = Guid.NewGuid(),
            Content = "perception-relevant fact",
        };
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[] { new ScoredMemory(queryHit, 0.8f, 0.7f) });

        var result = await Build().BuildAsync(
            new[] { new PerceptionEvent { Summary = "anything" } },
            new[] { anchored },
            conversationMode: true, CancellationToken.None);

        result.GroundedFacts.Should().HaveCount(2);
        result.GroundedFacts[0].Id.Should().Be(anchoredId,
            "anchored memories are inserted at the front of the Facts pool");
        result.GroundedFacts[1].Id.Should().Be(queryHit.Id);
    }

    [Fact]
    public async Task BuildAsync_AnchoredAlreadyInFacts_NotDuplicated()
    {
        var sharedId = Guid.NewGuid();
        var anchored = new MemoryRecord { Id = sharedId, Content = "shared" };
        var queryHit = new MemoryRecord { Id = sharedId, Content = "shared" };

        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), EpistemicTier.Facts, It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<float>()))
            .ReturnsAsync(new[] { new ScoredMemory(queryHit, 0.9f, 0.8f) });

        var result = await Build().BuildAsync(
            new[] { new PerceptionEvent { Summary = "anything" } },
            new[] { anchored },
            conversationMode: true, CancellationToken.None);

        result.GroundedFacts.Should().HaveCount(1, "id-based dedup prevents duplicate insertion");
    }

    [Fact]
    public async Task BuildAsync_AllRetrievalsThrow_DegradesToEmptyPools()
    {
        MockMemory.Setup(m => m.SearchByTierAsync(
                It.IsAny<string>(), It.IsAny<EpistemicTier>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<float>()))
            .ThrowsAsync(new InvalidOperationException("simulated retrieval failure"));

        var result = await Build().BuildAsync(
            new[] { new PerceptionEvent { Summary = "x" } },
            Array.Empty<MemoryRecord>(),
            conversationMode: false, CancellationToken.None);

        result.GroundedFacts.Should().BeEmpty();
        result.InteriorContext.Should().BeEmpty();
    }
}
