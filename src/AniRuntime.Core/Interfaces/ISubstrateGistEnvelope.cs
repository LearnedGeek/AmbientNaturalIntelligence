namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Treatment directives — instructions to downstream consumers about how a
/// wrapped piece of substrate should be USED, distinct from what it says.
/// Introduced 2026-08-18 for F-1 Phase 4 (<see cref="ISubstrateGistEnvelope"/>);
/// scoped to the gist envelope for now. Hoist to
/// <see cref="IProvenancedContent{T}"/> if a second envelope class needs the
/// same enum (Foundation Input discipline: don't add breadth until a
/// concrete consumer requires it).
/// </summary>
public enum SubstrateGistTreatment
{
    /// <summary>
    /// The substrate content is REFERENCE-ONLY. The composer's voice
    /// (phrasings, register-tics, sentence-shape) must NOT be lifted into
    /// the outgoing reply. Content may inform WHAT is discussed but not
    /// HOW it is said. Enforced at the consumer surface by wrapping the
    /// injected block with visible boundary markers the model can attend
    /// to; the actual voice-preservation guarantee is training-side and
    /// output-boundary (Theme J).
    /// </summary>
    ReferenceOnlyDoNotAdoptVoice = 0,
}

/// <summary>
/// Foundation Input (F-1) Phase 4 (2026-08-18) — producer-boundary envelope
/// for the conscious-substrate gist. Wraps the gist text with an explicit
/// <see cref="Treatment"/> directive so downstream consumers can render it
/// with boundary markers (per Theme M architectural rule §4.6: substrate is
/// merged as framing, not turn-adjacent user-role content).
///
/// <para>
/// <see cref="IProvenancedContent{T}.Content"/> returns the composed gist
/// body. <see cref="IProvenancedContent{T}.SourceType"/> renders as
/// <c>"gist.substrate"</c>. Same envelope pattern as
/// <c>IActiveTriggerEnvelope</c> (Phase 2) and <c>IThoughtEnvelope</c>
/// (Phase 3).
/// </para>
///
/// <para>
/// Empirical anchor for the treatment-directive concept: the 2026-06-10
/// production self-echo cascade (documented in
/// <c>ConsciousSubstrateGistObservation.cs</c> G.1 note) where the gist
/// alone became a user-role turn and the model lifted phrasings from it.
/// The G.1 fix moved the gist to the system prompt when the directive is
/// system-side; Phase 4 layers explicit boundary markers on the gist body
/// itself so intent is legible regardless of which prompt-slot it lands in.
/// </para>
/// </summary>
public interface ISubstrateGistEnvelope : IProvenancedContent<string>
{
    /// <summary>The directive telling consumers how the wrapped substrate should be used.</summary>
    SubstrateGistTreatment Treatment { get; }
}
