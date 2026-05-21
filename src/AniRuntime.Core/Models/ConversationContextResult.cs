namespace AniRuntime.Core.Models;

/// <summary>
/// Output of <c>IConversationContextBuilder.BuildAsync</c>. Carries the three
/// conversation-context fields that <c>ContextBuilder</c> previously assembled
/// inline. Extracted 2026-05-19 as the third sub-builder of the
/// ContextBuilder SRP decomposition (Testability Architecture Plan §2).
/// </summary>
/// <remarks>
/// All three fields are nullable — each block degrades to null independently
/// when its upstream service is missing or its retrieval fails, matching the
/// pre-extraction inline shape exactly.
/// </remarks>
public sealed record ConversationContextResult(
    string?                         RecentConversationSummary,
    StructuredConversationSummary?  StructuredConversationSummary,
    ClosedConversationRecord?       RecentClosedConversation);
