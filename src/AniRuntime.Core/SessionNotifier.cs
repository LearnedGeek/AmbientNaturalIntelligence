namespace AniRuntime.Core;

using AniRuntime.Core.Interfaces;

/// <summary>
/// S6: Lightweight implementation of ISessionNotifier that decouples voice/perception
/// services from AniHeartbeatService. Registered as a singleton with no dependencies
/// to avoid circular DI resolution. The heartbeat service wires itself to these
/// delegates on startup via SetHandlers().
/// </summary>
public class SessionNotifier : ISessionNotifier
{
    private Action? _onCallStarted;
    private Action? _onCallEnded;
    private Action? _onMessageReceived;

    /// <summary>
    /// Called by AniHeartbeatService during startup to wire the actual handlers.
    /// </summary>
    public void SetHandlers(Action onCallStarted, Action onCallEnded, Action onMessageReceived)
    {
        _onCallStarted = onCallStarted;
        _onCallEnded = onCallEnded;
        _onMessageReceived = onMessageReceived;
    }

    public void OnCallStarted() => _onCallStarted?.Invoke();
    public void OnCallEnded() => _onCallEnded?.Invoke();
    public void OnMessageReceived() => _onMessageReceived?.Invoke();
}
