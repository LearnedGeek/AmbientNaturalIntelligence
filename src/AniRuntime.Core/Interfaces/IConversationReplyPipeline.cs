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
    Task RunAsync(
        ConversationThread thread,
        List<PerceptionEvent> perceptions,
        CancellationToken ct,
        EmotionalState? emotionalState = null,
        bool isReconsideration = false);
}
