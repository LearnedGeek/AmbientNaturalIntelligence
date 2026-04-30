using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme J Phase J.4 — one universal invariant on cognitive output.
/// Examples: anti-parrot (no 7+ token verbatim contact phrases),
/// prompt-template-leak (no directive phrases like "WHAT IS TRUE" in
/// generated output), claim verification (Mark-action claims must be
/// supported by Mark-asserted facts).
///
/// **Type-conditional applicability** is the load-bearing piece:
/// <see cref="AppliesTo"/> lets the same gate dispatch invariants
/// differently for replies vs inner thoughts vs world experiences.
/// Reply paraphrase requirements differ from inner-thought creative
/// freedom; same gate, different invariants run.
/// </summary>
public interface ICognitiveOutputInvariant
{
    /// <summary>
    /// Stable name for logging + result aggregation. Example:
    /// <c>"anti-parrot"</c>, <c>"prompt-template-leak"</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this invariant applies to the given artifact. Combines
    /// producer kind (which pipeline) with intended sink (where the
    /// output is going). Returning false skips the invariant — does
    /// NOT count as a failure.
    ///
    /// Example: anti-parrot applies when ProducerKind ∈
    /// {ConversationReply, Outreach, ClosedThreadSummary} AND
    /// IntendedSink ∈ {Dispatch, PersistedSummary}.
    /// </summary>
    bool AppliesTo(CognitiveArtifact artifact);

    /// <summary>
    /// Run the invariant against the artifact. Pure function of the
    /// artifact's content + context fields; side-effects (logging,
    /// metrics) are allowed but persistence is forbidden.
    /// </summary>
    Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact artifact, CancellationToken ct);
}
