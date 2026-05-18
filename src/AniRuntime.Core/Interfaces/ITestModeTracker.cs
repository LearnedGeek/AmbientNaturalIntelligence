namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Orchestrator SOLID refactor §5.1 (2026-05-18) — extracted from the
/// private <c>_testModeStartedAt</c> field on the legacy AdminCommandHandler.
/// Test-mode state (when test mode started, or null if not in test mode)
/// is shared by <c>TestModeOnCommand</c>, <c>TestModeOffCommand</c>, and
/// <c>StatusCommand</c>.
///
/// <para>
/// **Lifetime: Singleton.** This is intentionally one of the few
/// stateful services. The flag must persist across command invocations,
/// which is exactly what Singleton lifetime provides.
/// </para>
/// </summary>
public interface ITestModeTracker
{
    /// <summary>
    /// Timestamp test mode was enabled, or null when in live mode.
    /// </summary>
    DateTimeOffset? StartedAt { get; }

    /// <summary>
    /// True iff test mode is currently active.
    /// </summary>
    bool IsActive => StartedAt.HasValue;

    /// <summary>
    /// Mark test mode as started. Idempotent — does nothing if already
    /// active (preserves the original StartedAt timestamp).
    /// </summary>
    void Enable();

    /// <summary>
    /// Mark test mode as ended. Returns the original StartedAt timestamp
    /// (so callers can report the duration), or null if test mode wasn't
    /// active.
    /// </summary>
    DateTimeOffset? Disable();
}
