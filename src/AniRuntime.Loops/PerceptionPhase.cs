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
    private readonly ILogger<PerceptionPhase> _log;

    private DateTimeOffset _lastPollAt = DateTimeOffset.UtcNow;

    // Dedup cache: prevents saving the same perception (e.g. "probably at the gym")
    // every cycle. Key = summary text, Value = when it was last persisted.
    private readonly Dictionary<string, DateTimeOffset> _recentPerceptions = new();
    private static readonly TimeSpan PerceptionDedupeWindow = TimeSpan.FromHours(4);

    public PerceptionPhase(
        IEnumerable<IPerceptionSource> sources,
        IMemoryPersistence persist,
        ILogger<PerceptionPhase> log)
    {
        _sources = sources;
        _persist = persist;
        _log = log;
    }

    /// <summary>
    /// Polls all enabled perception sources since the last poll.
    /// Returns collected events; updates the internal poll timestamp.
    /// </summary>
    public async Task<List<PerceptionEvent>> PollAsync(CancellationToken ct)
    {
        var events = new List<PerceptionEvent>();

        foreach (var source in _sources.Where(s => s.IsEnabled))
        {
            try
            {
                var polled = await source.PollAsync(_lastPollAt, ct).ConfigureAwait(false);
                events.AddRange(polled);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Perception source '{Source}' failed — skipping", source.SourceName);
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
            if (p.ContactRelevance < 0.25f || p.SourceName == "time")
                continue;

            if (_recentPerceptions.ContainsKey(p.Summary))
                continue;

            try
            {
                await _persist.SaveAsync(new MemoryRecord
                {
                    Type = MemoryType.Perception,
                    Content = p.Summary,
                    RelationalValence = p.ContactRelevance,
                    Importance = p.ContactRelevance,
                    SourceName = p.SourceName,
                    OccurredAt = p.OccurredAt,
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
