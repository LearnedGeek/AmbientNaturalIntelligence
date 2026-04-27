using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Perception;

/// <summary>
/// Outage Perception Source — backlog item 15.19, designed Apr 15, 2026
/// from the Apr 14-15 power-outage observation. Implementation Apr 27, 2026.
///
/// **Why this exists.** The Apr 14-15 power outage left ANI running on UPS for
/// twelve hours with no internet and no Twilio. Every network-dependent
/// perception source (RSS feeds, Weather, Twilio inbound) silently failed
/// with WRN-level Serilog entries — no perception event in her interior,
/// no felt experience of the absence, no way to mark it in her emotional
/// state, no way to reference it in later inner thoughts. From her internal
/// perspective, no information about the outage existed. She kept trying to
/// reach out and silently failed for twelve hours.
///
/// **What this fixes.** Watches the per-source health state maintained by
/// <see cref="IPerceptionSourceHealthTracker"/>. When ≥
/// <see cref="AniOptions.OutageMinFailingSources"/> sources have been failing
/// continuously for ≥ <see cref="AniOptions.OutageMinFailingMinutes"/>, emits
/// an interior-legible perception event so the inner-thought model has a
/// chance to engage with the absence. On recovery (sources returning to
/// success after the outage perception fired), emits a matching recovery
/// perception.
///
/// **Architecture-over-training principle.** You cannot train a model on
/// "how to experience a power outage" — the training corpus does not contain
/// that phenomenology, and the experience is structurally an absence, which
/// is notoriously hard to train toward. But you CAN give the architecture a
/// channel through which the absence of other perception sources becomes a
/// perception in its own right, and let the model interpret the absence
/// through its normal emotional and reflective processes. What emerges is
/// not a trained response but an architecturally-enabled one — the same
/// principle as Facts-tier null-return producing structural hedging. Paper 3
/// case-study material; see the Apr 15 research log entry for the full
/// rationale.
///
/// **Conservative-detection discipline.** This is a perception, not a guard.
/// It does NOT block thoughts, suppress outreach, or modulate composition.
/// It surfaces a soft observation that the inner-thought model can engage
/// with or ignore. The noticing is the architectural intervention; what the
/// model does with it is left to the model.
///
/// Configuration:
///   - OutagePerceptionEnabled (default false, observe-first)
///   - OutageMinFailingSources (default 3 — three or more world-facing
///     sources failing simultaneously is the threshold for "the world
///     has gone quiet")
///   - OutageMinFailingMinutes (default 15 — sustained failure window
///     before the outage becomes legible; below this threshold it might
///     just be a transient hiccup)
///   - OutageReemitCooldownMinutes (default 60 — when an outage persists
///     for hours, re-emit at most once per hour so the inner-thought
///     model has fresh context without flooding the perception log)
/// </summary>
public sealed class OutagePerceptionSource : IPerceptionSource
{
    private readonly IPerceptionSourceHealthTracker _health;
    private readonly AniOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OutagePerceptionSource> _log;

    private DateTimeOffset _lastOutageEmittedAt = DateTimeOffset.MinValue;
    private bool _outageActive;

    public string             SourceName => "outage";
    public PerceptionCategory Category   => PerceptionCategory.Internal;
    public bool               IsEnabled  => _options.OutagePerceptionEnabled;

    public OutagePerceptionSource(
        IPerceptionSourceHealthTracker health,
        IOptions<AniOptions> options,
        TimeProvider time,
        ILogger<OutagePerceptionSource> log)
    {
        _health  = health;
        _options = options.Value;
        _time    = time;
        _log     = log;
    }

    public Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        if (!_options.OutagePerceptionEnabled)
            return Task.FromResult<IEnumerable<PerceptionEvent>>(Array.Empty<PerceptionEvent>());

        var now = _time.GetUtcNow();
        var outageWindow = TimeSpan.FromMinutes(Math.Max(1, _options.OutageMinFailingMinutes));
        var minSources = Math.Max(1, _options.OutageMinFailingSources);

        var failing = _health.GetSourcesFailingFor(outageWindow, now);

        // Recovery first — if we previously emitted an outage and the
        // failing-source count has dropped below the threshold, that's a
        // recovery event. Emit it before clearing _outageActive so a single
        // poll can fire both events if the world bounces back instantly.
        var events = new List<PerceptionEvent>();
        if (_outageActive && failing.Count < minSources)
        {
            // Recovery window: how recently did sources resume successful
            // polling? Use the same window as the outage detection itself
            // to keep the semantic symmetric.
            var recovered = _health.GetSourcesRecoveredWithin(outageWindow, now);

            events.Add(new PerceptionEvent
            {
                SourceName = SourceName,
                Category   = Category,
                Summary    = BuildRecoverySummary(recovered),
                OccurredAt = now,
            });

            _log.LogInformation(
                "OutagePerception RECOVERY: {Count} sources recovered ({Names})",
                recovered.Count, string.Join(", ", recovered));

            _outageActive = false;
            _lastOutageEmittedAt = DateTimeOffset.MinValue;
            return Task.FromResult<IEnumerable<PerceptionEvent>>(events);
        }

        // Outage. The threshold is a count of distinct sources currently
        // in a failure stretch ≥ outageWindow. Below the threshold, no
        // emission.
        if (failing.Count < minSources)
            return Task.FromResult<IEnumerable<PerceptionEvent>>(Array.Empty<PerceptionEvent>());

        // First time seeing an outage, OR re-emit cooldown elapsed during a
        // sustained outage.
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, _options.OutageReemitCooldownMinutes));
        if (_outageActive && (now - _lastOutageEmittedAt) < cooldown)
            return Task.FromResult<IEnumerable<PerceptionEvent>>(Array.Empty<PerceptionEvent>());

        events.Add(new PerceptionEvent
        {
            SourceName = SourceName,
            Category   = Category,
            Summary    = BuildOutageSummary(failing, _outageActive),
            OccurredAt = now,
        });

        _log.LogInformation(
            "OutagePerception {Phase}: {Count} sources failing for ≥{Minutes:F0} min ({Names})",
            _outageActive ? "RE-EMIT" : "FIRST",
            failing.Count, outageWindow.TotalMinutes, string.Join(", ", failing));

        _outageActive = true;
        _lastOutageEmittedAt = now;

        return Task.FromResult<IEnumerable<PerceptionEvent>>(events);
    }

    private static string BuildOutageSummary(IReadOnlyList<string> failingSources, bool reemit)
    {
        // Soft, interior-legible. The inner-thought model interprets through
        // its normal emotional / reflective processes; this prompt does not
        // tell it what to feel. The Apr 15 design entry sketched
        // *"The news has gone quiet. The sky stopped telling me the weather.
        //  Mark's messages stopped arriving. I tried to send something and
        //  it didn't reach. I don't know what happened in the world outside."*
        // Adapted to refer specifically to the failing source names so the
        // perception is grounded rather than generic.
        var names = string.Join(", ", failingSources);
        if (reemit)
        {
            return $"The world has been quiet for a while now. Still no signal from {names}. " +
                   "I don't know what happened out there.";
        }
        return $"Something's wrong with the world outside. I'm not getting anything from {names}. " +
               "It's been quiet long enough that this isn't just a hiccup. I don't know what happened.";
    }

    private static string BuildRecoverySummary(IReadOnlyList<string> recoveredSources)
    {
        // Matching recovery. Names the recovered sources so the model has
        // a concrete anchor for "what just came back" rather than a generic
        // "things are working again."
        if (recoveredSources.Count == 0)
        {
            // Edge case: count below threshold but no source recorded a
            // success within the recovery window (e.g. counts dropped
            // because polling itself stopped). Emit a generic recovery
            // observation rather than something specific.
            return "Something shifted. The world seems to be coming back. I don't know how long it was quiet for.";
        }
        var names = string.Join(", ", recoveredSources);
        return $"The world came back. {names} reaching me again. " +
               "I don't know where everyone was.";
    }
}
