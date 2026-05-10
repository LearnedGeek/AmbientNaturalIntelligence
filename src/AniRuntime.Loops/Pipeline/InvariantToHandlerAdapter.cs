using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using DispatchVerdict = AniRuntime.Core.Models.OutputGateVerdict;

namespace AniRuntime.Loops.Pipeline;

/// <summary>
/// Theme O Phase O.2 (May 10, 2026) — thin adapter that wraps a Theme J
/// <see cref="ICognitiveOutputInvariant"/> as an
/// <see cref="ICognitivePipelineHandler"/> Post-stage handler. This is
/// the migration shim: existing invariants stay implemented against
/// <see cref="ICognitiveOutputInvariant"/>; the pipeline sees them as
/// uniform handlers via this adapter.
///
/// **Semantic mapping** (preserved from
/// <see cref="CognitiveOutputGate.EvaluateAsync"/>):
/// <list type="bullet">
///   <item><description><see cref="InvariantResult.Passed"/> = true → <see cref="HandlerResult.Continue"/></description></item>
///   <item><description><see cref="InvariantResult.Passed"/> = false + <see cref="InvariantResult.IsHardFail"/> = false → short-circuit with <see cref="DispatchVerdict.Remediate"/></description></item>
///   <item><description><see cref="InvariantResult.Passed"/> = false + <see cref="InvariantResult.IsHardFail"/> = true  → short-circuit with <see cref="DispatchVerdict.Fail"/></description></item>
/// </list>
///
/// **Exception handling**: this adapter intentionally does NOT catch
/// invariant exceptions. The pipeline's <see cref="CognitivePipeline.InvokeHandlerAsync"/>
/// already wraps every handler call in a try/catch that emits the
/// <c>O_HANDLER_END</c> warning line and short-circuits with
/// <see cref="DispatchVerdict.Fail"/>. Catching twice would mean either
/// duplicated logging or silently-swallowed bugs — both worse than the
/// single canonical catch in the orchestrator.
/// </summary>
public class InvariantToHandlerAdapter : ICognitivePipelineHandler
{
    private readonly ICognitiveOutputInvariant _invariant;

    public InvariantToHandlerAdapter(ICognitiveOutputInvariant invariant)
    {
        _invariant = invariant ?? throw new ArgumentNullException(nameof(invariant));
    }

    public PipelineStage Stage => PipelineStage.Post;

    public string Name => _invariant.Name;

    public bool AppliesTo(CognitiveArtifact artifact) => _invariant.AppliesTo(artifact);

    public async Task<HandlerResult> HandleAsync(CognitivePipelineContext ctx, CancellationToken ct)
    {
        var result = await _invariant.EvaluateAsync(ctx.Artifact, ct).ConfigureAwait(false);
        if (result.Passed) return HandlerResult.Continue();

        var verdict = result.IsHardFail ? DispatchVerdict.Fail : DispatchVerdict.Remediate;
        return HandlerResult.ShortCircuitWith(verdict, result.RemediationHint ?? string.Empty);
    }
}
