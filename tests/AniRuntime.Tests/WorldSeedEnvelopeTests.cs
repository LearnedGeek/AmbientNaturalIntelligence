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

    [Fact]
    public void ContextSnapshot_CarriesBothWorldSeedStringAndEnvelope_ForBackwardsCompat()
    {
        // Both surfaces exist during the F-1 Phase 8a migration window:
        // pre-envelope consumers keep reading .WorldSeed; envelope-aware
        // consumers read .WorldSeedEnvelope. Both should populate together
        // at writer sites.
        var snapshot = new ContextSnapshot
        {
            WorldSeed         = "morning — the world picking up",
            WorldSeedEnvelope = new WorldSeedEnvelope
            {
                Text   = "morning — the world picking up",
                Source = WorldSeedSource.WorldSeed,
            },
        };

        snapshot.WorldSeed.Should().NotBeNull();
        snapshot.WorldSeedEnvelope.Should().NotBeNull();
        snapshot.WorldSeedEnvelope!.Content.Should().Be(snapshot.WorldSeed);
    }
}
