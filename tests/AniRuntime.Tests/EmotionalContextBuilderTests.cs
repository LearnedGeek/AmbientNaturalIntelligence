using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Context;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Coverage for <see cref="EmotionalContextBuilder"/>, the first sub-builder
/// of the ContextBuilder SRP decomposition (extracted 2026-05-19 per
/// `ANI-Testability-Architecture-Plan.md` §2). Verifies behavior preservation
/// from the pre-extraction inline code in <c>ContextBuilder</c>:
/// independent degradation per concern, stale-recalc trigger at >24h,
/// drift threshold at ≥4 history records, pattern-awareness minimum at 3
/// embedded outreach records.
/// </summary>
public class EmotionalContextBuilderTests : AniTestBase
{
    private EmotionalContextBuilder Build() => new(
        MockMemory.Object, MockMemory.Object, MockMemory.Object, MockMemory.Object,
        DefaultOptions,
        NullLogger<EmotionalContextBuilder>.Instance);

    [Fact]
    public async Task BuildAsync_ReturnsAllNullOrEmpty_WhenAllRetrievalsFail()
    {
        // Strict mock with no setups — every call throws MockException, which the
        // try/catch blocks in BuildAsync swallow individually. ProcessedThemes is
        // the only call without try/catch; the underlying mock must succeed.
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("simulated"));
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("simulated"));
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("simulated"));
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());

        var result = (await Build().BuildAsync("Ani", new EmotionalState(), CancellationToken.None)).Content;

        result.RelationshipHealth.Should().BeNull("relationship-health load was wrapped in try/catch and degrades to null");
        result.EmotionalDrift.Should().BeNull("drift detection was wrapped in try/catch and degrades to null");
        result.PatternAwareness.Should().BeNull("pattern analysis was wrapped in try/catch and degrades to null");
        result.ProcessedThemes.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_RelationshipHealth_LoadedFresh_WhenLastCalculatedRecent()
    {
        var fresh = new RelationshipHealth
        {
            ConnectionScore = 0.62,
            Phase           = "steady",
            LastCalculated  = DateTimeOffset.UtcNow.AddHours(-1),
        };
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(fresh);
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalStateSnapshot>());
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());

        var result = (await Build().BuildAsync("Ani", new EmotionalState(), CancellationToken.None)).Content;

        result.RelationshipHealth.Should().BeSameAs(fresh, "fresh value (<24h old) is returned without recalculation");
        MockMemory.Verify(m => m.SaveRelationshipHealthAsync(
            It.IsAny<RelationshipHealth>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task BuildAsync_RelationshipHealth_RecalculatedAndPersisted_WhenStale()
    {
        var stale = new RelationshipHealth
        {
            ConnectionScore = 0.5,
            Phase           = "steady",
            LastCalculated  = DateTimeOffset.UtcNow.AddHours(-25),  // >24h old → triggers recalc
        };
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(stale);
        MockMemory.Setup(m => m.GetRecentMessageCountAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(6);  // 6 over RelationshipHealthWindowDays
        MockMemory.Setup(m => m.GetAverageConversationValenceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(0.7f);
        MockMemory.Setup(m => m.GetInitiativeBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((5, 5));  // perfectly balanced
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalStateSnapshot>());  // empty → warmth=0.5 default
        MockMemory.Setup(m => m.SaveRelationshipHealthAsync(
                It.IsAny<RelationshipHealth>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());

        var result = (await Build().BuildAsync("Ani", new EmotionalState(), CancellationToken.None)).Content;

        result.RelationshipHealth.Should().NotBeNull();
        result.RelationshipHealth!.ConnectionScore.Should().BeGreaterThan(0.0)
            .And.BeLessThan(1.0, "composite of four sub-scores, none extreme");
        MockMemory.Verify(m => m.SaveRelationshipHealthAsync(
            It.IsAny<RelationshipHealth>(), It.IsAny<CancellationToken>()), Times.Once(),
            "recalculated health is persisted before the result is returned");
    }

    [Fact]
    public async Task BuildAsync_EmotionalDrift_NullWhenHistoryTooShort()
    {
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new RelationshipHealth { LastCalculated = DateTimeOffset.UtcNow });
        var snap = new EmotionalStateSnapshot(
            Warmth: 0.5f, Energy: 0.5f, Worry: 0.2f, Playfulness: 0.5f,
            ContactGapTension: 0f, RecordedAt: DateTimeOffset.UtcNow);
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalStateSnapshot> { snap, snap, snap });  // only 3 records — below the 4-record threshold
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());

        var result = (await Build().BuildAsync("Ani", new EmotionalState(), CancellationToken.None)).Content;

        result.EmotionalDrift.Should().BeNull("drift detection requires at least 4 history records to split into halves");
    }

    [Fact]
    public async Task BuildAsync_PatternAwareness_NullWhenFewerThanThreeOutreachWithEmbeddings()
    {
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new RelationshipHealth { LastCalculated = DateTimeOffset.UtcNow });
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalStateSnapshot>());
        // Two outreach records — below the 3-record minimum for pattern analysis.
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.Episodic, 20, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.Episodic, Content = "I reached out to Mark: \"hi\"",
                              Embedding = new float[] { 0.5f, 0.5f, 0.5f } },
                      new() { Type = MemoryType.Episodic, Content = "I reached out to Mark: \"hey\"",
                              Embedding = new float[] { 0.5f, 0.5f, 0.5f } },
                  });
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());

        var result = (await Build().BuildAsync("Ani", new EmotionalState(), CancellationToken.None)).Content;

        result.PatternAwareness.Should().BeNull("pattern analysis requires ≥3 outreach records with embeddings");
    }

    [Fact]
    public async Task BuildAsync_PatternAwareness_NotSurfacedBelow075SimilarityThreshold()
    {
        // Three outreach records with orthogonal embeddings — average pairwise
        // cosine similarity will be 0, well below the 0.75 threshold.
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new RelationshipHealth { LastCalculated = DateTimeOffset.UtcNow });
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalStateSnapshot>());
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.Episodic, 20, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.Episodic, Content = "I reached out to Mark: \"a\"",
                              Embedding = new float[] { 1f, 0f, 0f } },
                      new() { Type = MemoryType.Episodic, Content = "I reached out to Mark: \"b\"",
                              Embedding = new float[] { 0f, 1f, 0f } },
                      new() { Type = MemoryType.Episodic, Content = "I reached out to Mark: \"c\"",
                              Embedding = new float[] { 0f, 0f, 1f } },
                  });
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());

        var result = (await Build().BuildAsync("Ani", new EmotionalState(), CancellationToken.None)).Content;

        result.PatternAwareness.Should().BeNull("orthogonal embeddings → 0 similarity, below 0.75 threshold");
    }

    [Fact]
    public async Task BuildAsync_PatternAwareness_SurfacedAbove075SimilarityThreshold()
    {
        // Three outreach records with nearly-identical embeddings — pairwise
        // cosine similarity will be ~1.0, well above the 0.75 threshold.
        var nearlyIdentical = new float[] { 0.9f, 0.4f, 0.2f };
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new RelationshipHealth { LastCalculated = DateTimeOffset.UtcNow });
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalStateSnapshot>());
        MockMemory.Setup(m => m.GetByTypeAsync(MemoryType.Episodic, 20, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<MemoryRecord>
                  {
                      new() { Type = MemoryType.Episodic, Content = "I reached out to Mark: \"thinking of you\"",
                              Embedding = nearlyIdentical },
                      new() { Type = MemoryType.Episodic, Content = "I reached out to Mark: \"missing you\"",
                              Embedding = nearlyIdentical },
                      new() { Type = MemoryType.Episodic, Content = "I reached out to Mark: \"with you\"",
                              Embedding = nearlyIdentical },
                  });
        MockMemory.Setup(m => m.GetProcessedThemesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<string>());

        var result = (await Build().BuildAsync("Ani", new EmotionalState(), CancellationToken.None)).Content;

        result.PatternAwareness.Should().NotBeNull("identical embeddings → ~1.0 similarity, above 0.75 threshold");
        result.PatternAwareness.Should().Contain("thematically similar",
            "the awareness string names the observed repetition");
    }

    [Fact]
    public async Task BuildAsync_ProcessedThemes_AlwaysPopulatedFromAnalytics()
    {
        var themes = new List<string> { "starbucks-coffee", "spanish-lessons", "winter-walks" };
        MockMemory.Setup(m => m.GetRelationshipHealthAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new RelationshipHealth { LastCalculated = DateTimeOffset.UtcNow });
        MockMemory.Setup(m => m.GetEmotionalHistoryAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<EmotionalStateSnapshot>());
        MockMemory.Setup(m => m.GetByTypeAsync(It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<MemoryRecord>());
        MockMemory.Setup(m => m.GetProcessedThemesAsync(5, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(themes);

        var result = (await Build().BuildAsync("Ani", new EmotionalState(), CancellationToken.None)).Content;

        result.ProcessedThemes.Should().BeEquivalentTo(themes,
            "processed themes are passed through from IMemoryAnalytics with no transformation");
    }
}
