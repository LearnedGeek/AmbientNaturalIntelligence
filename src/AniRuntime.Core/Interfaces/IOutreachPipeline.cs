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
    Task RunAsync(ContextSnapshot snapshot, string recentThought, CancellationToken ct);
}
