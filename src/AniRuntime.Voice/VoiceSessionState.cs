using System.Collections.Concurrent;
using System.Net.WebSockets;
using AniRuntime.Core.Models;

namespace AniRuntime.Voice;

/// <summary>
/// Thread-safe state container for a streaming voice session.
/// All mutable state is synchronized — no direct property mutation from multiple threads.
/// Separates state management (S in SOLID) from the orchestrator's pipeline logic.
/// </summary>
public class VoiceSessionState : IDisposable
{
    private readonly object _lock = new();
    private volatile bool _isAniSpeaking;
    private CancellationTokenSource? _currentTurnCts;

    public string SessionId { get; }
    public Guid ThreadId { get; }
    public WebSocket ClientSocket { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public ConcurrentQueue<ConversationMessage> PendingMessages { get; } = new();

    private int _turnCount;
    private DateTimeOffset _lastTurnAt = DateTimeOffset.UtcNow;

    public VoiceSessionState(string sessionId, Guid threadId, WebSocket clientSocket)
    {
        SessionId    = sessionId;
        ThreadId     = threadId;
        ClientSocket = clientSocket;
    }

    /// <summary>Thread-safe read of whether Ani is currently speaking.</summary>
    public bool IsAniSpeaking => _isAniSpeaking;

    /// <summary>Total completed turns in this session.</summary>
    public int TurnCount => Volatile.Read(ref _turnCount);

    /// <summary>Timestamp of last completed turn.</summary>
    public DateTimeOffset LastTurnAt
    {
        get { lock (_lock) return _lastTurnAt; }
    }

    /// <summary>
    /// Mark Ani as speaking. Sets the flag and creates a new per-turn CancellationTokenSource
    /// linked to the provided parent token. Returns the turn-scoped token for pipeline use.
    /// </summary>
    public CancellationToken BeginSpeaking(CancellationToken parentCt)
    {
        lock (_lock)
        {
            _isAniSpeaking = true;
            _currentTurnCts?.Dispose();
            _currentTurnCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
            return _currentTurnCts.Token;
        }
    }

    /// <summary>
    /// Mark Ani as done speaking and record the completed turn.
    /// </summary>
    public void EndSpeaking()
    {
        _isAniSpeaking = false;
        Interlocked.Increment(ref _turnCount);
        lock (_lock)
        {
            _lastTurnAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Cancel the current turn (barge-in). Cancels the turn CTS and clears the speaking flag.
    /// Safe to call from any thread.
    /// </summary>
    public void CancelCurrentTurn()
    {
        lock (_lock)
        {
            _currentTurnCts?.Cancel();
            _isAniSpeaking = false;
        }
    }

    /// <summary>
    /// Mark speaking state without creating a turn CTS (used for greeting synthesis
    /// which doesn't need barge-in cancellation).
    /// </summary>
    public void SetSpeaking(bool speaking)
    {
        _isAniSpeaking = speaking;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _currentTurnCts?.Dispose();
            _currentTurnCts = null;
        }
    }
}
