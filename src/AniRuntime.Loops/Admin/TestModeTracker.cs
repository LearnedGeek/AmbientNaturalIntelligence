using AniRuntime.Core.Interfaces;

namespace AniRuntime.Loops.Admin;

/// <summary>
/// Orchestrator SOLID refactor §5.1 (2026-05-18) — Singleton holding the
/// test-mode flag previously kept as a private field on AdminCommandHandler.
/// </summary>
public sealed class TestModeTracker : ITestModeTracker
{
    private readonly object _lock = new();
    private DateTimeOffset? _startedAt;

    public DateTimeOffset? StartedAt
    {
        get { lock (_lock) return _startedAt; }
    }

    public void Enable()
    {
        lock (_lock)
        {
            _startedAt ??= DateTimeOffset.UtcNow;
        }
    }

    public DateTimeOffset? Disable()
    {
        lock (_lock)
        {
            var prior = _startedAt;
            _startedAt = null;
            return prior;
        }
    }
}
