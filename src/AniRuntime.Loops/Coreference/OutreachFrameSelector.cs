using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops.Coreference;

/// <summary>
/// Theme N Phase N.1 (May 8, 2026) — SKELETON.
///
/// This is the deterministic-ranking implementation of
/// <see cref="IOutreachFrameSelector"/>. The interface is locked; the
/// scoring algorithm is intentionally unimplemented at N.1 — N.2 ships
/// the scoring + tier-scoped queries against the substrate populated in
/// the snapshot.
///
/// **N.1 deliverable boundary:** interface signature + skeleton class +
/// DI wiring shape + spec tests for the contract. No frame-selection
/// behavior yet — every call returns <see cref="OutreachFrame.None"/>
/// (the safe fallback that suppresses outreach when uncertain). When
/// the skeleton is wired into <see cref="OutreachPhase"/> behind a flag
/// (planned for N.3), it has the effect of suppressing all outreaches —
/// which is intentional: the scaffolding is verified end-to-end before
/// the scoring landed gives any frame a non-None result.
///
/// **N.2 will ship:**
/// <list type="bullet">
///   <item>Per-tier candidate retrieval (Facts/Episodic/Interior queries against the snapshot's substrate).</item>
///   <item>Recency × salience × frame-specific-weight scoring.</item>
///   <item>Top-frame selection with confidence calibration.</item>
///   <item>Telemetry log line <c>N4_FRAME_SELECTED</c>.</item>
/// </list>
///
/// **Why log this skeleton state.** The architectural property we're
/// pinning is that frame-selection is deterministic, snapshot-driven,
/// and never invokes an LLM in the gate path. The spec tests for this
/// skeleton enforce those invariants up front, so when N.2 lands the
/// implementation can be reviewed against tests that already pin the
/// shape of the contract.
/// </summary>
public sealed class OutreachFrameSelector : IOutreachFrameSelector
{
    private readonly ILogger<OutreachFrameSelector> _log;

    public OutreachFrameSelector(ILogger<OutreachFrameSelector> log)
    {
        _log = log;
    }

    public Task<OutreachFrame> SelectFrameAsync(
        ContextSnapshot snapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        // N.1 SKELETON: always return None. N.2 ships scoring.
        // Logging the skeleton state explicitly so deployment watchers
        // can confirm the selector is reachable + the substrate-suppress
        // safe fallback is firing.
        _log.LogDebug(
            "OutreachFrameSelector N.1 skeleton — returning None " +
            "(scoring not yet shipped; outreach will be suppressed).");

        return Task.FromResult(OutreachFrame.None);
    }
}
