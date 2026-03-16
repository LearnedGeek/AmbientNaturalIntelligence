using Microsoft.Extensions.Logging;

namespace AniRuntime.Voice;

/// <summary>
/// Accumulates finalized STT segments and emits a complete utterance after a configurable
/// silence period (debounce). Thread-safe: segments can arrive from the STT receive loop
/// while the debounce timer fires from a background task.
///
/// Extracted from DeepgramStreamingSTTService to isolate turn-detection logic (SRP)
/// and enable independent testing without a WebSocket connection.
/// </summary>
public class DebouncedUtterance
{
    private readonly int _debounceMs;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly List<string> _segments = new();
    private CancellationTokenSource? _debounceCts;

    /// <summary>Fires when the debounce period elapses with the full accumulated utterance.</summary>
    public event Action<string>? UtteranceReady;

    public DebouncedUtterance(int debounceMs, ILogger logger)
    {
        _debounceMs = debounceMs;
        _log = logger;
    }

    /// <summary>
    /// Add a finalized segment. Resets the debounce timer. After <see cref="_debounceMs"/>
    /// of no new segments, the accumulated utterance fires via <see cref="UtteranceReady"/>.
    /// </summary>
    public void AddSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return;

        lock (_lock)
        {
            _segments.Add(segment);
            _log.LogDebug("DebouncedUtterance: segment added \"{Segment}\" (total {Count})",
                segment, _segments.Count);
        }

        ResetTimer();
    }

    /// <summary>
    /// Discard all accumulated segments and cancel the debounce timer.
    /// Used to clear echo artifacts after Ani finishes speaking.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _debounceCts?.Cancel();
            var count = _segments.Count;
            _segments.Clear();
            if (count > 0)
                _log.LogDebug("DebouncedUtterance: cleared {Count} segments", count);
        }
    }

    /// <summary>
    /// Flush any accumulated segments immediately without waiting for the debounce timer.
    /// Used during graceful shutdown. Does NOT fire UtteranceReady if segments are empty.
    /// </summary>
    public void Flush()
    {
        lock (_lock)
        {
            _debounceCts?.Cancel();
            EmitIfReady();
        }
    }

    /// <summary>Number of currently accumulated segments (for testing/diagnostics).</summary>
    public int PendingCount
    {
        get { lock (_lock) return _segments.Count; }
    }

    private void ResetTimer()
    {
        CancellationToken token;
        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceMs, token).ConfigureAwait(false);
                lock (_lock)
                {
                    EmitIfReady();
                }
            }
            catch (OperationCanceledException) { /* timer was reset or cancelled */ }
        });
    }

    /// <summary>Must be called under _lock.</summary>
    private void EmitIfReady()
    {
        if (_segments.Count == 0) return;

        var utterance = string.Join(" ", _segments);
        _segments.Clear();
        _log.LogInformation("DebouncedUtterance: utterance ready \"{Utterance}\"", utterance);

        // Fire outside lock would be better but the handler should be fast (just enqueues work).
        // Keeping it under lock to prevent Clear() from racing with the event invocation.
        UtteranceReady?.Invoke(utterance);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
            _segments.Clear();
        }
    }
}
