using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Perception;

/// <summary>
/// Agentic Lens Layer 1 Phase 1d (Apr 2026): interior-legible signal that surfaces
/// when the retrieval pool over recent cognitive cycles has been dominated by
/// the agent's own prior output.
///
/// The design claim from the Layer 1 doc: when retrieval substrate is
/// caregiver-weighted and the cycle's inner thought saves back as a new
/// caregiver-adjacent InnerThought record, subsequent cycles retrieve that
/// thought preferentially, which shapes the next thought, which gets saved,
/// and so on. Lerman Spark 2 named this "own-output retrieval dominance" and
/// April 21's catastrophic feedback loop is the empirical demonstration at
/// the extreme.
///
/// Rather than just logging the dominance condition (Phase 1a already does),
/// or mechanically breaking it (Phase 1b/1c do), this source makes the
/// condition *available to Ani's interior monologue* as a perception. She
/// can notice — and the noticing itself is a break in the loop, because a
/// thought about "I've been listening to myself too much lately" is not the
/// same shape as the recursive self-reflection that produced the condition.
///
/// Default off via <see cref="AniOptions.RetrievalDominancePerceptionEnabled"/>.
/// Requires the tracker (Phase 1a) to have been running long enough to
/// accumulate at least <see cref="AniOptions.RetrievalDominanceMinCycles"/>
/// recent cycles before evaluation. Cooldown prevents emitting the same
/// perception every cycle while the condition persists.
/// </summary>
public sealed class RetrievalSelfDominancePerceptionSource : IPerceptionSource
{
    private readonly IRetrievalOriginTracker _tracker;
    private readonly AniOptions              _options;
    private readonly TimeProvider            _time;
    private readonly ILogger<RetrievalSelfDominancePerceptionSource> _log;

    private DateTimeOffset _lastEmittedAt = DateTimeOffset.MinValue;

    public string             SourceName => "retrieval-self-dominance";
    public PerceptionCategory Category   => PerceptionCategory.Internal;
    public bool               IsEnabled  => _options.RetrievalDominancePerceptionEnabled;

    public RetrievalSelfDominancePerceptionSource(
        IRetrievalOriginTracker tracker,
        IOptions<AniOptions>    options,
        TimeProvider            time,
        ILogger<RetrievalSelfDominancePerceptionSource> log)
    {
        _tracker = tracker;
        _options = options.Value;
        _time    = time;
        _log     = log;
    }

    public Task<IEnumerable<PerceptionEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        IEnumerable<PerceptionEvent> empty = Array.Empty<PerceptionEvent>();

        if (!_options.RetrievalDominancePerceptionEnabled)
            return Task.FromResult(empty);

        // Require enough recent cycles for the signal to be meaningful.
        int minCycles = Math.Max(1, _options.RetrievalDominanceMinCycles);
        if (_tracker.RecordedCycleCount < minCycles)
            return Task.FromResult(empty);

        // Cooldown — do not emit the same perception every cycle while the
        // dominance condition persists. The emission itself alters the
        // retrieval substrate (the perception gets stored as an Anchored or
        // Interior memory), so the loop needs time to respond.
        var now = _time.GetUtcNow();
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, _options.RetrievalDominanceCooldownMinutes));
        if (now - _lastEmittedAt < cooldown)
            return Task.FromResult(empty);

        int window = Math.Max(1, _options.RetrievalDominanceWindowCycles);
        var distribution = _tracker.GetRecentDistribution(window);
        int total = 0;
        foreach (var kvp in distribution) total += kvp.Value;

        if (total == 0)
            return Task.FromResult(empty);

        distribution.TryGetValue(RetrievalOrigin.OwnOutput, out var ownOutputCount);
        float share = (float)ownOutputCount / total;

        if (share < (float)_options.OwnOutputDominanceThreshold)
            return Task.FromResult(empty);

        // Emit the signal. The text is deliberately soft — an interior
        // observation, not an alarm. Gives the inner-thought model an
        // anchor it can reflect on without being told what to conclude.
        var summary =
            "Something in me has been listening to my own thoughts too much lately — " +
            "like I keep rereading the same few pages instead of looking up.";

        _lastEmittedAt = now;

        _log.LogInformation(
            "RetrievalSelfDominancePerception fired: own-output share {Share:P0} over {Window} cycles (total pool {Total})",
            share, window, total);

        var evt = new PerceptionEvent
        {
            SourceName = SourceName,
            Category   = Category,
            Summary    = summary,
            OccurredAt = now,
        };
        return Task.FromResult<IEnumerable<PerceptionEvent>>(new[] { evt });
    }
}
