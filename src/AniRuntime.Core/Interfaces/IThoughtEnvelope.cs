using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 3 (2026-08-18) — producer-boundary envelope
/// for an inner thought. Wraps the raw thought text with a
/// <see cref="Shape"/> classification (mumble-loop / coherent-thought /
/// fact-catalog / third-person-frame / unclassified) so downstream
/// consumers can distinguish healthy first-person interior monologue from
/// the empirically-observed pathologies without re-classifying at each
/// call site.
///
/// <para>
/// <see cref="IProvenancedContent{T}.Content"/> returns the thought text.
/// <see cref="IProvenancedContent{T}.SourceType"/> is derived from
/// <see cref="Shape"/> as <c>"thought.{shape}"</c>. Same pattern as
/// <c>IActiveTriggerEnvelope</c> (Phase 2).
/// </para>
///
/// <para>
/// See F-1 Phase 3 sub-tasks in
/// <c>ani-docs/spec/ANI-Foundation-Input-Refactor-Plan.md</c>. Acceptance
/// (weakened from plan verbatim on 2026-08-18): zero fact-catalog or
/// third-person-frame shapes reach outreach COMPOSITION after 48h of
/// production observation; instrumentation counts per-shape at classify
/// time and at composition boundary.
/// </para>
/// </summary>
public interface IThoughtEnvelope : IProvenancedContent<string>
{
    /// <summary>The classified shape of the wrapped thought.</summary>
    ThoughtShape Shape { get; }
}
