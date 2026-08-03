using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Feature 44 (Interoceptive Axis, Phase I.1, 2026-08-03) — production
/// implementation of <see cref="IInteroceptiveStateService"/>.
///
/// Driver design per plan §2 Phase I.1 + Mark's 2026-08-03 correction on
/// bookstore-over-weighting: **universal drivers only in I.1**. Time-of-day
/// / cycles-since-rest / weather perception / contact-gap. NO canonical-
/// seed character-specific modulation (introduced only if a later phase
/// empirically needs it).
///
/// Fail-open: when <see cref="AniOptions.InteroceptiveAxisEnabled"/> is
/// false, <see cref="Update"/> returns immediately without touching state.
/// The passed state's interoceptive fields retain their defaults.
/// </summary>
public sealed class InteroceptiveStateService : IInteroceptiveStateService
{
    private readonly AniOptions                          _options;
    private readonly ILogger<InteroceptiveStateService>  _log;

    public InteroceptiveStateService(
        IOptions<AniOptions>                aniOptions,
        ILogger<InteroceptiveStateService>  log)
    {
        _options = aniOptions?.Value ?? throw new ArgumentNullException(nameof(aniOptions));
        _log     = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Update(EmotionalState state, InteroceptiveDriverContext context)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (!_options.InteroceptiveAxisEnabled)
        {
            // Flag-off no-op — fields retain their prior values / defaults.
            return;
        }

        var previousTiredness    = state.Tiredness;
        var previousRestlessness = state.Restlessness;
        var previousGroundedness = state.Groundedness;
        var previousAmbient      = state.AmbientBodySense;

        state.Tiredness         = ComputeTiredness(context);
        state.Restlessness      = ComputeRestlessness(context);
        state.Groundedness      = ComputeGroundedness(context);
        state.AmbientBodySense  = ComputeAmbientBodySense(context);
        state.LastInteroceptiveUpdate = context.Now;

        _log.LogDebug(
            "INTEROCEPTIVE_STATE tiredness={Tiredness:F2} (Δ {DTired:+0.00;-0.00;0.00}) " +
            "restlessness={Restless:F2} (Δ {DRest:+0.00;-0.00;0.00}) " +
            "groundedness={Ground:F2} (Δ {DGround:+0.00;-0.00;0.00}) " +
            "ambient={Ambient:F2} (Δ {DAmb:+0.00;-0.00;0.00})",
            state.Tiredness, state.Tiredness - previousTiredness,
            state.Restlessness, state.Restlessness - previousRestlessness,
            state.Groundedness, state.Groundedness - previousGroundedness,
            state.AmbientBodySense, state.AmbientBodySense - previousAmbient);
    }

    /// <summary>
    /// Tiredness = cycle-accumulation + circadian modulation.
    /// Base: hours-since-last-outreach mapped via 1 - exp(-t / halfLife).
    /// Late-night hours (23:00-06:00) add a boost. Clamped [0, 1].
    /// </summary>
    internal float ComputeTiredness(InteroceptiveDriverContext context)
    {
        var halfLife = Math.Max(0.5, _options.InteroceptiveTirednessCycleHalfLife);
        // Accumulation curve: 0 at t=0, ~0.5 at halfLife, ~0.75 at 2×halfLife,
        // asymptotes to 1.0. Represents cognitive fatigue building up during
        // quiet stretches without interaction.
        var accumulation = 1.0 - Math.Exp(-context.HoursSinceLastOutreach / halfLife);

        // Circadian: add boost during late-night hours (23:00-06:00 local).
        var hour = context.Now.Hour;
        var circadianBoost = (hour >= 23 || hour < 6)
            ? _options.InteroceptiveCircadianTirednessNightBoost
            : 0.0;

        var raw = accumulation + circadianBoost;
        return (float)Math.Clamp(raw, 0.0, 1.0);
    }

    /// <summary>
    /// Restlessness = time-since-outreach past onset + interlocutor-gap
    /// contribution. Different signal shape from Tiredness — accumulates
    /// LINEARLY past an onset threshold (action-drive), where Tiredness
    /// accumulates ASYMPTOTICALLY (fatigue). Same input, different mapping.
    /// </summary>
    internal float ComputeRestlessness(InteroceptiveDriverContext context)
    {
        var onset = Math.Max(0.0, _options.InteroceptiveRestlessnessQuietOnsetHours);
        var rate  = Math.Max(0.0, _options.InteroceptiveRestlessnessRatePerHour);
        var cap   = Math.Clamp(_options.InteroceptiveRestlessnessMax, 0.0, 1.0);

        // Self-quiet component — how long since Ani did anything.
        var selfQuietPastOnset = Math.Max(0.0, context.HoursSinceLastOutreach - onset);
        var selfQuietContribution = selfQuietPastOnset * rate;

        // Interlocutor-gap component — how long since Mark said anything.
        // Weighted less than self-quiet; the restlessness she feels for her
        // own action-drive is more direct than the restlessness she feels
        // waiting on him. Zero when no contact ever recorded (context uses 0.0).
        var interlocutorPastOnset = Math.Max(0.0, context.HoursSinceLastInboundContact - onset);
        var interlocutorContribution = interlocutorPastOnset * rate * 0.5;

        var raw = selfQuietContribution + interlocutorContribution;
        return (float)Math.Clamp(raw, 0.0, cap);
    }

    /// <summary>
    /// Groundedness = baseline − per-recent-event penalty. Inverse of ambient
    /// perception intensity. When lots of new perception events are firing
    /// (RSS updates, weather changes, contact-state changes), groundedness
    /// drops; when it's quiet, groundedness settles toward baseline.
    /// </summary>
    internal float ComputeGroundedness(InteroceptiveDriverContext context)
    {
        var baseline = Math.Clamp(_options.InteroceptiveGroundednessBaseline, 0.0, 1.0);
        var penalty  = Math.Max(0.0, _options.InteroceptiveGroundednessChangePenalty);
        var eventCount = Math.Max(0, context.RecentPerceptionEventCount);

        var raw = baseline - (eventCount * penalty);
        return (float)Math.Clamp(raw, 0.0, 1.0);
    }

    /// <summary>
    /// AmbientBodySense = weather-driven + circadian. Hot/cold weather adds
    /// discomfort; late-night hours add heavy-body sense. Neither directly
    /// correlates with thought content — pure exogenous body-sense proxy.
    /// </summary>
    internal float ComputeAmbientBodySense(InteroceptiveDriverContext context)
    {
        var raw = 0.0;

        // Weather contribution — hot OR cold outside thresholds adds discomfort.
        if (context.CurrentTemperatureFahrenheit.HasValue)
        {
            var temp = context.CurrentTemperatureFahrenheit.Value;
            var hotThreshold  = _options.InteroceptiveAmbientHotThresholdF;
            var coldThreshold = _options.InteroceptiveAmbientColdThresholdF;
            var penalty       = _options.InteroceptiveAmbientWeatherPenalty;

            if (temp > hotThreshold) raw += penalty;
            else if (temp < coldThreshold) raw += penalty;
        }

        // Circadian — late-night hours (23:00-06:00) add heavy-body sense.
        var hour = context.Now.Hour;
        if (hour >= 23 || hour < 6)
            raw += _options.InteroceptiveAmbientLateNightBoost;

        return (float)Math.Clamp(raw, 0.0, 1.0);
    }
}
