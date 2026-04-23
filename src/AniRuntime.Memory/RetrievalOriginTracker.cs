using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;

namespace AniRuntime.Memory;

/// <summary>
/// In-memory ring-buffer implementation of <see cref="IRetrievalOriginTracker"/>.
///
/// Agentic Lens Layer 1 Phase 1d (Apr 2026). Holds the last N cycles' origin
/// distributions so the feedback-loop perception source can ask "how much has
/// recent retrieval been my own prior output?" without re-scanning logs.
///
/// Lives in AniRuntime.Memory because it shares a lifetime concern with the
/// memory service and naturally extends the retrieval observability layer
/// introduced by <see cref="RetrievalOriginClassifier"/> in Phase 1a.
/// </summary>
public sealed class RetrievalOriginTracker : IRetrievalOriginTracker
{
    private readonly int _capacity;
    private readonly Queue<IReadOnlyDictionary<RetrievalOrigin, int>> _buffer;
    private readonly object _lock = new();

    public RetrievalOriginTracker(int capacity = 30)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buffer   = new Queue<IReadOnlyDictionary<RetrievalOrigin, int>>(capacity);
    }

    public int RecordedCycleCount
    {
        get { lock (_lock) return _buffer.Count; }
    }

    public void RecordCycle(IReadOnlyDictionary<RetrievalOrigin, int> distribution)
    {
        if (distribution is null) return;

        // Take a defensive copy so callers can't mutate the stored snapshot.
        var snapshot = new Dictionary<RetrievalOrigin, int>(distribution);

        lock (_lock)
        {
            while (_buffer.Count >= _capacity)
                _buffer.Dequeue();
            _buffer.Enqueue(snapshot);
        }
    }

    public IReadOnlyDictionary<RetrievalOrigin, int> GetRecentDistribution(int cycleWindow = 10)
    {
        if (cycleWindow <= 0) return new Dictionary<RetrievalOrigin, int>();

        var aggregate = new Dictionary<RetrievalOrigin, int>();

        lock (_lock)
        {
            // The Queue iterates front-to-back (oldest-to-newest). We want
            // the newest N, so we skip the older entries when the buffer
            // exceeds the requested window.
            int skip = Math.Max(0, _buffer.Count - cycleWindow);
            int i = 0;
            foreach (var cycle in _buffer)
            {
                if (i++ < skip) continue;
                foreach (var kvp in cycle)
                {
                    aggregate.TryGetValue(kvp.Key, out var running);
                    aggregate[kvp.Key] = running + kvp.Value;
                }
            }
        }

        return aggregate;
    }
}
