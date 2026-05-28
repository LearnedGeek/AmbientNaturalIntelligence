using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Pins the foundation-memory bypass behavior (May 28, 2026) added to
/// <see cref="EfSemanticSearchComposer.ApplyCosineFloorWithAnchoredBypass"/>.
///
/// <para>
/// Anchored records (foundation: character seeds, world canon, identity
/// scaffolding) must pass the cosine-similarity noise floor regardless of
/// query-relevance, because the floor's purpose ("filter topically unrelated
/// noise") does not apply to scaffolding records that are never noise.
/// Empirical anchor: 2026-05-28 01:56 outreach where 1002 Facts-tier
/// candidates all scored below the 0.75 verifier floor — including anchored
/// records about Mia — and the verifier received zero canonical substrate.
/// </para>
/// </summary>
public class FoundationMemoryBypassTests
{
    private static ScoredMemory Make(float cosine, DecayTier decayTier, string id)
    {
        var record = new MemoryRecord
        {
            Id        = Guid.NewGuid(),
            Type      = MemoryType.Semantic,
            Content   = $"mem-{id}",
            DecayTier = decayTier,
        };
        // CompositeScore is irrelevant to the floor filter — it's used by
        // the caller's downstream Order/Take. Use cosine as a proxy here.
        return new ScoredMemory(record, cosine, cosine);
    }

    [Fact]
    public void Anchored_record_below_floor_is_kept()
    {
        var scored = new List<ScoredMemory>
        {
            Make(0.30f, DecayTier.Anchored,    "mia"),  // below floor, anchored
            Make(0.50f, DecayTier.Standard, "chat"), // below floor, not anchored
        };

        var (passing, dropped, bypassed) =
            EfSemanticSearchComposer.ApplyCosineFloorWithAnchoredBypass(scored, minCosine: 0.75f);

        passing.Should().HaveCount(1,
            "anchored record bypasses the floor; non-anchored below-floor record is dropped");
        passing[0].Record.DecayTier.Should().Be(DecayTier.Anchored);
        dropped.Should().Be(1, "the non-anchored below-floor record is dropped");
        bypassed.Should().Be(1, "exactly one anchored record bypassed the floor");
    }

    [Fact]
    public void Non_anchored_record_above_floor_is_kept()
    {
        var scored = new List<ScoredMemory>
        {
            Make(0.85f, DecayTier.Standard, "topic-match"),
        };

        var (passing, dropped, bypassed) =
            EfSemanticSearchComposer.ApplyCosineFloorWithAnchoredBypass(scored, minCosine: 0.75f);

        passing.Should().HaveCount(1);
        dropped.Should().Be(0);
        bypassed.Should().Be(0,
            "this record passed on its own cosine, not via bypass");
    }

    [Fact]
    public void Anchored_record_above_floor_is_kept_but_not_counted_as_bypassed()
    {
        // Edge case: an anchored record that is also topically relevant. It
        // passes the floor on its cosine merit; the bypass counter should
        // not double-count it.
        var scored = new List<ScoredMemory>
        {
            Make(0.90f, DecayTier.Anchored, "topic-and-foundation"),
        };

        var (passing, dropped, bypassed) =
            EfSemanticSearchComposer.ApplyCosineFloorWithAnchoredBypass(scored, minCosine: 0.75f);

        passing.Should().HaveCount(1);
        dropped.Should().Be(0);
        bypassed.Should().Be(0,
            "record passed on its own cosine — bypass counter only tracks records that needed the bypass");
    }

    [Fact]
    public void Mixed_pool_only_drops_non_anchored_below_floor()
    {
        // The 01:56 shape: many non-anchored candidates that all fail the
        // floor, plus a few anchored records that should bypass.
        var scored = new List<ScoredMemory>
        {
            Make(0.10f, DecayTier.Standard, "chat-1"),
            Make(0.20f, DecayTier.Standard, "chat-2"),
            Make(0.30f, DecayTier.Standard, "chat-3"),
            Make(0.40f, DecayTier.Anchored,     "mia"),       // bypass
            Make(0.50f, DecayTier.Anchored,     "bookstore"), // bypass
            Make(0.80f, DecayTier.Standard, "match"),     // passes naturally
        };

        var (passing, dropped, bypassed) =
            EfSemanticSearchComposer.ApplyCosineFloorWithAnchoredBypass(scored, minCosine: 0.75f);

        passing.Should().HaveCount(3, "2 anchored bypassed + 1 cosine-passer");
        dropped.Should().Be(3, "3 non-anchored records below floor");
        bypassed.Should().Be(2, "2 anchored records needed the bypass");

        passing.Should().Contain(s => s.Record.Content == "mem-mia");
        passing.Should().Contain(s => s.Record.Content == "mem-bookstore");
        passing.Should().Contain(s => s.Record.Content == "mem-match");
    }

    [Fact]
    public void Zero_or_negative_floor_disables_filter_entirely()
    {
        // Floor of 0 means "no floor" — all records pass, no bypass counted.
        var scored = new List<ScoredMemory>
        {
            Make(0.0f,  DecayTier.Standard, "zero"),
            Make(0.5f,  DecayTier.Anchored,     "anchored"),
        };

        var (passing, dropped, bypassed) =
            EfSemanticSearchComposer.ApplyCosineFloorWithAnchoredBypass(scored, minCosine: 0f);

        passing.Should().HaveCount(2);
        dropped.Should().Be(0);
        bypassed.Should().Be(0,
            "no floor means no bypass — every record passes for the same reason");
    }

    [Fact]
    public void Empty_input_returns_empty_result()
    {
        var (passing, dropped, bypassed) =
            EfSemanticSearchComposer.ApplyCosineFloorWithAnchoredBypass(
                new List<ScoredMemory>(), minCosine: 0.75f);

        passing.Should().BeEmpty();
        dropped.Should().Be(0);
        bypassed.Should().Be(0);
    }

    [Fact]
    public void All_anchored_pool_below_floor_all_bypass()
    {
        // Pathological case: every record is anchored and below floor. All
        // bypass — the verifier sees the full foundation pool, which is the
        // intended behavior (foundation memories are never noise).
        var scored = new List<ScoredMemory>
        {
            Make(0.10f, DecayTier.Anchored, "a"),
            Make(0.20f, DecayTier.Anchored, "b"),
            Make(0.30f, DecayTier.Anchored, "c"),
        };

        var (passing, dropped, bypassed) =
            EfSemanticSearchComposer.ApplyCosineFloorWithAnchoredBypass(scored, minCosine: 0.75f);

        passing.Should().HaveCount(3);
        dropped.Should().Be(0);
        bypassed.Should().Be(3);
    }
}
