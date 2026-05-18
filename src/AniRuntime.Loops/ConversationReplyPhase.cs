using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Loops;

/// <summary>
/// Inbound-conversation coordinator. The body of work (~1100 lines covering
/// context build, continuation detection, decision/silence heuristic,
/// LLM reply, output-gate evaluation + remediation, dispatch, and
/// post-reply emotional processing) lives in
/// <see cref="IConversationReplyPipeline"/>.
///
/// <para>
/// Orchestrator SOLID refactor §5.4b (2026-05-18) — previously a 24-dep
/// class. Now a 2-dep facade plus the withdrawal-state passthrough.
/// CognitiveCycleProcessor still calls <c>RunConversationReplyAsync</c>,
/// <c>IsWithdrawn</c>, and <c>SetWithdrawalExpiry</c> on this type;
/// those members are now passthroughs to the pipeline / tracker, so the
/// upstream call sites + test fixtures don't change shape until §5.5
/// rewires the cycle processor.
/// </para>
/// </summary>
public class ConversationReplyPhase
{
    private readonly IConversationReplyPipeline _pipeline;
    private readonly IWithdrawalStateTracker _withdrawal;

    public ConversationReplyPhase(
        IConversationReplyPipeline pipeline,
        IWithdrawalStateTracker withdrawal)
    {
        _pipeline   = pipeline   ?? throw new ArgumentNullException(nameof(pipeline));
        _withdrawal = withdrawal ?? throw new ArgumentNullException(nameof(withdrawal));
    }

    /// <summary>Feature 18 withdrawal window — true while Ani is in a quieter-tone phase.</summary>
    internal bool IsWithdrawn => _withdrawal.IsWithdrawn;

    /// <summary>Facade over <see cref="IWithdrawalStateTracker.SetExpiry"/>.</summary>
    internal void SetWithdrawalExpiry(DateTimeOffset? expiresAt) => _withdrawal.SetExpiry(expiresAt);

    /// <summary>
    /// Run one inbound-reply turn. Delegates to the pipeline implementation.
    /// </summary>
    public Task RunConversationReplyAsync(
        ConversationThread thread,
        List<PerceptionEvent> perceptions,
        CancellationToken ct,
        EmotionalState? emotionalState = null,
        bool isReconsideration = false)
        => _pipeline.RunAsync(thread, perceptions, ct, emotionalState, isReconsideration);
}
