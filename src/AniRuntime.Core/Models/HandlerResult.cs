// Theme O Phase O.1 (May 10, 2026) — pipeline-handler result types.
// See docs/spec/ANI-Theme-O-Cognitive-Pipeline-Middleware-Plan.md §3.
using DispatchVerdict = AniRuntime.Core.Models.OutputGateVerdict;

namespace AniRuntime.Core.Models;

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — what a pipeline handler returns to the
/// orchestrator. Either <see cref="Continue"/> (let the next handler run) or
/// <see cref="ShortCircuitWith"/> (stop the pipeline; the orchestrator
/// surfaces the verdict + reason to the caller).
///
/// Handlers that mutate context state (e.g. populating
/// <see cref="CognitivePipelineContext.Frame"/>) AND want the pipeline to
/// continue still return <see cref="Continue"/>; the mutation already
/// happened via the shared context.
///
/// <see cref="Reason"/> is what the orchestrator surfaces in the
/// <c>O_HANDLER_END</c> telemetry line (<c>details="..."</c>). Handlers can
/// also stash structured telemetry on the context state-bag via
/// <see cref="CognitivePipelineContext.SetState{T}"/>.
/// </summary>
public sealed record HandlerResult(
    bool             ShortCircuit,
    DispatchVerdict? Verdict,
    string?          Reason)
{
    /// <summary>
    /// Handler succeeded; pipeline continues to the next handler. Reason is
    /// optional — when present, surfaces as the <c>details="..."</c> field on
    /// the <c>O_HANDLER_END</c> telemetry line. Use it to record "what this
    /// handler observed" (e.g. <c>"frame=AniInterior score=0.79"</c>).
    /// </summary>
    public static HandlerResult Continue(string? reason = null) =>
        new(false, null, reason);

    /// <summary>
    /// Handler stops the pipeline. The orchestrator returns a
    /// <see cref="DispatchResult"/> with this verdict, this reason, and the
    /// handler's name + stage so the caller can identify what stopped.
    /// Common values for <paramref name="verdict"/>: <c>Fail</c> when the
    /// handler observed a hard violation, <c>Remediate</c> when the caller
    /// should regenerate, <c>Pass</c> when the handler intentionally cut the
    /// pipeline short for a non-error reason (e.g. "no substrate; skip
    /// dispatch silently").
    /// </summary>
    public static HandlerResult ShortCircuitWith(DispatchVerdict verdict, string reason) =>
        new(true, verdict, reason);
}
