using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 8a (2026-08-19) — verifies
/// <see cref="WorldSeedEnvelope"/> implements <see cref="IWorldSeedEnvelope"/>
/// correctly and that the two <c>WorldSeedSource</c> variants produce
/// distinct <see cref="IProvenancedContent{T}.SourceType"/> /
/// <see cref="IProvenancedContent{T}.Producer"/> tags so downstream
/// consumers can tell the circadian world-seed from the carried-over
/// associative anchor.
/// </summary>
public class WorldSeedEnvelopeTests
{
    [Fact]
    public void WorldSeedEnvelope_ImplementsIWorldSeedEnvelope_ExposesContent()
    {
        var env = new WorldSeedEnvelope
        {
            Text   = "mid-morning — quiet hours, things settling",
            Source = WorldSeedSource.WorldSeed,
        };
        IWorldSeedEnvelope contract = env;

        contract.Content.Should().Be(env.Text);
        contract.SourceType.Should().Be("world-seed.circadian");
        contract.Producer.Should().Be("WorldSeedService");
    }

    [Fact]
    public void WorldSeedEnvelope_AssociativeAnchorSource_HasDistinctSourceType()
    {
        var env = new WorldSeedEnvelope
        {
            Text   = "The last thing lingering in your mind: dust motes on the counter",
            Source = WorldSeedSource.AssociativeAnchor,
        };
        IWorldSeedEnvelope contract = env;

        contract.SourceType.Should().Be("world-seed.associative-anchor");
        contract.Producer.Should().Be("CognitiveCyclePipeline");
    }

    [Theory]
    [InlineData(WorldSeedSource.WorldSeed,          "world-seed.circadian",         "WorldSeedService")]
    [InlineData(WorldSeedSource.AssociativeAnchor,  "world-seed.associative-anchor", "CognitiveCyclePipeline")]
    public void WorldSeedEnvelope_SourceType_And_Producer_MapFromSourceEnum(
        WorldSeedSource source, string expectedSourceType, string expectedProducer)
    {
        IWorldSeedEnvelope env = new WorldSeedEnvelope
        {
            Text   = "x",
            Source = source,
        };
        env.SourceType.Should().Be(expectedSourceType);
        env.Producer.Should().Be(expectedProducer);
    }

    [Fact]
    public async Task WorldSeedEnvelope_CreatedAt_IsStableAcrossReads()
    {
        // Sibling-impl discipline (from PR #112 CreatedAt-non-idempotent
        // finding): captured once at construction, must return the same
        // value across reads.
        IProvenancedContent<string> env = new WorldSeedEnvelope
        {
            Text   = "x",
            Source = WorldSeedSource.WorldSeed,
        };

        var first = env.CreatedAt;
        await Task.Delay(20);
        var second = env.CreatedAt;

        second.Should().Be(first, "CreatedAt must be captured once at construction, not recomputed on each read");
    }

    [Fact]
    public void WorldSeedEnvelope_SemanticKey_IsNull()
    {
        IProvenancedContent<string> env = new WorldSeedEnvelope
        {
            Text   = "x",
            Source = WorldSeedSource.WorldSeed,
        };
        env.SemanticKey.Should().BeNull(
            "world-seed content isn't dedup-scored today; SemanticKey stays null unless a future consumer needs it");
    }

    // PR #118 review-fix (Devin): ContextSnapshot.WorldSeed is a computed
    // getter that mirrors WorldSeedEnvelope.Content. Single source of truth
    // — the two surfaces can never drift because there IS only one surface;
    // the string is a passthrough view.
    [Fact]
    public void ContextSnapshot_WorldSeedMirrorsEnvelope_SingleSourceOfTruth()
    {
        var snapshot = new ContextSnapshot
        {
            WorldSeedEnvelope = new WorldSeedEnvelope
            {
                Text   = "morning — the world picking up",
                Source = WorldSeedSource.WorldSeed,
            },
        };

        snapshot.WorldSeedEnvelope.Should().NotBeNull();
        snapshot.WorldSeed.Should().Be("morning — the world picking up",
            "WorldSeed is a computed getter — always mirrors WorldSeedEnvelope.Content");
    }

    [Fact]
    public void ContextSnapshot_NullEnvelope_YieldsNullWorldSeed()
    {
        var snapshot = new ContextSnapshot
        {
            WorldSeedEnvelope = null,
        };

        snapshot.WorldSeed.Should().BeNull(
            "no envelope → no world seed on either surface (they cannot disagree)");
    }

    [Fact]
    public void ContextSnapshot_ChangingEnvelope_ChangesComputedWorldSeed()
    {
        // Proves the surfaces cannot drift: switching the envelope changes
        // the string; there is no independent .WorldSeed setter that could
        // hold a stale value.
        var snapshot = new ContextSnapshot
        {
            WorldSeedEnvelope = new WorldSeedEnvelope
            {
                Text   = "first",
                Source = WorldSeedSource.WorldSeed,
            },
        };
        snapshot.WorldSeed.Should().Be("first");

        snapshot.WorldSeedEnvelope = new WorldSeedEnvelope
        {
            Text   = "second",
            Source = WorldSeedSource.AssociativeAnchor,
        };
        snapshot.WorldSeed.Should().Be("second");
    }
}
