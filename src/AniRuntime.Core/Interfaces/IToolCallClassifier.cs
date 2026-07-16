using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Issue #96 (2026-07-15) — LLM-backed classifier that reads the user's
/// message against a set of available tool descriptors and returns a
/// structured <see cref="ToolCallVerdict"/> — either "call this tool with
/// these arguments" or "no tool needed."
///
/// **Two-model split (mirrors Issue #93 discipline).** The classifier runs
/// on the local verifier model (<c>qwen3:14b</c> by default) via
/// <see cref="IOllamaClient.ChatJsonWithModelAsync"/> — the same seam
/// <see cref="ITagIntentClassifier"/> and <see cref="IContentContradictionClassifier"/>
/// use. The character model (<c>ani-v7-conversation</c>) never sees the
/// tool-selection decision directly; if a tool fires, its result is injected
/// as context on the character-model's next call.
///
/// **Failure contract.** Any transport / parse / timeout failure returns
/// <see cref="ToolCallVerdict"/> with <c>ShouldCallTool=false</c> — never
/// throws. Caller fails open: proceed with the untooled conversational path.
/// Matches the routing-classifier / tag-intent / verifier fallback shape.
///
/// **Empirical baseline before wiring.** Per Issue #96 test-first discipline,
/// the classifier is validated via <c>AniRuntime.Eval --tool-call &lt;fixture&gt;</c>
/// on a canonical fixture (obvious tool-required + obvious no-tool + ambiguous)
/// before any production integration into <c>ConversationReplyPipeline</c>.
/// Target: ≥90% accuracy per the issue's acceptance criteria.
/// </summary>
public interface IToolCallClassifier
{
    /// <summary>
    /// Classify the user message against the available tool descriptors.
    /// </summary>
    /// <param name="userMessage">The message the user just sent Ani. Verbatim
    /// text — do not pre-strip or normalize.</param>
    /// <param name="availableTools">Tools the classifier may choose from.
    /// Empty list is legal (classifier returns <c>ShouldCallTool=false</c>).</param>
    /// <param name="conversationContext">Optional short summary of the prior
    /// turn(s) — helps disambiguate follow-up questions like "what about the
    /// other one?" Pass empty string when no context available.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ToolCallVerdict> ClassifyAsync(
        string                        userMessage,
        IReadOnlyList<ToolDescriptor> availableTools,
        string                        conversationContext,
        CancellationToken             ct);
}
