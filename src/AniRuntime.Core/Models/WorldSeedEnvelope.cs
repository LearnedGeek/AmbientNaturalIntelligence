using AniRuntime.Core.Interfaces;

namespace AniRuntime.Core.Models;

/// <summary>
/// Canonical <see cref="IWorldSeedEnvelope"/> implementation used by the
/// two producer sites that populate <c>ContextSnapshot.WorldSeed</c>.
/// Two source variants are supported via <see cref="Source"/> — see the
/// <c>WorldSeedSource</c> enum values.
///
/// <para>
/// Class (not record) so implementing <see cref="IWorldSeedEnvelope"/>
/// with an explicit-interface <see cref="IProvenancedContent{T}.CreatedAt"/>
/// auto-property doesn't change record equality (Phase 4 sibling-impl
/// discipline — records with a stored CreatedAt break value equality
/// across per-instance timestamps; classes are exempt).
/// </para>
/// </summary>
public sealed class WorldSeedEnvelope : IWorldSeedEnvelope
{
    public required string Text { get; init; }

    /// <summary>
    /// Which producer emitted this envelope — distinguishes the circadian
    /// light-spark (<see cref="WorldSeedSource.WorldSeed"/>) from the
    /// carried-over interior anchor (<see cref="WorldSeedSource.AssociativeAnchor"/>).
    /// </summary>
    public required WorldSeedSource Source { get; init; }

    // ── IWorldSeedEnvelope / IProvenancedContent<string> ────────────────

    /// <inheritdoc />
    string IProvenancedContent<string>.Content => Text;

    /// <inheritdoc />
    string IProvenancedContent<string>.SourceType => Source switch
    {
        WorldSeedSource.WorldSeed          => "world-seed.circadian",
        WorldSeedSource.AssociativeAnchor  => "world-seed.associative-anchor",
        _                                   => "world-seed.unknown",
    };

    /// <inheritdoc />
    string IProvenancedContent<string>.Producer => Source switch
    {
        WorldSeedSource.WorldSeed         => "WorldSeedService",
        WorldSeedSource.AssociativeAnchor => "CognitiveCyclePipeline",
        _                                  => "unknown",
    };

    /// <inheritdoc />
    /// <remarks>
    /// Captured once at construction per <c>IProvenancedContent&lt;T&gt;.CreatedAt</c>
    /// contract (sibling-impl discipline from PR #112). WorldSeedEnvelope
    /// is a class so this doesn't affect equality behavior.
    /// </remarks>
    DateTimeOffset IProvenancedContent<string>.CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    float[]? IProvenancedContent<string>.SemanticKey => null;
}

/// <summary>
/// The two producers that populate <c>ContextSnapshot.WorldSeed</c>.
/// </summary>
public enum WorldSeedSource
{
    /// <summary>Time-of-day + weather + event flavor via WorldSeedService.</summary>
    WorldSeed = 0,

    /// <summary>Carried-over "lingering" anchor from the previous cycle's inner thought.</summary>
    AssociativeAnchor = 1,
}
