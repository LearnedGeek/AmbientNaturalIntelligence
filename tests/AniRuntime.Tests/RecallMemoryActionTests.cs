using AniRuntime.Actions;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #96 (2026-07-15) — spec tests for
/// <see cref="RecallMemoryAction"/>. Pins:
///
/// <list type="number">
///   <item>Tier argument routes to <see cref="IMemorySearch.SearchByTierAsync"/>;
///     absent tier routes to <see cref="IMemorySearch.SearchWithScoresAsync"/>
///     — one tier-normalization site, no drift between action and search.</item>
///   <item>Descriptor matches the empirically-validated fixture wording so
///     production classifier sees identical framing to the fixture that
///     hit 100% on 2026-07-15.</item>
///   <item>Missing / whitespace query returns an attributable error string
///     rather than throwing (per Issue #96: "Tool errors surface as
///     attributable errors, not silent fallbacks").</item>
///   <item>Search-throw is caught and surfaced as an error string — the
///     turn-level loop must never see a raw exception from a tool.</item>
///   <item>Zero-hit results surface as a distinct message the character
///     model can reason about ("no results for X"), not an empty string.</item>
/// </list>
/// </summary>
public class RecallMemoryActionTests
{
    private static RecallMemoryAction Build(Mock<IMemorySearch> search)
        => new(search.Object, NullLogger<RecallMemoryAction>.Instance);

    private static ScoredMemory MakeHit(string content, EpistemicTier tier, DateTimeOffset? occurredAt = null)
    {
        var record = new MemoryRecord
        {
            Id         = Guid.NewGuid(),
            Content    = content,
            Provenance = tier,
            OccurredAt = occurredAt ?? new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero),
        };
        return new ScoredMemory(record, CompositeScore: 0.85f, CosineSimilarity: 0.80f);
    }

    // ── Descriptor shape ───────────────────────────────────────────────────

    [Fact]
    public void Descriptor_NameIsRecallMemory()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        var action = Build(search);
        action.Descriptor.Name.Should().Be("recall_memory");
    }

    [Fact]
    public void Descriptor_HasQueryAndTierParameters()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        var action = Build(search);
        action.Descriptor.ParameterSchema.Keys.Should().Contain(new[] { "query", "tier" });
    }

    [Fact]
    public void Descriptor_DescriptionCarriesFixtureWording()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        var action = Build(search);
        // The wording pin — "Absence of conversation context ... is a reason to CALL the tool" —
        // is what makes qwen3:14b stop treating "no context = don't call" as a rejection reason.
        // If this text drifts, the empirically-validated classifier behaviour drifts with it.
        action.Descriptor.Description.Should().Contain("Absence of conversation context");
        action.Descriptor.Description.Should().Contain("CALL the tool");
    }

    // ── Query routing: tier ─────────────────────────────────────────────────

    [Theory]
    [InlineData("facts",    EpistemicTier.Facts)]
    [InlineData("Facts",    EpistemicTier.Facts)]
    [InlineData("EPISODIC", EpistemicTier.Episodic)]
    [InlineData("interior", EpistemicTier.Interior)]
    public async Task InvokeAsync_TierArgument_RoutesToSearchByTierAsync(string tierArg, EpistemicTier expectedTier)
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchByTierAsync("Peru", expectedTier, 5, It.IsAny<CancellationToken>(), 0.0f))
            .ReturnsAsync(new[] { MakeHit("we hiked in Peru last summer", expectedTier) });

        var action = Build(search);
        var result = await action.InvokeAsync(
            new Dictionary<string, string> { ["query"] = "Peru", ["tier"] = tierArg },
            CancellationToken.None);

        result.Should().Contain("Peru");
        result.Should().Contain(expectedTier.ToString());
        search.Verify(s => s.SearchByTierAsync("Peru", expectedTier, 5, It.IsAny<CancellationToken>(), 0.0f), Times.Once);
        search.Verify(s => s.SearchWithScoresAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_NoTierArgument_RoutesToSearchWithScoresAsync()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchWithScoresAsync("Kevin", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                MakeHit("Kevin from the gym mentioned...", EpistemicTier.Episodic),
                MakeHit("Kevin is one of Mark's gym friends", EpistemicTier.Facts),
            });

        var action = Build(search);
        var result = await action.InvokeAsync(
            new Dictionary<string, string> { ["query"] = "Kevin" },
            CancellationToken.None);

        result.Should().Contain("Kevin");
        result.Should().Contain("2 result(s)");
        search.Verify(s => s.SearchWithScoresAsync("Kevin", 5, It.IsAny<CancellationToken>()), Times.Once);
        search.Verify(s => s.SearchByTierAsync(It.IsAny<string>(), It.IsAny<EpistemicTier>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<float>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_UnknownTierValue_FallsBackToUntieredSearch()
    {
        // Guard rail — an LLM might emit an unexpected tier value (e.g. "recent",
        // "all", a typo). Rather than error out, fall back to the untiered path
        // so the caller still gets results.
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchWithScoresAsync("book", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeHit("book we discussed", EpistemicTier.Episodic) });

        var action = Build(search);
        var result = await action.InvokeAsync(
            new Dictionary<string, string> { ["query"] = "book", ["tier"] = "recent" },
            CancellationToken.None);

        result.Should().Contain("book");
        search.Verify(s => s.SearchWithScoresAsync("book", 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Attributable errors ────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_MissingQuery_ReturnsAttributableError()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        var action = Build(search);

        var result = await action.InvokeAsync(
            new Dictionary<string, string>(),
            CancellationToken.None);

        result.Should().Contain("recall_memory error");
        result.Should().Contain("no query");
    }

    [Fact]
    public async Task InvokeAsync_WhitespaceQuery_ReturnsAttributableError()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        var action = Build(search);

        var result = await action.InvokeAsync(
            new Dictionary<string, string> { ["query"] = "   " },
            CancellationToken.None);

        result.Should().Contain("recall_memory error");
        result.Should().Contain("no query");
    }

    [Fact]
    public async Task InvokeAsync_SearchThrows_ReturnsAttributableError()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchWithScoresAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db unreachable"));

        var action = Build(search);
        var result = await action.InvokeAsync(
            new Dictionary<string, string> { ["query"] = "Peru" },
            CancellationToken.None);

        result.Should().Contain("recall_memory error");
        result.Should().Contain("InvalidOperationException");
    }

    // ── Zero-hit distinctness ──────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ZeroHits_ReturnsDistinctNoResultsMessage()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchWithScoresAsync("nothingburger", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var action = Build(search);
        var result = await action.InvokeAsync(
            new Dictionary<string, string> { ["query"] = "nothingburger" },
            CancellationToken.None);

        result.Should().Contain("no results");
        result.Should().Contain("nothingburger");
        // Distinct from the error path — a query that returned nothing is a
        // legitimate observation the character model can reason about.
        result.Should().NotContain("error");
    }

    [Fact]
    public async Task InvokeAsync_ZeroHits_WithTier_MentionsTierInMessage()
    {
        var search = new Mock<IMemorySearch>(MockBehavior.Strict);
        search.Setup(s => s.SearchByTierAsync("nothingburger", EpistemicTier.Facts, 5, It.IsAny<CancellationToken>(), 0.0f))
            .ReturnsAsync(Array.Empty<ScoredMemory>());

        var action = Build(search);
        var result = await action.InvokeAsync(
            new Dictionary<string, string> { ["query"] = "nothingburger", ["tier"] = "facts" },
            CancellationToken.None);

        result.Should().Contain("no results");
        result.Should().Contain("Facts");
    }

    // ── Result formatting ─────────────────────────────────────────────────

    [Fact]
    public void FormatHits_TruncatesLongContent()
    {
        var longContent = new string('x', 500);
        var hits = new List<ScoredMemory> { MakeHit(longContent, EpistemicTier.Interior) };

        var formatted = RecallMemoryAction.FormatHits("q", tier: null, hits);

        formatted.Should().Contain("…");
        formatted.Length.Should().BeLessThan(400); // truncation kicked in
    }

    [Fact]
    public void FormatHits_IncludesProvenanceAndDateForEachHit()
    {
        var hits = new List<ScoredMemory>
        {
            MakeHit("first hit",  EpistemicTier.Facts,    new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeHit("second hit", EpistemicTier.Episodic, new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)),
        };

        var formatted = RecallMemoryAction.FormatHits("q", tier: null, hits);

        formatted.Should().Contain("[Facts]");
        formatted.Should().Contain("[Episodic]");
        formatted.Should().Contain("2026-03-01");
        formatted.Should().Contain("2026-07-15");
        formatted.Should().Contain("first hit");
        formatted.Should().Contain("second hit");
    }
}
