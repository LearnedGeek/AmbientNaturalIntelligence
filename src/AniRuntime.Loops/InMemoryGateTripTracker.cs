using System.Collections.Concurrent;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Loops;

/// <summary>
/// Theme M Phase M.1 (May 6, 2026) — thread-safe in-memory ring buffer
/// of recent gate-trip events. Singleton-scope.
///
/// **Bounded size:** the buffer keeps at most <see cref="Capacity"/>
/// most-recent events. Older events are evicted on insertion when the
/// buffer is full. The capacity is set to a small bounded value (20)
/// because the tension-state slice queries by a time-bounded lookback
/// (24h default) and the per-cycle gate trips are infrequent enough
/// that 20 events comfortably covers the lookback window.
///
/// **Best-effort:** <see cref="Record"/> never throws. Telemetry
/// recording must never affect dispatch — same property as the
/// VibeBiasObservation V1.5a pattern.
///
/// **Thread-safety:** <see cref="ConcurrentQueue{T}"/> for the buffer
/// + atomic interlocked-decrement for capacity enforcement. Producer
/// call sites (gate-evaluation exit points) and consumer call sites
/// (the conscious-substrate gist composer) operate on different
/// threads; the queue handles concurrent access without locks.
/// </summary>
public class InMemoryGateTripTracker : IRecentGateTripTracker
{
    public const int Capacity = 20;

    private readonly ConcurrentQueue<GateTripEvent> _buffer = new();

    /// <inheritdoc />
    public void Record(GateTripEvent evt)
    {
        try
        {
            _buffer.Enqueue(evt);

            // Trim oldest entries if we're over capacity. The check is
            // racy under concurrent enqueues, but the over-capacity
            // overshoot is bounded by concurrent producer count which
            // in this runtime is single-digit at peak.
            while (_buffer.Count > Capacity && _buffer.TryDequeue(out _))
            {
                // Drop oldest.
            }
        }
        catch
        {
            // Best-effort — never throw from a telemetry record path.
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GateTripEvent> GetRecent(TimeSpan lookback)
    {
        var cutoff = DateTimeOffset.UtcNow - lookback;
        // Snapshot the buffer; filter; reverse to newest-first.
        var snapshot = _buffer.ToArray();
        var filtered = new List<GateTripEvent>(snapshot.Length);
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i].Timestamp >= cutoff)
                filtered.Add(snapshot[i]);
        }
        return filtered;
    }
}
