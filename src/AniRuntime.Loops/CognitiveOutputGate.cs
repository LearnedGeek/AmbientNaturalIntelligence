using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Theme J Phase J.4 (Apr 30, 2026) — default
/// <see cref="ICognitiveOutputGate"/> implementation.
///
/// Evaluates a fixed-order list of registered invariants, applying only
/// those whose <see cref="ICognitiveOutputInvariant.AppliesTo"/>
/// predicate matches the artifact. Aggregates results into a single
/// <see cref="OutputGateResult"/> with per-invariant fire annotations
/// and a producer-readable remediation hint.
///
/// **Failure containment**: an invariant that throws is logged and
/// treated as Pass for that artifact — observability bugs in invariants
/// MUST NOT block pipelines from dispatching. The verdict will surface
/// the exception in logs; producers continue.
///
/// **Order**: invariants run in DI registration order. Convention from
/// the Theme J plan: structural checks first (data-shape invariants
/// like source attribution), content checks next (anti-parrot,
/// prompt-template leak), semantic checks last (confabulation, claim
/// verification — heavier, often LLM-bound). DI registration in
/// <c>Program.cs</c> establishes the canonical order.
/// </summary>
public sealed class CognitiveOutputGate : ICognitiveOutputGate
{
    private readonly IReadOnlyList<ICognitiveOutputInvariant> _invariants;
    private readonly ILogger<CognitiveOutputGate>             _log;

    public CognitiveOutputGate(
        IEnumerable<ICognitiveOutputInvariant> invariants,
        ILogger<CognitiveOutputGate>           log)
    {
        _invariants = invariants?.ToList()
            ?? new List<ICognitiveOutputInvariant>();
        _log        = log;
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

        var fired = new List<string>();
        var hints = new List<string>();
        var hardFail = false;

        foreach (var invariant in _invariants)
        {
            if (ct.IsCancellationRequested) break;
            if (!invariant.AppliesTo(artifact)) continue;

            InvariantResult result;
            try
            {
                result = await invariant.EvaluateAsync(artifact, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "CognitiveOutputGate: invariant '{Name}' threw — treating as Pass for this artifact.",
                    invariant.Name);
                continue;
            }

            if (!result.Passed)
            {
                fired.Add(invariant.Name);
                if (!string.IsNullOrWhiteSpace(result.RemediationHint))
                    hints.Add(result.RemediationHint);
                if (result.IsHardFail) hardFail = true;
            }
        }

        if (fired.Count == 0)
        {
            _log.LogDebug("CognitiveOutputGate: {Producer}/{Sink} passed all applicable invariants.",
                artifact.ProducerKind, artifact.IntendedSink);
            return OutputGateResult.Pass();
        }

        var verdict = hardFail ? OutputGateVerdict.Fail : OutputGateVerdict.Remediate;
        _log.LogInformation(
            "CognitiveOutputGate: {Producer}/{Sink} → {Verdict}; fired={Fired}; hint=\"{Hint}\"",
            artifact.ProducerKind, artifact.IntendedSink, verdict,
            string.Join(",", fired), string.Join(" | ", hints));

        return new OutputGateResult
        {
            Verdict          = verdict,
            FiredInvariants  = fired,
            RemediationHint  = hints.Count == 0 ? null : string.Join(" | ", hints),
        };
    }
}
