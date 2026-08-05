using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Feature 44 Phase I.3 (2026-08-05) — Wandering-Mind time-band slot
/// unit tests for
/// <see cref="EfSemanticSearchComposer.ApplyWanderingTimeBandSlot"/>.
///
/// <para>
/// Pinned invariants:
/// </para>
/// <list type="number">
///   <item>If the ranked top-K already contains a record ≥ minAgeDays old,
///     the mechanism is a no-op (swaps=0). Diversity condition already met.</item>
///   <item>If no candidate outside the ranked set is old enough, the
///     mechanism is a no-op (swaps=0). Nothing to promote.</item>
///   <item>When both conditions are absent AND an old-enough candidate
///     exists outside the ranked set, exactly one swap occurs: the highest-
///     composite old-enough candidate replaces the lowest-composite ranked
///     slot. Result stays size-K, re-sorted by composite score.</item>
///   <item>The mechanism is deterministic given a fixed <c>now</c> value —
///     tests pass their own reference time so wall-clock drift doesn't
///     flake them.</item>
/// </list>
/// </summary>
public class WanderingTimeBandSlotTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static ScoredMemory Make(float composite, DateTimeOffset occurredAt, string id)
    {
        var guid = Guid.Parse(id.PadRight(32, '0').Insert(8, "-").Insert(13, "-").Insert(18, "-").Insert(23, "-"));
        var record = new MemoryRecord
        {
            Id         = guid,
            Type       = MemoryType.Episodic,
            Content    = $"mem-{id}",
            OccurredAt = occurredAt,
            Embedding  = null,
        };
        return new ScoredMemory(record, composite, composite);
    }

    // ── Degenerate inputs ────────────────────────────────────────────────

    [Fact]
    public void EmptyRanked_ReturnsEmptyNoSwap()
    {
        var result = EfSemanticSearchComposer.ApplyWanderingTimeBandSlot(
            rankedTopK: new List<ScoredMemory>(),
            allCandidates: new List<ScoredMemory>(),
            topK: 5,
            minAgeDays: 7,
            now: Now,
            out var swaps);

        result.Should().BeEmpty();
        swaps.Should().Be(0);
    }

    [Fact]
    public void ZeroTopK_ReturnsUnchangedNoSwap()
    {
        var ranked = new List<ScoredMemory> { Make(0.9f, Now.AddDays(-1), "00000001") };
        var result = EfSemanticSearchComposer.ApplyWanderingTimeBandSlot(
            ranked, ranked, topK: 0, minAgeDays: 7, now: Now, out var swaps);

        result.Should().BeSameAs(ranked);
        swaps.Should().Be(0);
    }

    [Fact]
    public void NegativeMinAgeDays_ReturnsUnchangedNoSwap()
    {
        var ranked = new List<ScoredMemory> { Make(0.9f, Now.AddDays(-1), "00000001") };
        var result = EfSemanticSearchComposer.ApplyWanderingTimeBandSlot(
            ranked, ranked, topK: 5, minAgeDays: -1, now: Now, out var swaps);

        result.Should().BeSameAs(ranked);
        swaps.Should().Be(0);
    }

    // ── Diversity already met — no swap ─────────────────────────────────

    [Fact]
    public void RankedAlreadyContainsOldEnough_NoSwap()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, Now.AddDays(-1),  "00000001"),
            Make(0.8f, Now.AddDays(-30), "00000002"),   // ≥ 7 days old
            Make(0.7f, Now.AddDays(-2),  "00000003"),
        };

        var result = EfSemanticSearchComposer.ApplyWanderingTimeBandSlot(
            ranked, ranked, topK: 3, minAgeDays: 7, now: Now, out var swaps);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
        swaps.Should().Be(0);
    }

    // ── No old-enough candidate available — no swap ─────────────────────

    [Fact]
    public void NoOldEnoughCandidateAvailable_NoSwap()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, Now.AddDays(-1), "00000001"),
            Make(0.8f, Now.AddDays(-2), "00000002"),
        };
        var pool = new List<ScoredMemory>(ranked)
        {
            Make(0.3f, Now.AddDays(-3), "00000003"),   // also recent
        };

        var result = EfSemanticSearchComposer.ApplyWanderingTimeBandSlot(
            ranked, pool, topK: 2, minAgeDays: 7, now: Now, out var swaps);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
        swaps.Should().Be(0);
    }

    // ── Swap performed ──────────────────────────────────────────────────

    [Fact]
    public void OldEnoughCandidateExists_SwapsWeakestForBestOldRecord()
    {
        var recent1 = Make(0.9f, Now.AddDays(-1), "00000001");
        var recent2 = Make(0.8f, Now.AddDays(-2), "00000002");
        var recentWeak = Make(0.4f, Now.AddDays(-3), "00000003");
        var oldStrong = Make(0.5f, Now.AddDays(-30), "00000004");
        var oldWeaker = Make(0.2f, Now.AddDays(-60), "00000005");

        var ranked = new List<ScoredMemory> { recent1, recent2, recentWeak };
        var pool = new List<ScoredMemory> { recent1, recent2, recentWeak, oldStrong, oldWeaker };

        var result = EfSemanticSearchComposer.ApplyWanderingTimeBandSlot(
            ranked, pool, topK: 3, minAgeDays: 7, now: Now, out var swaps);

        swaps.Should().Be(1);
        result.Should().HaveCount(3);
        result.Should().Contain(oldStrong, "the highest-composite old-enough candidate should be promoted");
        result.Should().NotContain(recentWeak, "the lowest-composite ranked slot should have been swapped out");
        result.Should().NotContain(oldWeaker, "only one wandering slot fires per call");
        // Post-swap result must be sorted by composite descending.
        var scores = result.Select(r => r.CompositeScore).ToList();
        scores.Should().BeInDescendingOrder();
    }

    [Fact]
    public void CandidateAlreadyInRanked_NotDoubleAdded()
    {
        // Edge case: same record appears in both lists (typical shape —
        // allCandidates is a superset of rankedTopK). We must never
        // "promote" a record already in the ranked set.
        var recent = Make(0.9f, Now.AddDays(-1), "00000001");
        var old = Make(0.5f, Now.AddDays(-30), "00000002");

        var ranked = new List<ScoredMemory> { recent, old };
        var pool = new List<ScoredMemory> { recent, old };

        var result = EfSemanticSearchComposer.ApplyWanderingTimeBandSlot(
            ranked, pool, topK: 2, minAgeDays: 7, now: Now, out var swaps);

        swaps.Should().Be(0,
            "the old record is already in the ranked set — diversity condition met");
        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
    }
}
