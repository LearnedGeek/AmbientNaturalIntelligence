namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 8a (2026-08-19) — producer-boundary
/// envelope for the "world seed" / "lingering" content injected into the
/// inner-thought prompt via <c>ContextSnapshot.WorldSeed</c>. Two
/// semantically-distinct producers write this field:
/// <list type="bullet">
///   <item><c>WorldSeedService.GenerateSeed</c> — time-of-day + weather +
///         optional event flavor. Circadian light-spark grounding.</item>
///   <item><c>CognitiveCyclePipeline._lastAssociativeAnchor</c> — carries
///         forward the last cycle's inner-thought anchor as a "lingering"
///         thread the next cycle can drift toward.</item>
/// </list>
/// Wrapping both in the same envelope surface, distinguished by
/// <see cref="IProvenancedContent{T}.SourceType"/>, means downstream
/// composers can tell "world environment" from "carried-over interior
/// thread" — pre-Phase-8a they were both raw strings dumped into the
/// same section.
///
/// <para>
/// Same envelope pattern as prior phases: <c>IActiveTriggerEnvelope</c>
/// (Phase 2), <c>IThoughtEnvelope</c> (Phase 3), <c>ISubstrateGistEnvelope</c>
/// (Phase 4), <c>IRetrievalEnvelope</c> (Phase 5), <c>IPerceptionEnvelope</c>
/// (Phase 6), <c>IAnchoredMemoryEnvelope</c> (Phase 7).
/// </para>
/// </summary>
public interface IWorldSeedEnvelope : IProvenancedContent<string>
{
}
