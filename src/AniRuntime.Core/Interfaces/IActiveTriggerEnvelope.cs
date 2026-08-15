using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Foundation Input (F-1) Phase 2 envelope for active desire triggers.
/// Wraps a <see cref="DesireTrigger"/> with source-type discriminator,
/// producer identity, and (optional) semantic-similarity key so that
/// the outreach composer (<see cref="AniRuntime.LLM.Prompts.OutreachPromptCommand"/>)
/// can render type-tagged, semantically-distinct triggers instead of
/// a semicolon-joined blob.
///
/// <para>
/// <see cref="IProvenancedContent{T}.Content"/> returns the trigger's
/// description text (the human-readable payload the composer actually
/// renders). <see cref="TriggerType"/> and <see cref="Weight"/> preserve
/// the pre-Foundation-Input trigger data so <see cref="AniRuntime.Loops.DesireEngine"/>
/// bookkeeping (desire bump amount, clearance timing) continues to work
/// against the same fields.
/// </para>
///
/// <para>
/// See F-1 Phase 2 sub-tasks in
/// <c>ani-docs/spec/ANI-Foundation-Input-Refactor-Plan.md</c>. Acceptance:
/// ActiveTriggers rendered ≤10 entries, ≥6 semantically-distinct.
/// </para>
/// </summary>
public interface IActiveTriggerEnvelope : IProvenancedContent<string>
{
    /// <summary>The trigger family enum (kept for bookkeeping continuity).</summary>
    TriggerType TriggerType { get; }

    /// <summary>Weight in [0,1] — how strongly this trigger elevated desire.</summary>
    float Weight { get; }
}
