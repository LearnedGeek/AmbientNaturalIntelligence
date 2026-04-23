using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Agentic Lens Layer 1 Phase 1c: origin-tier protected-slots backfill unit tests.
///
/// The backfill is pure: given a ranked top-K, the full candidate pool, a top-K
/// size, and a non-caregiver fraction, produce a top-K that meets the minimum
/// non-caregiver share by swapping weak caregiver slots for strong non-caregiver
/// candidates from the remainder.
///
/// These tests pin the algorithmic behavior:
///
///   - No swap when quota is already met.
///   - No swap when no non-caregiver candidates exist in the remainder
///     (we do not drop caregiver memories without an alternative).
///   - Swap count equals the shortfall, not more.
///   - Weakest caregiver slot is the one swapped out.
///   - Strongest non-caregiver remainder is the one swapped in.
///   - All origin classes except Caregiver count as non-caregiver
///     (Anchored, World, OwnOutput, External, Unknown).
/// </summary>
public class ProtectedSlotsBackfillTests
{
    private static ScoredMemory Make(
        float composite,
        RetrievalOrigin origin,
        string id)
    {
        // Guid with the id embedded for readable assertions.
        var guid = Guid.Parse(id.PadRight(32, '0').Insert(8, "-").Insert(13, "-").Insert(18, "-").Insert(23, "-"));
        var record = new MemoryRecord
        {
            Id        = guid,
            Type      = MemoryType.Episodic,
            Content   = $"mem-{id}",
            Embedding = null,  // embeddings not used in backfill logic
        };
        return new ScoredMemory(record, composite, composite)
        {
            OriginTier = origin,
        };
    }

    // ── Degenerate inputs ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyProtectedSlotsBackfill_EmptyRanked_ReturnsEmpty()
    {
        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            rankedTopK: new List<ScoredMemory>(),
            allCandidates: new List<ScoredMemory>(),
            topK: 5,
            minNonCaregiverFraction: 0.30f);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyProtectedSlotsBackfill_ZeroFraction_ReturnsRankedUnchanged()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, RetrievalOrigin.Caregiver, "00000001"),
            Make(0.8f, RetrievalOrigin.Caregiver, "00000002"),
        };
        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            ranked, allCandidates: new List<ScoredMemory>(ranked),
            topK: 2, minNonCaregiverFraction: 0f);
        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
    }

    // ── Already meets quota ──────────────────────────────────────────────────

    [Fact]
    public void ApplyProtectedSlotsBackfill_QuotaAlreadyMet_NoSwap()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, RetrievalOrigin.Caregiver, "00000001"),
            Make(0.8f, RetrievalOrigin.World,     "00000002"),  // non-caregiver already present
            Make(0.7f, RetrievalOrigin.Caregiver, "00000003"),
            Make(0.6f, RetrievalOrigin.External,  "00000004"),  // non-caregiver already present
        };
        // 2/4 = 50% non-caregiver, quota is 0.30 → already met
        var remainder = new List<ScoredMemory>
        {
            Make(0.5f, RetrievalOrigin.Anchored, "00000005"),
        };
        var all = ranked.Concat(remainder).ToList();

        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            ranked, all, topK: 4, minNonCaregiverFraction: 0.30f);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
    }

    // ── No non-caregiver candidates in remainder ─────────────────────────────

    [Fact]
    public void ApplyProtectedSlotsBackfill_NoAlternativesInRemainder_NoSwap()
    {
        // top-K is all-caregiver but remainder is also all-caregiver — we do
        // not drop a caregiver slot when there is no alternative to fill it.
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, RetrievalOrigin.Caregiver, "00000001"),
            Make(0.8f, RetrievalOrigin.Caregiver, "00000002"),
            Make(0.7f, RetrievalOrigin.Caregiver, "00000003"),
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.6f, RetrievalOrigin.Caregiver, "00000004"),
            Make(0.5f, RetrievalOrigin.Caregiver, "00000005"),
        };
        var all = ranked.Concat(remainder).ToList();

        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            ranked, all, topK: 3, minNonCaregiverFraction: 0.30f);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
    }

    // ── Single-swap backfill ─────────────────────────────────────────────────

    [Fact]
    public void ApplyProtectedSlotsBackfill_Shortfall1_SwapsWeakestCaregiverForStrongestAlternative()
    {
        // All caregiver, strong non-caregiver in remainder at score 0.65.
        // Quota: ceil(3 * 0.30) = 1 non-caregiver required.
        // Result: weakest caregiver (score 0.70) replaced by non-caregiver (0.65).
        var ranked = new List<ScoredMemory>
        {
            Make(0.90f, RetrievalOrigin.Caregiver, "00000001"),
            Make(0.80f, RetrievalOrigin.Caregiver, "00000002"),
            Make(0.70f, RetrievalOrigin.Caregiver, "00000003"),  // weakest — should be swapped out
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.65f, RetrievalOrigin.World,    "00000004"),   // strongest non-caregiver — swapped in
            Make(0.60f, RetrievalOrigin.External, "00000005"),
            Make(0.55f, RetrievalOrigin.Caregiver, "00000006"),
        };
        var all = ranked.Concat(remainder).ToList();

        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            ranked, all, topK: 3, minNonCaregiverFraction: 0.30f);

        result.Should().HaveCount(3);
        result.Should().Contain(r => r.Record.Content == "mem-00000001");
        result.Should().Contain(r => r.Record.Content == "mem-00000002");
        result.Should().Contain(r => r.Record.Content == "mem-00000004");  // swapped in
        result.Should().NotContain(r => r.Record.Content == "mem-00000003");  // swapped out
    }

    // ── Multi-swap backfill ──────────────────────────────────────────────────

    [Fact]
    public void ApplyProtectedSlotsBackfill_Shortfall2_SwapsTwoWeakestForTwoStrongestAlternatives()
    {
        // Quota: ceil(5 * 0.40) = 2 non-caregiver required. Pool has 0 → shortfall 2.
        var ranked = new List<ScoredMemory>
        {
            Make(0.95f, RetrievalOrigin.Caregiver, "00000001"),
            Make(0.90f, RetrievalOrigin.Caregiver, "00000002"),
            Make(0.85f, RetrievalOrigin.Caregiver, "00000003"),
            Make(0.80f, RetrievalOrigin.Caregiver, "00000004"),  // 2nd weakest
            Make(0.75f, RetrievalOrigin.Caregiver, "00000005"),  // weakest
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.70f, RetrievalOrigin.World,     "00000006"),  // strongest non-caregiver
            Make(0.65f, RetrievalOrigin.Anchored,  "00000007"),  // 2nd strongest non-caregiver
            Make(0.60f, RetrievalOrigin.OwnOutput, "00000008"),  // would be 3rd but not needed
        };
        var all = ranked.Concat(remainder).ToList();

        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            ranked, all, topK: 5, minNonCaregiverFraction: 0.40f);

        result.Should().HaveCount(5);
        result.Should().Contain(r => r.Record.Content == "mem-00000006");
        result.Should().Contain(r => r.Record.Content == "mem-00000007");
        result.Should().NotContain(r => r.Record.Content == "mem-00000004");
        result.Should().NotContain(r => r.Record.Content == "mem-00000005");
    }

    // ── Result preserves composite-score order ───────────────────────────────

    [Fact]
    public void ApplyProtectedSlotsBackfill_ResultIsComposite_Ordered()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.90f, RetrievalOrigin.Caregiver, "00000001"),
            Make(0.80f, RetrievalOrigin.Caregiver, "00000002"),
            Make(0.70f, RetrievalOrigin.Caregiver, "00000003"),
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.85f, RetrievalOrigin.World, "00000004"),  // middle-of-pack score
        };
        var all = ranked.Concat(remainder).ToList();

        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            ranked, all, topK: 3, minNonCaregiverFraction: 0.30f);

        // World candidate at 0.85 should land in the middle of the result:
        //   0.90 caregiver, 0.85 world (swapped in), 0.80 caregiver
        // (0.70 caregiver was the weakest and got swapped out)
        result[0].CompositeScore.Should().Be(0.90f);
        result[1].CompositeScore.Should().Be(0.85f);
        result[2].CompositeScore.Should().Be(0.80f);
    }

    // ── All non-caregiver origin classes count ───────────────────────────────

    [Theory]
    [InlineData(RetrievalOrigin.Anchored)]
    [InlineData(RetrievalOrigin.World)]
    [InlineData(RetrievalOrigin.OwnOutput)]
    [InlineData(RetrievalOrigin.External)]
    [InlineData(RetrievalOrigin.Unknown)]
    public void ApplyProtectedSlotsBackfill_AllNonCaregiverOriginsCountTowardQuota(
        RetrievalOrigin nonCaregiverOrigin)
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.90f, RetrievalOrigin.Caregiver,    "00000001"),
            Make(0.80f, nonCaregiverOrigin,           "00000002"),  // meets quota
            Make(0.70f, RetrievalOrigin.Caregiver,    "00000003"),
        };
        var all = new List<ScoredMemory>(ranked);

        // Quota is 1; ranked already has 1 non-caregiver. Should not swap.
        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            ranked, all, topK: 3, minNonCaregiverFraction: 0.30f);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
    }

    // ── Partial backfill when alternatives are limited ───────────────────────

    [Fact]
    public void ApplyProtectedSlotsBackfill_FewerAlternativesThanShortfall_SwapsAvailable()
    {
        // Quota: ceil(5 * 0.40) = 2. Current non-caregiver: 0. Shortfall: 2.
        // But only 1 non-caregiver in remainder. Should swap that 1.
        var ranked = new List<ScoredMemory>
        {
            Make(0.95f, RetrievalOrigin.Caregiver, "00000001"),
            Make(0.90f, RetrievalOrigin.Caregiver, "00000002"),
            Make(0.85f, RetrievalOrigin.Caregiver, "00000003"),
            Make(0.80f, RetrievalOrigin.Caregiver, "00000004"),
            Make(0.75f, RetrievalOrigin.Caregiver, "00000005"),
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.70f, RetrievalOrigin.World,     "00000006"),  // only non-caregiver
            Make(0.65f, RetrievalOrigin.Caregiver, "00000007"),
        };
        var all = ranked.Concat(remainder).ToList();

        var result = SqliteMemoryService.ApplyProtectedSlotsBackfill(
            ranked, all, topK: 5, minNonCaregiverFraction: 0.40f);

        result.Should().HaveCount(5);
        result.Should().Contain(r => r.Record.Content == "mem-00000006");
        // Only weakest caregiver should have been dropped
        result.Should().NotContain(r => r.Record.Content == "mem-00000005");
    }
}
