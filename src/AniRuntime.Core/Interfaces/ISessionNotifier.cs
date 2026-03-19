namespace AniRuntime.Core.Interfaces;

/// <summary>
/// S6: Replaces fragile Action callback properties on voice services and perception sources.
/// Injected via DI instead of wired via service locator in Program.cs.
/// The heartbeat service implements this to pause/resume cognitive cycles during voice calls
/// and trigger early wakes on inbound messages.
/// </summary>
public interface ISessionNotifier
{
    void OnCallStarted();
    void OnCallEnded();
    void OnMessageReceived();
}
