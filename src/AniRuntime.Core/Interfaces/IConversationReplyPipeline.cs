using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Handles a single inbound conversation turn end-to-end: context build,
/// continuation detection, reply decision (silence vs respond), reply
/// generation (LLM), output-gate evaluation + remediation, dispatch, and
/// post-reply emotional processing (care detect, hurt detect, lexical
/// anchors, contact-gap tension dissipation).
///
/// <para>
/// Extracted from <c>ConversationReplyPhase</c> in §5.4 SOLID refactor
/// (2026-05-18). The phase keeps a narrow facade for backwards-
/// compatibility with CognitiveCycleProcessor + the existing test
/// fixtures; the body of work lives here.
/// </para>
/// </summary>
public interface IConversationReplyPipeline
{
    // Audit Root A (#197, 2026-11-27): returns the reply that was
    // dispatched so the cycle pipeline can wire obs.ReplyMessage. Returns
    // null on every non-dispatch outcome (terminal message, silence
    // heuristic, empty reply, suppression, exception). Pre-fix the
    // observation's ReplyMessage was null on every conversation cycle,
    // hiding the H2 ResonanceScorer.OutreachQuality reply-tier branch.
    Task<string?> RunAsync(
        ConversationThread thread,
        List<PerceptionEvent> perceptions,
        CancellationToken ct,
        EmotionalState? emotionalState = null,
        bool isReconsideration = false);
}
