using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Loops;

/// <summary>
/// Polls all enabled perception sources and persists notable perceptions
/// as memory records with embeddings for future semantic search.
///
/// Extracted from CognitiveCycleProcessor (SRP) — perception polling
/// and deduplication are a distinct responsibility from cycle orchestration.
/// </summary>
public class PerceptionPhase
{
    private readonly IEnumerable<IPerceptionSource> _sources;
    private readonly IMemoryPersistence _persist;
    private readonly IPerceptionSourceHealthTracker? _health;
    private readonly ILogger<PerceptionPhase> _log;

    private DateTimeOffset _lastPollAt = DateTimeOffset.UtcNow;

    // Dedup cache: prevents saving the same perception (e.g. "probably at the gym")
    // every cycle. Key = summary text, Value = when it was last persisted.
    private readonly Dictionary<string, DateTimeOffset> _recentPerceptions = new();
    private static readonly TimeSpan PerceptionDedupeWindow = TimeSpan.FromHours(4);

    public PerceptionPhase(
        IEnumerable<IPerceptionSource> sources,
        IMemoryPersistence persist,
        ILogger<PerceptionPhase> log,
        IPerceptionSourceHealthTracker? health = null)
    {
        _sources = sources;
        _persist = persist;
        _health  = health;
        _log = log;
    }

    /// <summary>
    /// Polls all enabled perception sources since the last poll.
    /// Returns collected events; updates the internal poll timestamp.
    ///
    /// Per-source success/failure is recorded in <see cref="IPerceptionSourceHealthTracker"/>
    /// when one is registered. <see cref="OutagePerceptionSource"/> consumes
    /// that state to detect when the world has gone quiet (Apr 15 / Apr 27).
    /// The Outage source itself is excluded from health recording so it can't
    /// fire on its own success/failure history.
    /// </summary>
    public async Task<List<PerceptionEvent>> PollAsync(CancellationToken ct)
    {
        var events = new List<PerceptionEvent>();

        foreach (var source in _sources.Where(s => s.IsEnabled))
        {
            var pollAt = DateTimeOffset.UtcNow;
            try
            {
                var polled = await source.PollAsync(_lastPollAt, ct).ConfigureAwait(false);
                events.AddRange(polled);
                if (_health is not null && source.SourceName != "outage")
                    _health.RecordSuccess(source.SourceName, pollAt);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Perception source '{Source}' failed — skipping", source.SourceName);
                if (_health is not null && source.SourceName != "outage")
                    _health.RecordFailure(source.SourceName, pollAt);
            }
        }

        _lastPollAt = DateTimeOffset.UtcNow;
        return events;
    }

    /// <summary>
    /// Saves notable perceptions as memory records so they get embedded and become
    /// findable via semantic search in future cycles. Deduplicates within a 4-hour window.
    /// </summary>
    public async Task PersistNotableAsync(List<PerceptionEvent> perceptions, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Evict stale entries from the dedup cache
        var stale = _recentPerceptions
            .Where(kv => now - kv.Value > PerceptionDedupeWindow)
            .Select(kv => kv.Key).ToList();
        foreach (var key in stale) _recentPerceptions.Remove(key);

        foreach (var p in perceptions)
        {
            // Skip low-relevance perceptions, time (always emitted), and contact-state
            // guesses ("Mark is probably [activity]"). Contact-state is useful as cycle
            // context but shouldn't be persisted — generic guesses about routines match
            // too many queries and poison retrieval (14/17 retrievals on "Mark is probably Flexible").
            if (p.ContactRelevance < 0.25f || p.SourceName is "time" or "contact-state")
                continue;

            if (_recentPerceptions.ContainsKey(p.Summary))
                continue;

            try
            {
                // F-2 Phase 1 P6 (2026-08-22) — perception attribution by
                // source: twilio-inbound → Mark (his SMS), else → World
                // (RSS/weather/environmental events with no utterer).
                // Descriptor uses OriginChannelId (Twilio message SID) when
                // present so downstream audit can trace to the exact inbound;
                // falls back to OccurredAt for perception sources that don't
                // set a channel id.
                var perceptionAttribution = p.SourceName == "twilio-inbound"
                    ? AttributionTriple.MarkAt(p.OccurredAt, $"twilio-inbound:{p.OriginChannelId ?? p.OccurredAt.ToString("O")}")
                    : AttributionTriple.WorldAt(p.OccurredAt, $"{p.SourceName}:{p.OccurredAt:O}");
                await _persist.SaveAsync(new MemoryRecord
                {
                    Type = MemoryType.Perception,
                    Content = p.Summary,
                    RelationalValence = p.ContactRelevance,
                    Importance = p.ContactRelevance,
                    SourceName = p.SourceName,
                    OccurredAt = p.OccurredAt,
                    // Epistemic Grounding (Apr 10): Perception events are observations
                    // of the external world — always Facts tier.
                    Provenance = EpistemicTier.Facts,
                    AttributedTo               = perceptionAttribution.AttributedTo,
                    AttributedAt               = perceptionAttribution.AttributedAt,
                    AttributedSourceRecordId   = perceptionAttribution.SourceRecordId,
                    AttributedSourceDescriptor = perceptionAttribution.SourceDescriptor,
                    AttributionTrust           = perceptionAttribution.Trust,
                }, ct).ConfigureAwait(false);

                _recentPerceptions[p.Summary] = now;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to persist perception from {Source}", p.SourceName);
            }
        }
    }
}
