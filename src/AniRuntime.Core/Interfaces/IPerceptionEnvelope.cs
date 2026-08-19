using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 6 (2026-08-19) — producer-boundary envelope
/// for perception events. Preserves <see cref="PerceptionEvent.SourceName"/>
/// through the L7 (perception → inner-thought composer) boundary so
/// downstream prompt builders can split the pre-Phase-6 semicolon-joined
/// <c>(Background: X; Y; Z)</c> blob into per-source, per-category
/// framing (<c>(You received a text from Mark 2m ago: "...")</c> vs
/// <c>(Weather right now: ...)</c>).
///
/// <para>
/// Same envelope pattern as prior phases:
/// <c>IActiveTriggerEnvelope</c> (Phase 2), <c>IThoughtEnvelope</c>
/// (Phase 3), <c>ISubstrateGistEnvelope</c> (Phase 4),
/// <c>IRetrievalEnvelope</c> (Phase 5). The wrapped payload is the full
/// <see cref="PerceptionEvent"/> so consumers can read
/// <see cref="PerceptionEvent.OccurredAt"/>, <see cref="PerceptionEvent.Category"/>,
/// and metadata directly from Content.
/// </para>
///
/// <para>
/// Empirical anchor for the "attribution needed at perception boundary"
/// motivation: issue #85 (temporal-awareness regression for contactState
/// perceptions) — inner-thoughts confabulated Mark's actions from
/// perception summaries that got flattened into an undifferentiated
/// background blob. Prefixing each perception line with a
/// source-appropriate phrase closes the L7 attribution gap.
/// </para>
/// </summary>
public interface IPerceptionEnvelope : IProvenancedContent<PerceptionEvent>
{
    /// <summary>The perception category (Environment / Content / Communication / Internal / etc.).</summary>
    PerceptionCategory Category { get; }
}
