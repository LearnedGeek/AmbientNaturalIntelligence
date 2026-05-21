using AniRuntime.Core.Models;
using AniRuntime.Loops.Context;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Coverage for <see cref="RetrievalContextBuilder"/>, the second sub-builder
/// of the ContextBuilder SRP decomposition (extracted 2026-05-19 per
/// `ANI-Testability-Architecture-Plan.md` §2). Verifies behavior preservation
/// from the pre-extraction inline code in <c>ContextBuilder</c>:
/// recent memory concat shape, anchored degradation on failure, perception-
/// driven semantic search, similar-thought two-pronged detection, and
/// diversity re-rank fail-open behavior.
/// </summary>
public class RetrievalContextBuilderTests : AniTestBase
{
    private RetrievalContextBuilder Build() => new(
        MockMemory.Object, MockOllama.Object,
        NullLogger<RetrievalContextBuilder>.Instance);

    [Fact]
    public async Task BuildAsync_NoPerceptions_ReturnsRecentAndAnchoredButEmptyRelevant()
    {
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.Episodic, 10, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.Episodic, Content = "recent episodic" },
                  });
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.InnerThought, 5, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.InnerThought, Content = "recent thought" },
                  });
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.Semantic, Content = "anchored foundation" },
                  });
        // Prong 2 of similar-thought detection fires because recentMem has an
        // inner-thought entry. Set up the search call so the prong completes
        // cleanly under strict-mock and returns nothing extra to surface.
        MockMemory.Setup(m => m.SearchByTypeAsync(
                It.IsAny<string>(), MemoryType.InnerThought, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryRecord>());

        var result = await Build().BuildAsync(
            Array.Empty<PerceptionEvent>(), CancellationToken.None);

        result.RecentMemory.Should().HaveCount(2, "episodic + inner-thought concat");
        result.AnchoredMemories.Should().ContainSingle();
        result.RelevantMemory.Should().BeEmpty("no perceptions → no semantic search query");
        result.SimilarRecentThoughts.Should().BeEmpty(
            "prong 1 skips without perceptions; prong 2 ran but returned no hits");
    }

    [Fact]
    public async Task BuildAsync_AnchoredFails_DegradesToEmpty_OthersStillSucceed()
    {
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.Episodic, 10, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.InnerThought, 5, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("simulated anchored load failure"));

        var result = await Build().BuildAsync(
            Array.Empty<PerceptionEvent>(), CancellationToken.None);

        result.AnchoredMemories.Should().BeEmpty(
            "anchored-load failure was wrapped in try/catch and degrades to empty");
        result.RecentMemory.Should().BeEmpty();
        result.RelevantMemory.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_WithPerceptions_RunsSemanticSearch()
    {
        var hit = new MemoryRecord { Type = MemoryType.Semantic, Content = "relevant memory hit" };
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<MemoryRecord> { hit });
        MockMemory.Setup(m => m.SearchByTypeAsync(
                It.IsAny<string>(), MemoryType.InnerThought, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryRecord>());

        var result = await Build().BuildAsync(
            new[] { new PerceptionEvent { Summary = "something happened" } },
            CancellationToken.None);

        result.RelevantMemory.Should().ContainSingle(m => m.Content == "relevant memory hit");
    }

    [Fact]
    public async Task BuildAsync_SemanticSearchFails_DegradesToEmptyRelevant()
    {
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("simulated search failure"));
        MockMemory.Setup(m => m.SearchByTypeAsync(
                It.IsAny<string>(), MemoryType.InnerThought, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryRecord>());

        var result = await Build().BuildAsync(
            new[] { new PerceptionEvent { Summary = "anything" } },
            CancellationToken.None);

        result.RelevantMemory.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_RecentMemory_IsEpisodicPlusInnerThoughtConcat()
    {
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.Episodic, 10, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.Episodic, Content = "e1" },
                      new() { Type = MemoryType.Episodic, Content = "e2" },
                  });
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.InnerThought, 5, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.InnerThought, Content = "t1" },
                  });
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.SearchByTypeAsync(
                It.IsAny<string>(), MemoryType.InnerThought, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryRecord>());

        var result = await Build().BuildAsync(
            Array.Empty<PerceptionEvent>(), CancellationToken.None);

        result.RecentMemory.Should().HaveCount(3);
        result.RecentMemory[0].Content.Should().Be("e1");
        result.RecentMemory[1].Content.Should().Be("e2");
        result.RecentMemory[2].Content.Should().Be("t1");
    }

    [Fact]
    public async Task BuildAsync_DiversityRerank_PrefersNoveltyOverEcho()
    {
        // Two candidates: one similar to the recent thought, one orthogonal.
        // The orthogonal one should rank first after diversity re-rank.
        var thoughtEmbed = new float[] { 1f, 0f, 0f };
        var echoEmbed    = new float[] { 1f, 0f, 0f };  // identical to thought → low novelty
        var novelEmbed   = new float[] { 0f, 1f, 0f };  // orthogonal → high novelty

        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.Episodic, 10, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.InnerThought, 5, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.InnerThought, Content = "thought", Embedding = thoughtEmbed },
                  });
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<MemoryRecord>
            {
                new() { Type = MemoryType.Semantic, Content = "echo-of-thought", Embedding = echoEmbed },
                new() { Type = MemoryType.Semantic, Content = "novel-content",   Embedding = novelEmbed },
            });
        MockMemory.Setup(m => m.SearchByTypeAsync(
                It.IsAny<string>(), MemoryType.InnerThought, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryRecord>());

        var result = await Build().BuildAsync(
            new[] { new PerceptionEvent { Summary = "x" } },
            CancellationToken.None);

        result.RelevantMemory.Should().HaveCount(2);
        result.RelevantMemory[0].Content.Should().Be("novel-content",
            "orthogonal embedding ranks first; the echo of the recent thought ranks last");
    }

    [Fact]
    public async Task BuildAsync_SimilarThoughts_TwoProngedDetection()
    {
        var prong1Hit = new MemoryRecord { Type = MemoryType.InnerThought, Content = "perception-similar" };
        var prong2Hit = new MemoryRecord { Type = MemoryType.InnerThought, Content = "last-thought-similar" };
        var lastThought = new MemoryRecord
        {
            Type       = MemoryType.InnerThought,
            Content    = "the latest thought",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.Episodic, 10, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.InnerThought, 5, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord> { lastThought });
        MockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>());
        MockMemory.Setup(m => m.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<MemoryRecord>());
        // Prong 1 — perception query → similar inner thoughts.
        MockMemory.Setup(m => m.SearchByTypeAsync(
                "a perception", MemoryType.InnerThought, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryRecord> { prong1Hit });
        // Prong 2 — last-thought content → older similar thoughts.
        MockMemory.Setup(m => m.SearchByTypeAsync(
                "the latest thought", MemoryType.InnerThought, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryRecord> { prong2Hit });

        var result = await Build().BuildAsync(
            new[] { new PerceptionEvent { Summary = "a perception" } },
            CancellationToken.None);

        result.SimilarRecentThoughts.Should().HaveCount(2,
            "both prongs surface a hit; the last-thought itself is excluded via id dedup");
        result.SimilarRecentThoughts.Should().Contain(m => m.Content == "perception-similar");
        result.SimilarRecentThoughts.Should().Contain(m => m.Content == "last-thought-similar");
    }
}
