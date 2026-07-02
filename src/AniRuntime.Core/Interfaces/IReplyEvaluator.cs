using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Runs the J.5a universal output gate over a composed reply, regenerates
/// once on Remediate verdict, re-evaluates the regen, and falls through
/// to a safe acknowledgement when both attempts fail. Owns the gate-trip
/// telemetry recording at each exit point (Pass / RemediatedOk / FellThroughToSafeAck).
///
/// <para>
/// Producer-side direct-address rewrite (DirectAddressRewriter) runs
/// inside this service so the gate's <c>DirectAddressInvariant</c> only
/// sees post-rewrite content — same shape as the prior inline behaviour.
/// </para>
///
/// <para>
/// Extracted from <c>ConversationReplyPipeline</c> in §5.4e SOLID
/// refactor (2026-05-18). Drops <c>ICognitiveOutputGate</c> and
/// <c>IRecentGateTripTracker</c> from the pipeline's direct deps.
/// </para>
/// </summary>
public interface IReplyEvaluator
{
    /// <summary>
    /// Evaluate the reply against the output gate; remediate on
    /// Remediate verdict; return the safe-acknowledgement on Fail or on
    /// re-evaluation failure. When the gate is not registered or the
    /// feature flag is off, returns the original reply unchanged.
    /// </summary>
    Task<string> EvaluateAndRemediateAsync(
        string reply,
        ConversationThread thread,
        ContextSnapshot snapshot,
        ConversationMessage replyMessage,
        PromptPair replyPrompt,
        float replyTemperature,
        CancellationToken ct,
        bool composerIsThin = false);
}
