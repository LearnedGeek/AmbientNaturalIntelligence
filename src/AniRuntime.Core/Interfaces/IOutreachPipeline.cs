using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// The proactive-outreach pipeline. Runs the full per-cycle outreach flow:
/// decision → frame select → grounding retrieval → compose → cleanup →
/// gates (ML-confab, frame-coherence, echo, claim-verify, output-gate) →
/// dispatch → record (thread + episodic). Each suppression point manages
/// its own desire decay + cooldown internally.
///
/// <para>
/// Extracted from <c>OutreachPhase</c> in §5.3 SOLID refactor (2026-05-18)
/// so the phase becomes a pure router (proactive / reactive / silence)
/// and the heavy orchestration lives in one place with a single
/// responsibility: "run the proactive outreach flow."
/// </para>
/// </summary>
public interface IOutreachPipeline
{
    // Audit Root A (#197, 2026-11-27): returns the dispatched message
    // + the coherence-gate "door" label on suppression, so the cycle
    // pipeline can wire obs.OutreachMessage + obs.CoherenceGateDoor.
    // Pre-fix those two fields were null on every cycle, hiding H2
    // (ResonanceScorer tier branches) + H8 (EmergenceObserver
    // register/door columns).
    Task<OutreachResult> RunAsync(ContextSnapshot snapshot, string recentThought, CancellationToken ct);
}

/// <summary>
/// Result of one proactive-outreach attempt. <see cref="Message"/> is the
/// final dispatched text (null on any non-dispatch outcome). <see cref="CoherenceGateDoor"/>
/// labels which gate closed when outreach was suppressed (e.g.
/// "output-gate:InnerThoughtBleedInvariant", "frame-coherence",
/// "claim-verification", "echo", "ml-confab", "frame-selector-none",
/// "low-confidence", "no-reach", "parse-failure"), or null on a successful
/// dispatch / an early short-circuit the gate stack never reached.
/// </summary>
public sealed record OutreachResult(string? Message, string? CoherenceGateDoor)
{
    public static readonly OutreachResult None = new(null, null);
}
