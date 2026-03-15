using System.Collections.Concurrent;
using AniRuntime.Core.Models;

namespace AniRuntime.Voice;

/// <summary>
/// Tracks the state of an active voice call. Stored in-memory keyed by Twilio CallSid.
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

    /// <summary>
    /// Messages exchanged during the call — saved to the conversation thread
    /// after the call ends so Ollama embedding doesn't compete with voice replies.
    /// Thread-safe: concurrent webhook calls may add messages simultaneously.
    /// </summary>
    public ConcurrentQueue<ConversationMessage> PendingMessages { get; } = new();
}
