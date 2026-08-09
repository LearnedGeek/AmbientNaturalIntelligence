using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Feature 44 Phase I.3 (2026-08-09) — character-seed entity injection slot
/// unit tests for
/// <see cref="EfSemanticSearchComposer.ApplyWanderingCharacterSeedSlot"/>.
///
/// <para>
/// Pinned invariants:
/// </para>
/// <list type="number">
///   <item>Degenerate inputs (empty ranked, zero topK, blank entity) →
///     no-op, no swap.</item>
///   <item>If ranked already contains a record mentioning the entity,
///     no-op.</item>
///   <item>If no candidate outside ranked mentions the entity, no-op
///     (better to leave ranked intact than swap in nothing).</item>
///   <item>When a valid entity-matching candidate exists outside ranked,
///     one swap occurs: highest-composite entity-matching candidate
///     replaces the weakest ranked slot.</item>
///   <item>Substring match is case-insensitive.</item>
/// </list>
/// </summary>
public class WanderingCharacterSeedSlotTests
{
    private static ScoredMemory Make(float composite, string content, string id)
    {
        var guid = Guid.Parse(id.PadRight(32, '0').Insert(8, "-").Insert(13, "-").Insert(18, "-").Insert(23, "-"));
        var record = new MemoryRecord
        {
            Id         = guid,
            Type       = MemoryType.InnerThought,
            Content    = content,
            OccurredAt = DateTimeOffset.UtcNow,
            Embedding  = null,
        };
        return new ScoredMemory(record, composite, composite);
    }

    // ── Degenerate inputs ────────────────────────────────────────────────

    [Fact]
    public void EmptyRanked_NoSwap()
    {
        var result = EfSemanticSearchComposer.ApplyWanderingCharacterSeedSlot(
            new List<ScoredMemory>(), new List<ScoredMemory>(),
            topK: 5, entity: "Kevin", out var swaps);
        result.Should().BeEmpty();
        swaps.Should().Be(0);
    }

    [Fact]
    public void BlankEntity_NoSwap()
    {
        var ranked = new List<ScoredMemory> { Make(0.9f, "some content", "00000001") };
        var result = EfSemanticSearchComposer.ApplyWanderingCharacterSeedSlot(
            ranked, ranked, topK: 5, entity: "", out var swaps);
        result.Should().BeSameAs(ranked);
        swaps.Should().Be(0);
    }

    // ── Diversity already met ────────────────────────────────────────────

    [Fact]
    public void RankedContainsEntity_NoSwap()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, "thinking about mark",           "00000001"),
            Make(0.7f, "spent the day with kevin",      "00000002"),
            Make(0.5f, "wandering the bookstore",       "00000003"),
        };
        var result = EfSemanticSearchComposer.ApplyWanderingCharacterSeedSlot(
            ranked, ranked, topK: 3, entity: "Kevin", out var swaps);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
        swaps.Should().Be(0);
    }

    // ── No matching candidate ────────────────────────────────────────────

    [Fact]
    public void NoMatchingCandidate_NoSwap()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, "warm reflections", "00000001"),
            Make(0.7f, "thinking about tea", "00000002"),
        };
        var pool = new List<ScoredMemory>(ranked)
        {
            Make(0.3f, "unrelated content about weather", "00000003"),
        };
        var result = EfSemanticSearchComposer.ApplyWanderingCharacterSeedSlot(
            ranked, pool, topK: 2, entity: "Peru", out var swaps);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
        swaps.Should().Be(0);
    }

    // ── Swap performed ──────────────────────────────────────────────────

    [Fact]
    public void EntityMatchExists_SwapsWeakestForBestMatch()
    {
        var recentTop  = Make(0.9f, "warm today",                          "00000001");
        var recentMid  = Make(0.7f, "thinking of you",                     "00000002");
        var recentLow  = Make(0.4f, "the room felt heavy",                 "00000003");
        var seedBest   = Make(0.5f, "that camping trip with Kevin",        "00000004");
        var seedWeaker = Make(0.2f, "Kevin brought over the wine",         "00000005");

        var ranked = new List<ScoredMemory> { recentTop, recentMid, recentLow };
        var pool   = new List<ScoredMemory> { recentTop, recentMid, recentLow, seedBest, seedWeaker };

        var result = EfSemanticSearchComposer.ApplyWanderingCharacterSeedSlot(
            ranked, pool, topK: 3, entity: "Kevin", out var swaps);

        swaps.Should().Be(1);
        result.Should().HaveCount(3);
        result.Should().Contain(seedBest,
            "highest-composite entity-matching candidate should be promoted");
        result.Should().NotContain(recentLow,
            "weakest ranked slot should have been swapped out");
        result.Should().NotContain(seedWeaker,
            "only the top match is promoted per call");
        result.Select(r => r.CompositeScore).Should().BeInDescendingOrder();
    }

    // ── Case insensitivity ──────────────────────────────────────────────

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, "warm today", "00000001"),
            Make(0.4f, "quiet",       "00000002"),
        };
        var pool = new List<ScoredMemory>(ranked)
        {
            Make(0.5f, "walked with KEVIN yesterday", "00000003"),
        };
        var result = EfSemanticSearchComposer.ApplyWanderingCharacterSeedSlot(
            ranked, pool, topK: 2, entity: "kevin", out var swaps);

        swaps.Should().Be(1);
        result.Should().Contain(r => r.Record.Content!.Contains("KEVIN"));
    }
}
