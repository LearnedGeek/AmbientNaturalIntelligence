using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
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
    private readonly IMemoryService _memory;
    private readonly AniOptions     _options;

    public DesireEngine(IMemoryService memory, IOptions<AniOptions> options)
    {
        _memory  = memory;
        _options = options.Value;
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

        // Jitter: ±20% — Ani cannot predict herself, neither can Mark
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
        if (state.CooldownActive) return false;

        var threshold = 0.55 + (Random.Shared.NextDouble() * 0.30);
        return state.DesireToConnect >= threshold;
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
        var elapsed = DateTimeOffset.UtcNow - state.LastMarkContact;

        // Drift contribution capped at 0.4 per cycle to prevent runaway accumulation
        var drift = (float)Math.Min(elapsed.TotalHours * 0.08, 0.4);
        state.DesireToConnect   = Math.Min(1.0f, state.DesireToConnect + drift);
        state.LastInnerThought  = DateTimeOffset.UtcNow;

        // Circadian hour uses local time intentionally — we want Ani's clock, not UTC
        state.CircadianModifier = ComputeCircadianModifier();

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

        state.DesireToConnect = Math.Min(1.0f, state.DesireToConnect + weight * 0.15f);

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
        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets all desire state after a successful outreach.
    /// Desire is zeroed, triggers cleared, cooldown lifted, timestamp recorded.
    /// </summary>
    public async Task ResetAfterOutreachAsync(CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        state.DesireToConnect = 0.0f;
        state.CooldownActive  = false;
        state.LastOutreach    = DateTimeOffset.UtcNow;
        state.ActiveTriggers.Clear();
        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    // Local time is intentional here — circadian rhythm maps to Ani's (Mark's) timezone
    private static float ComputeCircadianModifier() => DateTimeOffset.Now.Hour switch
    {
        >= 6  and < 10  => 1.2f,   // morning  — curious, engaged
        >= 10 and < 17  => 1.0f,   // afternoon — neutral
        >= 17 and < 21  => 1.15f,  // evening  — warm, reflective
        >= 21 and < 23  => 0.8f,   // late evening — quieter
        _               => 0.4f,   // night    — only if something feels important
    };
}
