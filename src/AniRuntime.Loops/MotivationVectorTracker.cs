using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Loops;

/// <summary>
/// Theme R.4 (2026-05-24, Issue #64) — in-memory singleton implementation of
/// <see cref="IMotivationVectorTracker"/>. Holds the most-recent Layer 2
/// motivation vector; lock-free read, locked write.
/// </summary>
public sealed class MotivationVectorTracker : IMotivationVectorTracker
{
    private readonly object _lock = new();
    private MotivationVector? _current;

    public MotivationVector? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Record(MotivationVector vector, DateTimeOffset at)
    {
        lock (_lock)
        {
            _current = vector;
        }
    }
}
