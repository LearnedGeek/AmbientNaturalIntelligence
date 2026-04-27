using AniRuntime.Core.Interfaces;

namespace AniRuntime.Memory;

/// <summary>
/// In-memory implementation of <see cref="IPerceptionSourceHealthTracker"/>.
///
/// Outage Perception Source support (Apr 27, 2026; backlog 15.19, motivating
/// case Apr 14-15 power outage). Records the last-success and last-failure
/// timestamps for each perception source so the
/// <c>OutagePerceptionSource</c> can compute the "world has gone quiet"
/// condition without re-scanning Serilog stack traces.
///
/// Lives in AniRuntime.Memory alongside <see cref="RetrievalOriginTracker"/>
/// — both are observability-substrate trackers populated by cognitive-cycle
/// phases and consumed by perception sources.
/// </summary>
public sealed class PerceptionSourceHealthTracker : IPerceptionSourceHealthTracker
{
    private readonly Dictionary<string, SourceHealth> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void RecordSuccess(string sourceName, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return;
        lock (_lock)
        {
            if (!_state.TryGetValue(sourceName, out var h))
                h = new SourceHealth();
            h.LastSuccessAt = at;
            // A success clears any in-flight failure stretch.
            h.FailingSinceAt = null;
            _state[sourceName] = h;
        }
    }

    public void RecordFailure(string sourceName, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return;
        lock (_lock)
        {
            if (!_state.TryGetValue(sourceName, out var h))
                h = new SourceHealth();
            h.LastFailureAt = at;
            // Start the failure stretch on first failure, leave alone on
            // subsequent ones. The stretch ends when a success is recorded.
            h.FailingSinceAt ??= at;
            _state[sourceName] = h;
        }
    }

    public bool IsTracked(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return false;
        lock (_lock) return _state.ContainsKey(sourceName);
    }

    public IReadOnlyList<string> GetSourcesFailingFor(TimeSpan minimumDuration, DateTimeOffset now)
    {
        var failing = new List<string>();
        lock (_lock)
        {
            foreach (var (name, h) in _state)
            {
                if (h.FailingSinceAt is { } since && (now - since) >= minimumDuration)
                    failing.Add(name);
            }
        }
        return failing;
    }

    public IReadOnlyList<string> GetSourcesRecoveredWithin(TimeSpan recoveryWindow, DateTimeOffset now)
    {
        var recovered = new List<string>();
        lock (_lock)
        {
            foreach (var (name, h) in _state)
            {
                // A "recovered" source is one whose last success is recent
                // AND whose last failure preceded it (i.e., it had been
                // failing). If LastFailureAt is null, it never failed, so
                // not a recovery event.
                if (h.LastSuccessAt is { } success
                    && h.LastFailureAt is { } failure
                    && success > failure
                    && (now - success) <= recoveryWindow)
                {
                    recovered.Add(name);
                }
            }
        }
        return recovered;
    }

    private sealed class SourceHealth
    {
        public DateTimeOffset? LastSuccessAt    { get; set; }
        public DateTimeOffset? LastFailureAt    { get; set; }
        public DateTimeOffset? FailingSinceAt   { get; set; }
    }
}
