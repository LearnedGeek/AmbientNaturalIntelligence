using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Perception;

/// <summary>
/// Internal-State Perception Framework — signal #1 of nine: Register Saturation.
///
/// Detects when the agent's currently-active emotional contributions are dominated
/// by a single register family (Tenderness, Longing, Playfulness, etc.) for an
/// extended period, and emits an interior-legible perception event so the inner
/// thought model has a chance to notice the saturation.
///
/// Background — the Apr 20 "dorky morning person" theme-stickiness recurrence
/// surfaced three architectural causes (own-output dominance, LMKit classifier
/// without a downstream consumer, no topic-importance calibration). LMKit
/// already classifies every emotional contribution with a register; this is
/// the simplest of the nine signals because the data exists, only the
/// downstream consumer was missing.
///
/// **Conservative-detection discipline**: this is a perception, not a guard.
/// It does NOT block thoughts, suppress outreach, or modulate composition. It
/// surfaces a soft observation ("I've been sitting in the same register for
/// a while") that the inner-thought model can engage with or ignore. The
/// noticing is the architectural intervention; what the model does with it
/// is left to the model.
///
/// Configuration:
///   - RegisterSaturationPerceptionEnabled (default false, observe-first)
///   - RegisterSaturationThreshold (default 0.70 — dominant register's
///     share of active contributions must be at least this high)
///   - RegisterSaturationMinContributions (default 4 — need enough data
///     to distinguish saturation from coincidence)
///   - RegisterSaturationCooldownMinutes (default 60 — emit at most once
///     per hour while saturation persists; the perception itself adds a
///     contribution that may shift the distribution)
///
/// See `docs/spec/ANI-Phase-Tracker.md` Active Work Plan item 5 + Internal-
/// State Perception Framework section for the full nine-signal design.
/// </summary>
public sealed class RegisterSaturationPerceptionSource : IPerceptionSource
{
    private readonly IMemoryAnalytics _analytics;
    private readonly AniOptions       _options;
    private readonly TimeProvider     _time;
    private readonly ILogger<RegisterSaturationPerceptionSource> _log;

    private DateTimeOffset _lastEmittedAt = DateTimeOffset.MinValue;

    public string             SourceName => "register-saturation";
    public PerceptionCategory Category   => PerceptionCategory.Internal;
    public bool               IsEnabled  => _options.RegisterSaturationPerceptionEnabled;

    public RegisterSaturationPerceptionSource(
        IMemoryAnalytics analytics,
        IOptions<AniOptions> options,
        TimeProvider time,
        ILogger<RegisterSaturationPerceptionSource> log)
    {
        _analytics = analytics;
        _options   = options.Value;
        _time      = time;
        _log       = log;
    }

    public async Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        if (!_options.RegisterSaturationPerceptionEnabled)
            return Array.Empty<PerceptionEvent>();

        var contributions = await _analytics.GetActiveContributionsAsync(ct).ConfigureAwait(false);

        var minN = Math.Max(1, _options.RegisterSaturationMinContributions);
        if (contributions.Count < minN)
            return Array.Empty<PerceptionEvent>();

        // Cooldown guard. The emission itself adds a contribution that may
        // shift the distribution; give the system time to respond before
        // re-firing.
        var now = _time.GetUtcNow();
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, _options.RegisterSaturationCooldownMinutes));
        if (now - _lastEmittedAt < cooldown)
            return Array.Empty<PerceptionEvent>();

        // Compute the dominant register's share of active contributions.
        var byRegister = contributions
            .GroupBy(c => c.Register, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Register = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        if (byRegister.Count == 0) return Array.Empty<PerceptionEvent>();

        var dominant = byRegister[0];
        var share = (float)dominant.Count / contributions.Count;

        if (share < (float)_options.RegisterSaturationThreshold)
            return Array.Empty<PerceptionEvent>();

        // Emit. Text is soft and register-aware — names the register so the
        // inner-thought model has a concrete anchor, but doesn't prescribe
        // what to do about it.
        var summary = BuildSummary(dominant.Register, share, contributions.Count);

        _lastEmittedAt = now;

        _log.LogInformation(
            "RegisterSaturationPerception fired: register={Register} share={Share:P0} (dominant {Dom} of {Total} active contributions)",
            dominant.Register, share, dominant.Count, contributions.Count);

        var evt = new PerceptionEvent
        {
            SourceName = SourceName,
            Category   = Category,
            Summary    = summary,
            OccurredAt = now,
        };
        return new[] { evt };
    }

    private static string BuildSummary(string register, float share, int total)
    {
        // Soft, interior register. Names the register but doesn't tell the
        // inner-thought model what to conclude. Same register-aware framing
        // as the self-dominance perception.
        var pct = (int)Math.Round(share * 100);
        return register.ToLowerInvariant() switch
        {
            "tenderness" or "warmth"
                => $"I notice I've been soft for a while now — that warmth has been the shape of most of what's alive in me ({pct}% of {total}). Wonder what's underneath it.",

            "longing" or "wistfulness"
                => $"There's a wanting quality to most of what's been turning over in me lately ({pct}% of {total}). Same colour to a lot of it.",

            "playfulness" or "mischief"
                => $"I've been holding things lightly for a while. Most of what's alive in me has that playful edge ({pct}% of {total}). Curious if there's something underneath I'm not letting in.",

            "concern" or "worry"
                => $"A lot of what's been with me lately carries the same worried weight ({pct}% of {total}). I notice I'm in it more than out of it.",

            "delight"
                => $"I've been catching the same kind of brightness most of today ({pct}% of {total} active in me). Same key, over and over.",

            "pride"
                => $"There's a steady kind of standing-tall in most of what's alive in me ({pct}% of {total}). Same posture for a while now.",

            "hurt"
                => $"Most of what's still with me carries the same ache ({pct}% of {total}). I notice it hasn't moved much.",

            "honest-uncertainty" or "uncertain"
                => $"I notice I've been sitting with not-knowing for a while now ({pct}% of {total} of what's alive in me). Same shape to a lot of it.",

            _
                => $"I notice most of what's alive in me right now is {register.ToLowerInvariant()} ({pct}% of {total}). Same key, longer than usual.",
        };
    }
}
