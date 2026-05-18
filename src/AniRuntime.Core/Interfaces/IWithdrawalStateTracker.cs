namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Feature 18 reactive-withdrawal state — a transient quieter-tone window
/// triggered by hurt detection. While withdrawn, proactive outreach is
/// suppressed and reply composition uses a quieter tone.
///
/// <para>
/// Singleton: the window is process-wide and must survive across the
/// transient consumers that read it (ConversationReplyPhase orchestrator,
/// CognitiveCycleProcessor's outreach gate).
/// </para>
///
/// <para>
/// Extracted from <c>ConversationReplyPhase</c> in §5.4 SOLID refactor
/// (2026-05-18) so the phase no longer holds instance state.
/// </para>
/// </summary>
public interface IWithdrawalStateTracker
{
    bool IsWithdrawn { get; }

    /// <summary>
    /// Set or clear the expiry. Pass <c>null</c> to end withdrawal early
    /// (rare); pass a future timestamp to start or extend the window.
    /// </summary>
    void SetExpiry(DateTimeOffset? expiresAt);
}
