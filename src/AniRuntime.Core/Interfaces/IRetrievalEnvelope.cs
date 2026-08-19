using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 5 (2026-08-18) — producer-boundary envelope
/// for retrieved memory records. Preserves <see cref="Provenance"/>
/// (Facts / Episodic / Interior) and <see cref="IProvenancedContent{T}.SourceType"/>
/// (derived from the record's <see cref="MemoryRecord.Provenance"/> plus
/// <see cref="MemoryRecord.SourceName"/>) through the L4 retrieval →
/// composer boundary so downstream prompt-builders can render per-record
/// source tags rather than dumping content with no attribution.
///
/// <para>
/// The wrapped payload is the full <see cref="MemoryRecord"/> so consumers
/// that need <see cref="MemoryRecord.OccurredAt"/>, temporal context, or
/// the raw <see cref="MemoryRecord.Content"/> can read them directly from
/// the envelope's Content. Same pattern as prior phases:
/// <c>IActiveTriggerEnvelope</c> (Phase 2), <c>IThoughtEnvelope</c>
/// (Phase 3), <c>ISubstrateGistEnvelope</c> (Phase 4).
/// </para>
///
/// <para>
/// Empirical anchor for the "attribution needed" motivation: the closed
/// issues #52 (reply pipeline pre-compose retrieval), #35 (FC-011
/// substrate-supported callbacks blocked), and partial #56 (semantic
/// dedup) all trace back to composers not being able to tell WHERE a
/// retrieved memory came from. Prefixing rendered blocks with
/// <c>[FROM: source, N ago]</c> at the consumer surface addresses this
/// producer-boundary gap.
/// </para>
/// </summary>
public interface IRetrievalEnvelope : IProvenancedContent<MemoryRecord>
{
    /// <summary>
    /// The epistemic tier of the wrapped record — Facts (external truth),
    /// Episodic (verbatim conversation), or Interior (Ani's inner life).
    /// Exposed here so consumers can route rendering / gating decisions
    /// without unwrapping <see cref="IProvenancedContent{T}.Content"/>.
    /// </summary>
    EpistemicTier Provenance { get; }
}
