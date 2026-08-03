using AniRuntime.Core.Models;

namespace AniRuntime.Core.Interfaces;

/// <summary>
/// Feature 44 (Interoceptive Axis, Phase I.1, 2026-08-03) — updates the
/// interoceptive dimensions of <see cref="EmotionalState"/> from EXOGENOUS
/// drivers (time-of-day, cycles-since-rest, weather perception, contact-gap)
/// that are decoupled from thought content. Counterforce signal source to
/// the warm-mirror-echo attractor characterized in Issue #99.
///
/// **Called once per cognitive cycle** in Phase 0, after the emotional-
/// contribution compute step and after Feature 17's ContactGapTension
/// accumulation, immediately before <see cref="IMemoryPersistence.SaveEmotionalStateAsync"/>.
/// This service mutates the passed <see cref="EmotionalState"/> in place;
/// the caller persists.
///
/// **Fail-open discipline.** When <see cref="AniOptions.InteroceptiveAxisEnabled"/>
/// is false, this service does nothing. Interoceptive dimensions retain
/// their previous values (or defaults on first run). No downstream consumer
/// should crash when the fields are at defaults.
///
/// **Universal drivers only (Phase I.1).** Time-of-day, cycle-count,
/// weather perception, contact-gap. Canonical-seed character modulation is
/// deferred to a later phase per Mark's 2026-08-03 correction on
/// bookstore-over-weighting; introducing character-specific drivers before
/// any composer consumes the state adds risk with no payoff.
/// </summary>
public interface IInteroceptiveStateService
{
    /// <summary>
    /// Compute new interoceptive-axis values from exogenous drivers and
    /// mutate <paramref name="state"/> in place.
    /// </summary>
    /// <param name="state">The current emotional state. Fields updated:
    /// <see cref="EmotionalState.Tiredness"/>, <see cref="EmotionalState.Restlessness"/>,
    /// <see cref="EmotionalState.Groundedness"/>, <see cref="EmotionalState.AmbientBodySense"/>,
    /// <see cref="EmotionalState.LastInteroceptiveUpdate"/>.</param>
    /// <param name="context">Exogenous inputs for driver calculation.</param>
    void Update(EmotionalState state, InteroceptiveDriverContext context);
}

/// <summary>
/// Driver context — all exogenous inputs the interoceptive-state service
/// needs, gathered by the cycle pipeline caller.
/// </summary>
/// <param name="Now">Current wall-clock time (local). Used for circadian
/// modulation on Tiredness and AmbientBodySense.</param>
/// <param name="HoursSinceLastOutreach">Time since Ani's last dispatched
/// outreach or reply. Drives Restlessness onset.</param>
/// <param name="HoursSinceLastInboundContact">Time since Mark's last
/// inbound message. Contributes to Restlessness (the interlocutor-gap
/// component). Zero when no contact ever recorded.</param>
/// <param name="RecentPerceptionEventCount">Count of new perception events
/// across the last N cycles (per
/// <see cref="AniOptions.InteroceptiveGroundednessRecentEventWindow"/>).
/// Drives inverse-of-change Groundedness.</param>
/// <param name="CurrentTemperatureFahrenheit">Latest weather perception
/// temperature. When outside hot/cold thresholds, contributes to
/// AmbientBodySense. Null if no recent weather perception.</param>
public sealed record InteroceptiveDriverContext(
    DateTimeOffset Now,
    double         HoursSinceLastOutreach,
    double         HoursSinceLastInboundContact,
    int            RecentPerceptionEventCount,
    double?        CurrentTemperatureFahrenheit);
