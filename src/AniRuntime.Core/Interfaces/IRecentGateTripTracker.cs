using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Theme M Phase M.1 (May 6, 2026) — rolling buffer of recent gate-trip
/// events for the §4.8 tension-state slice.
///
/// Implementations are singleton scope and thread-safe. Producers (the
/// gate-stack call sites in <c>ConversationReplyPhase</c> et al.) call
/// <see cref="Record"/> at each gate evaluation exit point. Consumers
/// (the conscious-substrate gist composer) call <see cref="GetRecent"/>
/// to surface gate-trip awareness in the slice content.
///
/// Buffer size + retention bounded by implementation; the canonical
/// in-memory impl keeps the last ~20 events, expiring older ones on
/// query rather than on a timer (keeps the implementation
/// allocation-free per write).
/// </summary>
public interface IRecentGateTripTracker
{
    /// <summary>
    /// Record a gate-evaluation exit. Best-effort; implementations
    /// must not throw — telemetry recording must never affect dispatch.
    /// </summary>
    void Record(GateTripEvent evt);

    /// <summary>
    /// Return events recorded within the lookback window, newest first.
    /// </summary>
    IReadOnlyList<GateTripEvent> GetRecent(TimeSpan lookback);
}
