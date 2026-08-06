using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Feature 44 Phase I.3 (2026-08-06) — Wandering-Mind register-family
/// diversity slot unit tests for
/// <see cref="EfSemanticSearchComposer.ApplyWanderingRegisterDiversitySlot"/>.
///
/// <para>
/// Pinned invariants:
/// </para>
/// <list type="number">
///   <item>Degenerate inputs (empty ranked, zero topK) → no-op, no swap.</item>
///   <item>If the ranked top-K contains ANY record whose Register folds
///     into a family different from the attractor, the diversity condition
///     is met — no swap.</item>
///   <item>Records with Register == null do NOT satisfy the diversity
///     condition (unknown family, not counted as diverse).</item>
///   <item>Records with Register == null are NOT eligible as swap
///     candidates (can't confirm they'd help — backfill fills these).</item>
///   <item>When ALL ranked records are same-family as attractor AND a
///     different-family candidate exists outside ranked, one swap happens:
///     highest-composite different-family candidate replaces the weakest
///     ranked slot. Result is size-K, re-sorted by composite.</item>
/// </list>
/// </summary>
public class WanderingRegisterDiversitySlotTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static ScoredMemory Make(float composite, string? register, string id)
    {
        var guid = Guid.Parse(id.PadRight(32, '0').Insert(8, "-").Insert(13, "-").Insert(18, "-").Insert(23, "-"));
        var record = new MemoryRecord
        {
            Id         = guid,
            Type       = MemoryType.InnerThought,
            Content    = $"mem-{id}",
            OccurredAt = Now.AddDays(-1),
            Register   = register,
            Embedding  = null,
        };
        return new ScoredMemory(record, composite, composite);
    }

    // ── Degenerate inputs ────────────────────────────────────────────────

    [Fact]
    public void EmptyRanked_NoSwap()
    {
        var result = EfSemanticSearchComposer.ApplyWanderingRegisterDiversitySlot(
            new List<ScoredMemory>(), new List<ScoredMemory>(),
            topK: 5, attractorFamily: RegisterFamily.Warmth, out var swaps);
        result.Should().BeEmpty();
        swaps.Should().Be(0);
    }

    [Fact]
    public void ZeroTopK_NoSwap()
    {
        var ranked = new List<ScoredMemory> { Make(0.9f, "Warmth", "00000001") };
        var result = EfSemanticSearchComposer.ApplyWanderingRegisterDiversitySlot(
            ranked, ranked, topK: 0, attractorFamily: RegisterFamily.Warmth, out var swaps);
        result.Should().BeSameAs(ranked);
        swaps.Should().Be(0);
    }

    // ── Diversity already met ────────────────────────────────────────────

    [Fact]
    public void RankedContainsDifferentFamily_NoSwap()
    {
        // attractor = Warmth; ranked has Existential which folds to
        // RegisterFamily.Existential (≠ Warmth) — diversity met.
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, "Warmth",      "00000001"),
            Make(0.8f, "Tenderness",  "00000002"),
            Make(0.7f, "Existential", "00000003"),
        };

        var result = EfSemanticSearchComposer.ApplyWanderingRegisterDiversitySlot(
            ranked, ranked, topK: 3, attractorFamily: RegisterFamily.Warmth, out var swaps);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
        swaps.Should().Be(0);
    }

    // ── Null-Register records don't satisfy the condition ────────────────

    [Fact]
    public void NullRegisterInRanked_DoesNotCountAsDiverse()
    {
        // attractor = Warmth; ranked has all warm-family (warmth, desire,
        // wanting all fold to RegisterFamily.Warmth) + one null-Register
        // record. Null doesn't satisfy diversity — needs a real
        // different-family record. Pool has one Curiosity available for swap.
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, "warmth",  "00000001"),
            Make(0.7f, "desire",  "00000002"),
            Make(0.4f, null,      "00000003"),
        };
        var pool = new List<ScoredMemory>(ranked)
        {
            Make(0.6f, "Curiosity", "00000004"),
        };

        var result = EfSemanticSearchComposer.ApplyWanderingRegisterDiversitySlot(
            ranked, pool, topK: 3, attractorFamily: RegisterFamily.Warmth, out var swaps);

        swaps.Should().Be(1, "null-Register record does not satisfy the diversity condition");
        result.Should().Contain(r => r.Record.Register == "Curiosity");
        result.Should().NotContain(r => r.Record.Register == null);
    }

    // ── Null-Register candidates are not eligible for swap ───────────────

    [Fact]
    public void NullRegisterCandidate_NotEligibleForSwap()
    {
        // attractor = Warmth; ranked all same-family; pool has one
        // null-Register candidate and no different-family candidate.
        // Slot should no-op — null candidate isn't a valid swap target.
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, "Warmth",     "00000001"),
            Make(0.7f, "Tenderness", "00000002"),
        };
        var pool = new List<ScoredMemory>(ranked)
        {
            Make(0.6f, null, "00000003"),   // null = unknown
        };

        var result = EfSemanticSearchComposer.ApplyWanderingRegisterDiversitySlot(
            ranked, pool, topK: 2, attractorFamily: RegisterFamily.Warmth, out var swaps);

        swaps.Should().Be(0);
        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
    }

    // ── Real swap happens ────────────────────────────────────────────────

    [Fact]
    public void AllRankedSameFamily_SwapsWeakestForBestDifferentFamily()
    {
        // attractor = Warmth; ranked all Warmth/Tenderness (same family
        // after folding — Warmth is its own family, Tenderness folds to
        // Tenderness. Wait: check the mapping.)
        //
        // Per ImpactCategoryDefaults.ToRegisterFamily:
        //   "warmth" → Warmth
        //   "tenderness" → Tenderness
        // So actually Warmth and Tenderness are DIFFERENT families. Rework
        // this test to use two Warmth-mapped registers:
        //   "warmth" → Warmth
        //   "desire" → Warmth
        //   "wanting" → Warmth
        //
        // Reference: EmotionalContribution.cs:200.
        var warmA = Make(0.9f, "warmth", "00000001");
        var warmB = Make(0.7f, "desire", "00000002");
        var warmWeak = Make(0.4f, "wanting", "00000003");
        var existentialCandidate = Make(0.5f, "Existential", "00000004");
        var lowerExistential = Make(0.2f, "Curiosity", "00000005");

        var ranked = new List<ScoredMemory> { warmA, warmB, warmWeak };
        var pool = new List<ScoredMemory> { warmA, warmB, warmWeak, existentialCandidate, lowerExistential };

        var result = EfSemanticSearchComposer.ApplyWanderingRegisterDiversitySlot(
            ranked, pool, topK: 3, attractorFamily: RegisterFamily.Warmth, out var swaps);

        swaps.Should().Be(1);
        result.Should().HaveCount(3);
        result.Should().Contain(existentialCandidate,
            "highest-composite different-family candidate wins the reserved slot");
        result.Should().NotContain(warmWeak,
            "the lowest-composite same-family slot should have been swapped out");
        result.Should().NotContain(lowerExistential,
            "only one wandering slot fires per call — the top different-family candidate wins");
        result.Select(r => r.CompositeScore).Should().BeInDescendingOrder();
    }
}
