using System.Collections.Concurrent;
using System.Net.WebSockets;
using AniRuntime.Core.Models;

namespace AniRuntime.Voice;

/// <summary>
/// Tracks the state of an active voice call. Stored in-memory keyed by session ID.
/// Voice calls are short-lived (minutes), so in-memory storage is appropriate.
/// Messages are buffered in-memory during the call and batch-saved when the call ends
/// to avoid Ollama embedding/contradiction calls stealing inference time from voice replies.
/// </summary>
public class VoiceCallSession
{
    public string CallSid { get; set; } = string.Empty;
    public Guid ThreadId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public int TurnCount { get; set; }
    public DateTimeOffset LastTurnAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Streaming voice fields (Phase 5) ────────────────────────────────────

    /// <summary>WebSocket connection to the MAUI client (null for batch/Twilio calls).</summary>
    public WebSocket? ClientSocket { get; set; }

    /// <summary>True while TTS audio is being sent to the client.</summary>
    public bool IsAniSpeaking { get; set; }

    /// <summary>Cancelled on barge-in to abort the current TTS/LLM pipeline.</summary>
    public CancellationTokenSource? CurrentTurnCts { get; set; }

    /// <summary>
    /// Messages exchanged during the call — saved to the conversation thread
    /// after the call ends so Ollama embedding doesn't compete with voice replies.
    /// Thread-safe: concurrent webhook calls may add messages simultaneously.
    /// </summary>
    public ConcurrentQueue<ConversationMessage> PendingMessages { get; } = new();
}
