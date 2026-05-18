using System.Collections.Concurrent;
using AniRuntime.Core.Interfaces;
using AniRuntime.Voice.Abstractions;

namespace AniRuntime.Voice.Internal;

/// <summary>
/// Singleton concurrent dictionary keyed by Twilio <c>CallSid</c>. Owns the
/// "is any voice call currently active?" signal that the cognitive cycle
/// listens to via <see cref="ISessionNotifier"/> — voice playback is paused
/// during a call so the LLM has uncontested inference time.
/// </summary>
public sealed class VoiceSessionStore : IVoiceSessionStore
{
    private readonly ConcurrentDictionary<string, VoiceCallSession> _sessions = new();
    private readonly ISessionNotifier _notifier;

    public VoiceSessionStore(ISessionNotifier notifier)
    {
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    public bool TryGet(string callSid, out VoiceCallSession? session)
    {
        var found = _sessions.TryGetValue(callSid, out var s);
        session = s;
        return found;
    }

    public void Add(string callSid, VoiceCallSession session)
    {
        _sessions[callSid] = session;
        _notifier.OnCallStarted();
    }

    public VoiceCallSession? Remove(string callSid)
    {
        if (!_sessions.TryRemove(callSid, out var session))
            return null;

        if (_sessions.IsEmpty)
            _notifier.OnCallEnded();

        return session;
    }
}
