using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme G Layer 3 Phase G3.4.B (May 3, 2026) — own-output retrieval ceiling
/// unit tests. Symmetric to <see cref="ProtectedSlotsBackfillTests"/> but
/// operating on the upper-bound side: when a retrieval pool has too much
/// own-output content, evict the lowest-scoring own-output slots and
/// backfill with the highest-scoring non-own-output candidates.
///
/// **Why this exists**: five empirically-confirmed instances of own-output
/// substrate dominance over the past week — Apr 27 vanilla cream soda
/// cascade, Apr 30 substrate-typing failure, May 2 evening remediation-
/// cascade-convergence (Mark `///tag repeating`), May 3 10:55 "perez"
/// outreach. The defensive output filtering caught individual generations
/// but the regenerations converged because both pulled from a substrate
/// where own-output had high gravity. The ceiling enforces substrate
/// diversification at the source.
/// </summary>
public class OwnOutputCeilingTests
{
    private static ScoredMemory Make(
        float composite,
        RetrievalOrigin origin,
        string id)
    {
        var guid = Guid.Parse(id.PadRight(32, '0').Insert(8, "-").Insert(13, "-").Insert(18, "-").Insert(23, "-"));
        var record = new MemoryRecord
        {
            Id        = guid,
            Type      = MemoryType.Episodic,
            Content   = $"mem-{id}",
            Embedding = null,
        };
        return new ScoredMemory(record, composite, composite)
        {
            OriginTier = origin,
        };
    }

    // ── Degenerate inputs ────────────────────────────────────────────────

    [Fact]
    public void ApplyOwnOutputCeiling_EmptyRanked_ReturnsEmpty()
    {
        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            rankedTopK: new List<ScoredMemory>(),
            allCandidates: new List<ScoredMemory>(),
            topK: 5,
            maxOwnOutputFraction: 0.25f);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplyOwnOutputCeiling_FullFraction_ReturnsUnchanged()
    {
        // maxFraction >= 1.0 means no ceiling effectively.
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, RetrievalOrigin.OwnOutput, "00000001"),
            Make(0.8f, RetrievalOrigin.OwnOutput, "00000002"),
        };
        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            ranked, new List<ScoredMemory>(ranked),
            topK: 2, maxOwnOutputFraction: 1.0f);
        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
    }

    // ── Already under ceiling ───────────────────────────────────────────

    [Fact]
    public void ApplyOwnOutputCeiling_AlreadyUnderCeiling_NoEviction()
    {
        // 1 own-output of 4 = 25% — equals the cap. floor(4 * 0.25) = 1
        // allowed. So 1 = 1, no excess, no eviction.
        var ranked = new List<ScoredMemory>
        {
            Make(0.9f, RetrievalOrigin.Caregiver, "00000001"),
            Make(0.8f, RetrievalOrigin.OwnOutput, "00000002"),
            Make(0.7f, RetrievalOrigin.Caregiver, "00000003"),
            Make(0.6f, RetrievalOrigin.External,  "00000004"),
        };
        var all = new List<ScoredMemory>(ranked);

        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            ranked, all, topK: 4, maxOwnOutputFraction: 0.25f);

        result.Should().BeEquivalentTo(ranked, opts => opts.WithStrictOrdering());
    }

    // ── The May 2 evening canonical case ────────────────────────────────

    [Fact]
    public void ApplyOwnOutputCeiling_TooMuchOwnOutput_EvictsLowestAndBackfills()
    {
        // 3 own-output of 5 = 60%. Cap = 0.25 → floor(5 * 0.25) = 1. Excess = 2.
        // Should drop the lowest-2 own-output slots and backfill with the
        // highest-scoring non-own-output candidates from the pool.
        var ranked = new List<ScoredMemory>
        {
            Make(0.95f, RetrievalOrigin.OwnOutput, "00000001"),  // strongest own-output → preserved
            Make(0.85f, RetrievalOrigin.Caregiver, "00000002"),
            Make(0.75f, RetrievalOrigin.OwnOutput, "00000003"),  // mid own-output → evicted
            Make(0.65f, RetrievalOrigin.Caregiver, "00000004"),
            Make(0.55f, RetrievalOrigin.OwnOutput, "00000005"),  // weakest own-output → evicted
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.50f, RetrievalOrigin.External,  "00000006"),  // strongest non-own-output backfill
            Make(0.45f, RetrievalOrigin.World,     "00000007"),  // second backfill
            Make(0.40f, RetrievalOrigin.OwnOutput, "00000008"),  // own-output — NOT a backfill candidate
        };
        var all = ranked.Concat(remainder).ToList();

        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            ranked, all, topK: 5, maxOwnOutputFraction: 0.25f);

        result.Count.Should().Be(5, "ceiling should preserve pool size by backfilling");
        result.Count(r => RetrievalOriginClassifier.IsOwnOutput(r.OriginTier))
            .Should().Be(1, "exactly one own-output slot allowed under 25% ceiling on topK=5");
        result.Should().Contain(r => r.Record.Id == ranked[0].Record.Id,
            "strongest own-output (composite=0.95) preserved");
        result.Should().NotContain(r => r.Record.Id == ranked[2].Record.Id,
            "mid-scoring own-output (0.75) evicted");
        result.Should().NotContain(r => r.Record.Id == ranked[4].Record.Id,
            "weakest own-output (0.55) evicted");
        result.Should().Contain(r => r.Record.Id == remainder[0].Record.Id,
            "strongest non-own-output backfill (External 0.50) included");
        result.Should().Contain(r => r.Record.Id == remainder[1].Record.Id,
            "second-strongest non-own-output backfill (World 0.45) included");
        result.Should().NotContain(r => r.Record.Id == remainder[2].Record.Id,
            "own-output backfill candidate must NOT be picked — defeats the purpose");
    }

    [Fact]
    public void ApplyOwnOutputCeiling_PoolExhausted_ReturnsSmallerPool()
    {
        // 3 own-output of 4, cap = 0.25 → 1 allowed, excess = 2. Only 1
        // non-own-output backfill candidate available. Result should have
        // 1 own-output (preserved strongest) + 1 caregiver + 1 backfill = 3.
        var ranked = new List<ScoredMemory>
        {
            Make(0.95f, RetrievalOrigin.OwnOutput, "00000001"),  // preserved
            Make(0.85f, RetrievalOrigin.Caregiver, "00000002"),
            Make(0.75f, RetrievalOrigin.OwnOutput, "00000003"),  // evicted
            Make(0.55f, RetrievalOrigin.OwnOutput, "00000004"),  // evicted
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.50f, RetrievalOrigin.External, "00000005"),  // only backfill available
        };
        var all = ranked.Concat(remainder).ToList();

        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            ranked, all, topK: 4, maxOwnOutputFraction: 0.25f);

        result.Count.Should().Be(3,
            "pool shrinks when backfill is exhausted — smaller-clean-pool > polluted-pool");
        result.Count(r => RetrievalOriginClassifier.IsOwnOutput(r.OriginTier)).Should().Be(1);
    }

    [Fact]
    public void ApplyOwnOutputCeiling_AllOwnOutput_EvictsToFloorOnly()
    {
        // 100% own-output, cap = 0.25 → 1 allowed of 4. No backfill candidates
        // available. Result should be 1 (the strongest own-output).
        var ranked = new List<ScoredMemory>
        {
            Make(0.95f, RetrievalOrigin.OwnOutput, "00000001"),
            Make(0.85f, RetrievalOrigin.OwnOutput, "00000002"),
            Make(0.75f, RetrievalOrigin.OwnOutput, "00000003"),
            Make(0.65f, RetrievalOrigin.OwnOutput, "00000004"),
        };
        var all = new List<ScoredMemory>(ranked);

        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            ranked, all, topK: 4, maxOwnOutputFraction: 0.25f);

        result.Count.Should().Be(1,
            "without backfill candidates, result shrinks to the ceiling");
        result[0].Record.Id.Should().Be(ranked[0].Record.Id,
            "strongest own-output preserved");
    }

    // ── Composite ordering preserved ────────────────────────────────────

    [Fact]
    public void ApplyOwnOutputCeiling_ResultOrderedByCompositeScore()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.90f, RetrievalOrigin.OwnOutput, "00000001"),  // preserved
            Make(0.80f, RetrievalOrigin.Caregiver, "00000002"),
            Make(0.70f, RetrievalOrigin.OwnOutput, "00000003"),  // evicted
            Make(0.60f, RetrievalOrigin.OwnOutput, "00000004"),  // evicted
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.85f, RetrievalOrigin.External, "00000005"),  // higher than evicted, lower than preserved
            Make(0.50f, RetrievalOrigin.World,    "00000006"),  // lowest backfill
        };
        var all = ranked.Concat(remainder).ToList();

        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            ranked, all, topK: 4, maxOwnOutputFraction: 0.25f);

        // Expected order: 0.90 (own-output preserved), 0.85 (External backfill),
        // 0.80 (Caregiver), 0.50 (World backfill).
        result.Select(r => r.CompositeScore).Should().BeInDescendingOrder();
    }

    // ── Zero ceiling ────────────────────────────────────────────────────

    [Fact]
    public void ApplyOwnOutputCeiling_ZeroFraction_AllOwnOutputEvicted()
    {
        var ranked = new List<ScoredMemory>
        {
            Make(0.90f, RetrievalOrigin.OwnOutput, "00000001"),
            Make(0.80f, RetrievalOrigin.Caregiver, "00000002"),
        };
        var remainder = new List<ScoredMemory>
        {
            Make(0.50f, RetrievalOrigin.External, "00000003"),
        };
        var all = ranked.Concat(remainder).ToList();

        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            ranked, all, topK: 2, maxOwnOutputFraction: 0.0f);

        result.Should().NotContain(r => RetrievalOriginClassifier.IsOwnOutput(r.OriginTier),
            "fraction=0 means zero own-output slots allowed");
    }

    // ── Negative fraction (defense) ─────────────────────────────────────

    [Fact]
    public void ApplyOwnOutputCeiling_NegativeFraction_ClampedToZero()
    {
        // Negative fractions are nonsense input but shouldn't crash.
        var ranked = new List<ScoredMemory>
        {
            Make(0.90f, RetrievalOrigin.OwnOutput, "00000001"),
        };
        var all = new List<ScoredMemory>(ranked);

        var result = EfSemanticSearchComposer.ApplyOwnOutputCeiling(
            ranked, all, topK: 1, maxOwnOutputFraction: -0.5f);

        // -0.5 clamped to 0; 0 own-output allowed. No backfill so result empty.
        result.Should().BeEmpty();
    }
}
