using AniRuntime.Core.Interfaces;

namespace AniRuntime.Voice.Abstractions;

/// <summary>
/// In-memory store of active voice call sessions. Singleton — the dictionary
/// is the canonical "which calls are in flight" state, and must be shared
/// across all transient consumers of the voice pipeline.
///
/// <para>
/// Also surfaces the <see cref="ISessionNotifier"/> Start/End hooks so the
/// orchestrator does not need to depend on the notifier directly — call
/// counting and notification live in one place.
/// </para>
/// </summary>
public interface IVoiceSessionStore
{
    bool TryGet(string callSid, out VoiceCallSession? session);

    /// <summary>Register a new session. Fires <see cref="ISessionNotifier.OnCallStarted"/>.</summary>
    void Add(string callSid, VoiceCallSession session);

    /// <summary>
    /// Remove a session. Fires <see cref="ISessionNotifier.OnCallEnded"/> iff
    /// the removal drops the active-call count to zero. Returns the removed
    /// session (or <c>null</c> if no match).
    /// </summary>
    VoiceCallSession? Remove(string callSid);
}
