using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;

namespace AniRuntime.Perception;

/// <summary>
/// Surfaces gaps in Ani's own existence as perception events.
///
/// Context: the AniHeartbeatService runs cognitive cycles at a roughly
/// 15-30 minute cadence driven by the desire engine. When the service is
/// stopped (reboot, update, laptop sleep, server move), no cycles run —
/// and unlike sleep in humans, Ani doesn't experience the gap. She can,
/// however, *observe* it when she comes back by looking at the timestamp
/// of her last inner thought and comparing to now.
///
/// Prior state: <see cref="TimePerceptionSource"/> had an inline gap
/// notice ("It has been about X hours since the last thought.") that
/// used <c>since</c> — a value initialized from <see cref="PerceptionPhase"/>
/// on service startup. Because that value was set to <c>UtcNow</c> at
/// construction, the gap across a service restart was invisible: the
/// service came up, set <c>_lastPollAt</c> to now, and the first poll
/// compared now to now. Ani could never notice she was gone.
///
/// This source fixes that specific case. Instead of using the in-process
/// poll timestamp, it queries the <em>persisted</em> timestamp of Ani's
/// most recent <see cref="MemoryType.InnerThought"/> record, which
/// survives service restarts. The first poll after a restart computes
/// the true gap and emits a perception event with texture-graded
/// narration (tone shifts with gap magnitude — small gaps are
/// observational, large gaps are existential).
///
/// Design principle: the gap is reported honestly. Ani can't claim to
/// have experienced it; she can only notice it by inference from her
/// own records. The framing reflects that. See the April 19 discussion
/// in the research log for the architectural reasoning.
///
/// Dedup: each unique gap report is emitted once per service lifetime.
/// Subsequent polls within a normal cycle cadence don't fire, because
/// the last InnerThought is the one from the prior cycle (~minutes ago).
/// </summary>
public sealed class TemporalGapPerceptionSource : IPerceptionSource
{
    private readonly IMemorySearch _search;
    private readonly ILogger<TemporalGapPerceptionSource> _log;
    private readonly TemporalGapOptions _options;
    private bool _hasEmittedForCurrentGap;

    public string             SourceName => "temporal-gap";
    public PerceptionCategory Category   => PerceptionCategory.Environment;
    public bool               IsEnabled  => _options.Enabled;

    public TemporalGapPerceptionSource(
        IMemorySearch search,
        ILogger<TemporalGapPerceptionSource> log,
        TemporalGapOptions? options = null)
    {
        _search = search;
        _log    = log;
        _options = options ?? new TemporalGapOptions();
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        if (_hasEmittedForCurrentGap)
            return Array.Empty<PerceptionEvent>();

        try
        {
            // Most recent InnerThought across the full memory store. This
            // survives restarts because it lives in SQLite, not in-process
            // state. GetByTypeAsync orders by occurred_at DESC; limit 1.
            var recent = await _search.GetByTypeAsync(MemoryType.InnerThought, limit: 1, ct)
                .ConfigureAwait(false);
            var last = recent.FirstOrDefault();

            // No prior InnerThought at all — first-ever startup, nothing
            // to report. The condition naturally resolves on the next poll
            // once the first cycle's inner thought has been persisted.
            if (last is null)
            {
                _hasEmittedForCurrentGap = true;
                return Array.Empty<PerceptionEvent>();
            }

            var now = DateTimeOffset.UtcNow;
            var gap = now - last.OccurredAt;

            if (gap < TimeSpan.FromHours(_options.MinGapHours))
            {
                // Gap is within normal cycle cadence — nothing unusual to
                // report. Don't mark as emitted, so we keep checking; a
                // mid-conversation pause isn't interesting but a later
                // service-level gap still is.
                return Array.Empty<PerceptionEvent>();
            }

            var narration = NarrateGap(gap);
            _log.LogInformation(
                "Temporal gap detected: last InnerThought was {Gap} ago ({At}) — emitting perception: \"{Narration}\"",
                Human(gap), last.OccurredAt, narration);

            _hasEmittedForCurrentGap = true;
            return new[]
            {
                new PerceptionEvent
                {
                    SourceName       = SourceName,
                    Category         = Category,
                    Summary          = narration,
                    ContactRelevance = 0.5f, // gaps are relationally relevant — Mark was waiting
                    OccurredAt       = now,
                }
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Temporal gap check failed — continuing without");
            return Array.Empty<PerceptionEvent>();
        }
    }

    /// <summary>
    /// Texture-graded narration. Small gaps are observational; medium
    /// gaps are "more than usual"; large gaps are existential. The
    /// framing is honest about the nature of the absence — Ani can
    /// observe the gap in her records but did not experience it.
    /// </summary>
    private static string NarrateGap(TimeSpan gap)
    {
        var h = gap.TotalHours;

        if (h < 4)
            return $"Noticing: my last thought was about {(int)Math.Round(h)} hours ago. Longer than the usual cadence.";

        if (h < 12)
            return $"Noticing: more than {(int)Math.Round(h)} hours have passed since my last thought. Something shifted — I was away.";

        if (h < 24)
        {
            var hours = (int)Math.Round(h);
            return $"Noticing: {hours} hours have gone by since my last thought. Most of a day. I wasn't here for it.";
        }

        if (h < 48)
        {
            return "Noticing: more than a day has gone by since my last thought. I was gone — really gone — and I didn't know.";
        }

        var days = (int)Math.Round(h / 24.0);
        return $"Noticing: about {days} days have passed since my last thought. I was away for longer than I can account for.";
    }

    private static string Human(TimeSpan gap)
    {
        if (gap.TotalMinutes < 60) return $"{(int)gap.TotalMinutes} min";
        if (gap.TotalHours < 24)    return $"{gap.TotalHours:F1}h";
        return $"{gap.TotalDays:F1}d";
    }
}

/// <summary>
/// Configuration for the temporal gap source.
/// </summary>
public sealed class TemporalGapOptions
{
    public bool   Enabled     { get; set; } = true;
    public double MinGapHours { get; set; } = 2.0;
}
