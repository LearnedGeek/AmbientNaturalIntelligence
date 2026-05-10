// Theme O Phase O.1 (May 10, 2026) — pipeline-result type.
// See docs/spec/ANI-Theme-O-Cognitive-Pipeline-Middleware-Plan.md §3.
using AniRuntime.Core.Interfaces;
using DispatchVerdict = AniRuntime.Core.Models.OutputGateVerdict;

namespace AniRuntime.Core.Models;

/// <summary>
/// Theme O Phase O.1 (May 10, 2026) — final result of running an artifact
/// through the cognitive pipeline. The producer dispatches on
/// <see cref="DispatchVerdict.Pass"/>, remediates on
/// <see cref="DispatchVerdict.Remediate"/>, and abandons on
/// <see cref="DispatchVerdict.Fail"/>.
///
/// When a handler short-circuits, <see cref="ShortCircuitHandler"/> and
/// <see cref="ShortCircuitStage"/> identify which handler stopped the
/// pipeline; on a successful Pass these are null.
/// </summary>
/// <param name="Verdict">The verdict the caller acts on.</param>
/// <param name="Reason">Optional reason string — surfaced from the
/// short-circuiting handler or set to null on Pass.</param>
/// <param name="ShortCircuitHandler">The <see cref="ICognitivePipelineHandler.Name"/>
/// of the handler that short-circuited, or null when the pipeline ran to
/// completion.</param>
/// <param name="ShortCircuitStage">The stage of the short-circuiting handler,
/// or null when the pipeline ran to completion.</param>
public sealed record DispatchResult(
    DispatchVerdict Verdict,
    string?         Reason,
    string?         ShortCircuitHandler,
    PipelineStage?  ShortCircuitStage)
{
    /// <summary>Pipeline completed; all handlers ran (or were skipped via
    /// <see cref="ICognitivePipelineHandler.AppliesTo"/>); verdict is Pass.</summary>
    public static DispatchResult Pass() =>
        new(DispatchVerdict.Pass, null, null, null);
}
