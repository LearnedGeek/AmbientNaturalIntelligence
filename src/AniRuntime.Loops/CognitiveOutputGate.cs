using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Pipeline;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Theme J Phase J.4 (Apr 30, 2026) — default
/// <see cref="ICognitiveOutputGate"/> implementation. **Theme O Phase O.2
/// (May 10, 2026)**: rewired as a pass-through to the cognitive pipeline's
/// Post stage. Producers still call <see cref="EvaluateAsync"/> exactly
/// as before; internally each call now goes through
/// <see cref="CognitivePipeline.RunPostOnlyAsync"/>, which runs the same
/// Theme J invariants wrapped in
/// <see cref="InvariantToHandlerAdapter"/>.
///
/// **Why preserve the gate surface during O.2**: producers (OutreachPhase,
/// ConversationReplyPhase, voice) call <see cref="EvaluateAsync"/> in
/// many places; rewriting all of them is O.4+ scope. Keeping the existing
/// surface lets O.2 ship the consolidation safely under the existing
/// tests and producer call-sites. The gate vanishes entirely in O.7.
///
/// **Semantic mapping** (pipeline DispatchResult → OutputGateResult):
/// <list type="bullet">
///   <item><description>Pass → <see cref="OutputGateVerdict.Pass"/> with empty
///   <see cref="OutputGateResult.FiredInvariants"/>.</description></item>
///   <item><description>Remediate → <see cref="OutputGateVerdict.Remediate"/>
///   with <see cref="DispatchResult.ShortCircuitHandler"/> as the single
///   fired-invariant entry and <see cref="DispatchResult.Reason"/> as the
///   remediation hint.</description></item>
///   <item><description>Fail → <see cref="OutputGateVerdict.Fail"/> with same
///   shape as Remediate.</description></item>
/// </list>
///
/// **Aggregation difference vs the pre-O.2 implementation**: the old gate
/// ran ALL applicable invariants and aggregated their fired names + hints;
/// the pipeline short-circuits on the first failing handler. This is an
/// intentional behaviour shift documented in the Theme O plan §6: each
/// failure is surfaced individually as it happens (with telemetry pointing
/// at the exact handler), and producers re-evaluate after remediation
/// regen anyway, so the second invariant that would have fired on the
/// original output will fire on the regen if it still applies.
/// </summary>
public sealed class CognitiveOutputGate : ICognitiveOutputGate
{
    private readonly CognitivePipeline                 _pipeline;
    private readonly ILogger<CognitiveOutputGate>      _log;

    public CognitiveOutputGate(
        CognitivePipeline                  pipeline,
        ILogger<CognitiveOutputGate>       log)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _log      = log;
    }

    public async Task<OutputGateResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct = default)
    {
        if (artifact is null) throw new ArgumentNullException(nameof(artifact));
        if (string.IsNullOrEmpty(artifact.Content))
        {
            _log.LogDebug("CognitiveOutputGate: empty content artifact for {Producer}/{Sink} — Pass.",
                artifact.ProducerKind, artifact.IntendedSink);
            return OutputGateResult.Pass();
        }

        // Build a minimal pipeline context. The Snapshot field is required-
        // init on CognitivePipelineContext; existing post-stage handlers
        // (Theme J invariants) read the artifact only — they do NOT touch
        // the snapshot — so a default empty instance is safe here. Pre-stage
        // handlers (Theme N selector) are skipped entirely via
        // RunPostOnlyAsync, so they never observe this placeholder either.
        var ctx = new CognitivePipelineContext
        {
            Artifact  = artifact,
            Snapshot  = new ContextSnapshot(),
            StartedAt = DateTimeOffset.UtcNow,
        };

        var dispatch = await _pipeline.RunPostOnlyAsync(ctx, ct).ConfigureAwait(false);

        if (dispatch.Verdict == OutputGateVerdict.Pass)
        {
            _log.LogDebug("CognitiveOutputGate: {Producer}/{Sink} passed all applicable invariants.",
                artifact.ProducerKind, artifact.IntendedSink);
            return OutputGateResult.Pass();
        }

        var firedName = dispatch.ShortCircuitHandler ?? "<unknown>";
        var hint      = dispatch.Reason ?? string.Empty;
        _log.LogInformation(
            "CognitiveOutputGate: {Producer}/{Sink} → {Verdict}; fired={Fired}; hint=\"{Hint}\"",
            artifact.ProducerKind, artifact.IntendedSink, dispatch.Verdict, firedName, hint);

        return new OutputGateResult
        {
            Verdict          = dispatch.Verdict,
            FiredInvariants  = new[] { firedName },
            RemediationHint  = string.IsNullOrEmpty(hint) ? null : hint,
        };
    }
}
