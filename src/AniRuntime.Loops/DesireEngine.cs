using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Loops;

/// <summary>
/// Manages the DesireState lifecycle: temporal drift, circadian modifiers, trigger weights,
/// and the ComputeNextWakeTime function that drives the cognitive cycle schedule.
///
/// IMPORTANT: All DesireState writes go through this class.
/// CognitiveCycleProcessor must never call IMemoryService.SaveDesireStateAsync() directly.
/// </summary>
public class DesireEngine
{
    private readonly IMemoryService         _memory;
    private readonly AniOptions             _options;
    private readonly ILogger<DesireEngine>  _log;

    // Daily outreach counter — resets when the day rolls over
    private int            _outreachCountToday;
    private DateTimeOffset _outreachCountDay = DateTimeOffset.MinValue;

    public DesireEngine(IMemoryService memory, IOptions<AniOptions> options, ILogger<DesireEngine> log)
    {
        _memory  = memory;
        _options = options.Value;
        _log     = log;
    }

    // ── Scheduling ────────────────────────────────────────────────────────────

    /// <summary>
    /// Pure function — no side effects. The single source of timing truth.
    /// Inverts the exponential probability model to produce a concrete delay.
    ///
    /// Formula: t = -λ * ln(1 - targetP)
    /// Then modulated by desire level, circadian weight, and ±20% jitter.
    /// Clamped to [MinWakeMinutes, MaxWakeMinutes].
    /// </summary>
    public TimeSpan ComputeNextWakeTime(DesireState desire)
    {
        // Base: time at which there is ThinkTargetProbability chance she would have thought
        var baseMinutes = -_options.DesireLambdaMinutes * Math.Log(1.0 - _options.ThinkTargetProbability);

        // High desire = wake sooner; modifier ranges 0.4–1.0
        var desireModifier = 1.0 - (desire.DesireToConnect * 0.6);

        // Circadian: morning/evening raise modifier (shorten interval), night lowers it
        var circadian = (double)Math.Max(desire.CircadianModifier, 0.01f); // guard against 0

        // Jitter: ±20% — Ani cannot predict herself, neither can the contact
        var jitterFactor = 0.8 + (Random.Shared.NextDouble() * 0.4);

        var finalMinutes = baseMinutes * desireModifier * (1.0 / circadian) * jitterFactor;
        finalMinutes = Math.Clamp(finalMinutes, _options.MinWakeMinutes, _options.MaxWakeMinutes);

        return TimeSpan.FromMinutes(finalMinutes);
    }

    // ── Outreach gate ─────────────────────────────────────────────────────────

    /// <summary>
    /// Threshold is re-randomised on each evaluation — Ani cannot predict herself.
    /// Returns false immediately if cooldown is active.
    /// </summary>
    public async Task<bool> ShouldReachOutAsync(CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);

        // Auto-expire cooldown
        if (state.CooldownActive && DateTimeOffset.UtcNow >= state.CooldownUntil)
        {
            state.CooldownActive = false;
            _log.LogDebug("Cooldown expired — lifting");
            await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
        }

        if (state.CooldownActive)
        {
            _log.LogDebug("Cooldown active until {Until} — outreach blocked", state.CooldownUntil);
            return false;
        }

        // Enforce daily outreach limit
        var today = DateTimeOffset.Now.Date;
        if (_outreachCountDay.Date != today)
        {
            _outreachCountToday = 0;
            _outreachCountDay   = DateTimeOffset.Now;
        }
        if (_outreachCountToday >= _options.MaxOutreachPerDay)
        {
            _log.LogInformation("Daily outreach limit reached ({Limit}) — blocked", _options.MaxOutreachPerDay);
            return false;
        }

        var threshold = _options.OutreachThresholdFloor + (Random.Shared.NextDouble() * _options.OutreachThresholdRange);
        var passes = state.DesireToConnect >= threshold;
        _log.LogInformation("Outreach gate: desire={Desire:F2} threshold={Threshold:F2} → {Result}",
            state.DesireToConnect, threshold, passes ? "PASS" : "blocked");
        return passes;
    }

    // ── State reads ───────────────────────────────────────────────────────────

    public async Task<DesireState> GetStateAsync(CancellationToken ct = default)
        => await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);

    // ── State mutations (single code path per write) ──────────────────────────

    /// <summary>
    /// Applies temporal drift and refreshes the circadian modifier.
    /// Called once per cognitive cycle after the inner thought completes.
    /// </summary>
    public async Task ApplyDriftAsync(CancellationToken ct = default)
    {
        var state   = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);

        // Use the more recent of LastContactInbound or LastOutreach — Ani's own messages
        // partially satisfy her connection need, so drift should slow after she texts too
        var lastConnection = state.LastContactInbound > state.LastOutreach
            ? state.LastContactInbound : state.LastOutreach;
        var elapsed = DateTimeOffset.UtcNow - lastConnection;

        var previousDesire = state.DesireToConnect;
        var drift = (float)Math.Min(elapsed.TotalHours * _options.DriftPerHour, _options.DriftCapPerCycle);
        state.DesireToConnect   = Math.Min(1.0f, state.DesireToConnect + drift);
        state.LastInnerThought  = DateTimeOffset.UtcNow;

        // Circadian hour uses local time intentionally — we want Ani's clock, not UTC
        state.CircadianModifier = ComputeCircadianModifier();

        _log.LogInformation("Desire drift: {Previous:F2} + {Drift:F2} → {New:F2} (elapsed={Hours:F1}h, circadian={Circadian:F2})",
            previousDesire, drift, state.DesireToConnect, elapsed.TotalHours, state.CircadianModifier);

        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a new trigger and proportionally elevates desire.
    /// Each trigger contributes weight * 0.15 to desire (capped at 1.0).
    /// </summary>
    public async Task AddTriggerAsync(
        TriggerType type, float weight, string description, CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);

        state.ActiveTriggers.Add(new DesireTrigger
        {
            Type        = type,
            Weight      = weight,
            Description = description,
            CreatedAt   = DateTimeOffset.UtcNow,
        });

        var bump = weight * (float)_options.TriggerDesireMultiplier;
        state.DesireToConnect = Math.Min(1.0f, state.DesireToConnect + bump);

        _log.LogDebug("Trigger added: {Type} weight={Weight:F2} bump={Bump:F2} → desire={Desire:F2}",
            type, weight, bump, state.DesireToConnect);

        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks cooldown active. The heartbeat's next ComputeNextWakeTime call will return
    /// a longer delay because desire will be lower after outreach.
    /// </summary>
    public async Task ApplyCooldownAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        state.CooldownActive = true;
        state.CooldownUntil  = DateTimeOffset.UtcNow + duration;
        _log.LogDebug("Cooldown activated until {Until} (desire={Desire:F2})", state.CooldownUntil, state.DesireToConnect);
        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that the primary contact reached out. Updates the LastContactInbound timestamp
    /// so desire drift uses the correct elapsed time.
    /// </summary>
    public async Task RecordInboundContactAsync(CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        state.LastContactInbound = DateTimeOffset.UtcNow;
        _log.LogDebug("Inbound contact recorded at {Time}", state.LastContactInbound);
        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets all desire state after a successful outreach.
    /// Desire is zeroed, triggers cleared, cooldown lifted, timestamp recorded.
    /// </summary>
    public async Task ResetAfterOutreachAsync(CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Outreach reset: desire {Previous:F2} → 0.00, clearing {TriggerCount} triggers",
            state.DesireToConnect, state.ActiveTriggers.Count);
        state.DesireToConnect = 0.0f;
        state.LastOutreach    = DateTimeOffset.UtcNow;
        state.ActiveTriggers.Clear();

        // Track daily outreach count
        _outreachCountToday++;

        // Activate cooldown — prevents rapid-fire messages
        state.CooldownActive = true;
        state.CooldownUntil  = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_options.MinOutreachGapMinutes);
        _log.LogInformation("Cooldown activated until {Until} ({Minutes} min)",
            state.CooldownUntil, _options.MinOutreachGapMinutes);

        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    // Local time is intentional here — circadian rhythm maps to Ani's (contact's) timezone
    private static float ComputeCircadianModifier() => DateTimeOffset.Now.Hour switch
    {
        >= 6  and < 10  => 1.2f,   // morning  — curious, engaged
        >= 10 and < 17  => 1.0f,   // afternoon — neutral
        >= 17 and < 21  => 1.15f,  // evening  — warm, reflective
        >= 21 and < 23  => 0.8f,   // late evening — quieter
        _               => 0.4f,   // night    — only if something feels important
    };
}
