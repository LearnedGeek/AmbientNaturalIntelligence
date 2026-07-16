namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Issue #96 (2026-07-15) — Encapsulates the classify → dispatch flow for
/// a single user turn. Consumers (currently just
/// <c>ConversationReplyPipeline</c>) get a one-liner: "given this user
/// message and this short context, either return a tool result string or
/// null." All the wiring (enumerating <see cref="IToolCallableAction"/>,
/// building the descriptor list, calling <see cref="IToolCallClassifier"/>,
/// dispatching by name, handling unknown names) lives behind this seam.
///
/// **Why a seam.** Two reasons: (a) keeps the pipeline diff for wiring the
/// loop tiny (~5 lines) so future readers can see the intent at the call
/// site, and (b) makes strict-mock unit tests possible for the pipeline
/// integration without materialising a real classifier / action set. The
/// pipeline test just verifies "when flag on and helper returns a string,
/// that string is injected into the prompt; when helper returns null, prompt
/// is unchanged."
/// </summary>
public interface IToolCallInvocation
{
    /// <summary>
    /// Try to invoke a tool for this turn.
    /// </summary>
    /// <param name="userMessage">The user's inbound message, verbatim.</param>
    /// <param name="conversationContext">Short summary of the prior turn(s)
    /// to help the classifier disambiguate follow-ups. Empty string legal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A short human-readable tool-observation string when a tool
    /// fired, or <c>null</c> when no tool was selected / no tools registered
    /// / classifier failed. Caller uses the string as attributed
    /// tool-observation context on the character-model prompt.</returns>
    Task<string?> TryInvokeAsync(
        string            userMessage,
        string            conversationContext,
        CancellationToken ct);
}
