namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Outage Perception Source support (Apr 27, 2026; backlog 15.19, motivating
/// case Apr 14-15 power outage). Records the success/failure state of each
/// perception source across cognitive cycles so the
/// <c>OutagePerceptionSource</c> can detect when the world has gone quiet.
///
/// Populated by <c>PerceptionPhase</c>: each source's PollAsync invocation
/// records success or failure with a timestamp. Read by
/// <c>OutagePerceptionSource</c> to compute "how many sources have been
/// failing continuously for ≥ N minutes."
///
/// In-memory only. State does not survive process restart — which means
/// the outage window resets on every redeploy. That is the correct
/// semantic: a fresh process has no inherited sense of "the world has been
/// quiet for hours" until it accumulates that observation itself.
///
/// Singleton lifetime. Single writer (PerceptionPhase on the cognitive-
/// cycle thread), single reader (the outage perception source on the same
/// thread), but implementations should lock on write/read to be defensive.
/// </summary>
public interface IPerceptionSourceHealthTracker
{
    /// <summary>
    /// Record a successful poll for the named source at the given moment.
    /// Idempotent if called multiple times with monotonically advancing times.
    /// </summary>
    void RecordSuccess(string sourceName, DateTimeOffset at);

    /// <summary>
    /// Record a failed poll for the named source at the given moment.
    /// </summary>
    void RecordFailure(string sourceName, DateTimeOffset at);

    /// <summary>
    /// True if the named source has at least one record (success or failure).
    /// Untracked sources are not counted as failing for outage detection —
    /// they may simply not have been polled yet.
    /// </summary>
    bool IsTracked(string sourceName);

    /// <summary>
    /// Source names that have been failing continuously since their last
    /// success (or since first record if no success has ever been observed)
    /// for at least <paramref name="minimumDuration"/> as of <paramref name="now"/>.
    /// Used by the outage source to count world-facing sources currently
    /// in failure.
    /// </summary>
    IReadOnlyList<string> GetSourcesFailingFor(TimeSpan minimumDuration, DateTimeOffset now);

    /// <summary>
    /// Source names that recorded a success within the most recent
    /// <paramref name="recoveryWindow"/> after a prior failure stretch.
    /// Used by the outage source to detect recovery events.
    /// </summary>
    IReadOnlyList<string> GetSourcesRecoveredWithin(TimeSpan recoveryWindow, DateTimeOffset now);
}
