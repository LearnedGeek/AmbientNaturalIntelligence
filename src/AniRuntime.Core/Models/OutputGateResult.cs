namespace AniRuntime.Core.Models;

/// <summary>
/// Theme J Phase J.4 — outcome of running an artifact through
/// <see cref="Interfaces.ICognitiveOutputGate"/>. The gate accumulates
/// invariant results and returns one of three verdicts; the producer
/// then chooses its own remediation behaviour from
/// <see cref="RemediationHint"/> (consistent with Mark's
/// *"pipeline-specific post-remediation remains"* design constraint).
/// </summary>
public sealed class OutputGateResult
{
    public required OutputGateVerdict       Verdict          { get; init; }
    public IReadOnlyList<string>            FiredInvariants  { get; init; } = Array.Empty<string>();
    public string?                          RemediationHint  { get; init; }

    public static OutputGateResult Pass() =>
        new() { Verdict = OutputGateVerdict.Pass };
}

public enum OutputGateVerdict
{
    /// <summary>All applicable invariants passed; producer dispatches as-is.</summary>
    Pass         = 1,

    /// <summary>One or more invariants failed; producer should regenerate or abandon per the hint.</summary>
    Remediate    = 2,

    /// <summary>Hard fail — invariant detected a structurally unsafe output (e.g. confabulation classifier high-confidence). Producer should NOT dispatch.</summary>
    Fail         = 3,
}

/// <summary>
/// Per-invariant evaluation result. Invariants return Pass or Fail
/// individually; the gate aggregates them.
/// </summary>
public sealed class InvariantResult
{
    public required bool Passed { get; init; }

    /// <summary>
    /// Producer-readable hint when an invariant fails. Should describe
    /// WHAT is wrong in a way the producer can use for regeneration
    /// (e.g. *"reply contains 7+ token verbatim lift from contact's
    /// last message: '...'"*).
    /// </summary>
    public string? RemediationHint { get; init; }

    /// <summary>
    /// Severity hint — when true, the gate escalates the verdict from
    /// <see cref="OutputGateVerdict.Remediate"/> to
    /// <see cref="OutputGateVerdict.Fail"/>. Used by structurally-unsafe
    /// failures (e.g. confabulation high-confidence) where regeneration
    /// won't help.
    /// </summary>
    public bool IsHardFail { get; init; }

    public static InvariantResult Pass() => new() { Passed = true };

    public static InvariantResult Fail(string hint, bool hard = false) =>
        new() { Passed = false, RemediationHint = hint, IsHardFail = hard };
}
