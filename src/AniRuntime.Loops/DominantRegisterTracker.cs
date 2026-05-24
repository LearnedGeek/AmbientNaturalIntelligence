using AniRuntime.Core.Interfaces;

namespace AniRuntime.Loops;

/// <summary>
/// Theme R.1 (2026-05-24, Issue #64) — in-memory singleton implementation of
/// <see cref="IDominantRegisterTracker"/>. Holds the most-recent inner-thought
/// dominant register; lock-free read, locked write. Lost on service restart by
/// design (the next inner-thought cycle repopulates).
/// </summary>
public sealed class DominantRegisterTracker : IDominantRegisterTracker
{
    private readonly object _lock = new();
    private string? _current;

    public string? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Record(string? register, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(register)) return;
        lock (_lock)
        {
            _current = register;
        }
    }
}
