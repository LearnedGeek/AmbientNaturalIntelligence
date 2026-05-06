namespace AniRuntime.Core.Models;

/// <summary>
/// Theme M Phase M.1 (May 6, 2026) — record of a single CognitiveOutputGate
/// firing event, captured for the §4.8 tension-state slice.
///
/// Recorded at the three exit points of
/// <c>ConversationReplyPhase.EvaluateAndRemediateReplyAsync</c> (and, in
/// future producer migrations, equivalent gate-stack call sites in
/// <c>OutreachPhase</c>, <c>InnerThoughtPhase</c>, etc.). The
/// <see cref="IRecentGateTripTracker"/> singleton holds a rolling buffer
/// of recent events; the tension-state slice queries the buffer to
/// surface gate-trip awareness as part of the conscious-substrate gist.
///
/// **Why this telemetry surface exists:** the May 3–4 + May 5 SafeAck
/// fall-through pattern showed the regen reaching for prior-Ani content
/// because the active-thread substrate was thin. Giving the model
/// awareness of *its own recent gate trips* — "the gate just caught my
/// last attempt; that's a signal to reach somewhere fresher" — is one
/// of three §4.8 inputs designed to address that pattern at the
/// substrate level rather than the output-filtering level.
/// </summary>
public sealed record GateTripEvent(
    DateTimeOffset      Timestamp,
    string              ProducerKind,
    string              FiredInvariants,
    GateTripOutcome     Outcome);

/// <summary>
/// Outcome of a gate evaluation at the dispatch boundary.
/// </summary>
public enum GateTripOutcome
{
    /// <summary>
    /// No invariant fired; original output dispatched as-is. The gate
    /// passed transparently. (Recording these is optional; default impls
    /// record only Remediate / FallThrough events to keep the buffer
    /// signal-dense.)
    /// </summary>
    Pass,

    /// <summary>
    /// Gate fired Remediate; regeneration produced output that passed
    /// the re-evaluation gate; regen output dispatched. The "gate did
    /// its job" path.
    /// </summary>
    RemediatedOk,

    /// <summary>
    /// Gate fired Fail OR Remediate-then-regen-also-failed re-eval;
    /// fall-through to <c>SafeAcknowledgement</c>. The conversational-
    /// unusability path documented in the May 3–4 + May 5 research log
    /// entries.
    /// </summary>
    FellThroughToSafeAck,
}
