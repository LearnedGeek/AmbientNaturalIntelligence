using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme J Phase J.4 (Apr 30, 2026) — the shared output-evaluation
/// surface every cognitive producer routes through at its output
/// boundary. Returns an <see cref="OutputGateResult"/>; producer
/// chooses its own remediation behaviour based on
/// <see cref="OutputGateResult.RemediationHint"/>.
///
/// **Why a single gate** instead of per-pipeline detectors: the Apr 24
/// guard-consistency audit found that 8 of 12 multi-pipeline failure
/// classes were unevenly covered (parroting, claim verification, source
/// attribution, temporal attribution, confabulation, echo, pronoun-fix,
/// care/hurt detection). Pipeline-scoped detectors meant fixes to one
/// path didn't propagate; the Apr 30 morning bugs (prompt-template
/// language leak; confabulation laundering across 5+ inner-thought
/// cycles in 70 minutes; Facts-tier search admitting Ani's own output as
/// evidence supporting Mark-action claims) are concrete instances of
/// the failure class. The gate consolidates the rule definitions; J.5
/// migrates each producer through the gate one at a time.
///
/// **What the gate does NOT do**: remediate. The verdict +
/// <see cref="OutputGateResult.RemediationHint"/> tells the producer
/// what's wrong; the producer chooses its own response (regenerate,
/// fall back to safe heuristic, abandon, etc.). This preserves the
/// *"pipeline-specific post-remediation remains"* design constraint
/// from the Theme J plan.
/// </summary>
public interface ICognitiveOutputGate
{
    /// <summary>
    /// Run all invariants that apply to this artifact (per
    /// <see cref="ICognitiveOutputInvariant.AppliesTo"/>) in deterministic
    /// order. Returns aggregated result. Never throws on invariant
    /// failure — invariant exceptions are logged and converted to a
    /// soft Pass to avoid blocking pipelines on observability bugs.
    /// </summary>
    Task<OutputGateResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct = default);
}
