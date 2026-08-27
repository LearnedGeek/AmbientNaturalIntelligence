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
    private readonly IStateStore             _state;
    private readonly IMemoryPersistence      _persist;
    private readonly AniOptions             _options;
    private readonly ILogger<DesireEngine>  _log;
    private readonly IOllamaClient?         _ollama;

    // #111 fix (2026-08-27) — serialises the read-mutate-write cycle inside
    // AddTriggerAsync. Two concurrent callers (e.g. CognitiveCyclePipeline and
    // OutreachPipeline overlapping) previously could each read the same
    // pre-mutation state, both miss the same near-duplicate on semantic
    // dedup, both append, and one write clobber the other. AniRuntime is a
    // single-process service so an in-process semaphore is sufficient; a
    // multi-process deployment would need to push the atomicity into
    // IStateStore.MutateDesireStateAsync (fix candidate #2 in the issue).
    // Scoped to AddTriggerAsync specifically because that's the identified
    // race site; other _state.GetDesireStateAsync callers stay unlocked.
    private readonly SemaphoreSlim _addTriggerLock = new(1, 1);

    // Daily outreach counter — resets when the day rolls over
    private int            _outreachCountToday;
    private DateTimeOffset _outreachCountDay = DateTimeOffset.MinValue;

    // Night outreach counter — resets when night hours end
    private int  _nightOutreachCount;
    private bool _wasNight;

    // Morning window send counter — resets when morning window ends
    private int  _morningSendCount;
    private bool _wasMorningWindow;

    /// <summary>
    /// F-1 Phase 2 (2026-08-15): <paramref name="ollama"/> is optional. When
    /// non-null and <see cref="AniOptions.TriggerSemanticDedupEnabled"/> is
    /// true, <see cref="AddTriggerAsync"/> computes an embedding and merges
    /// near-duplicate triggers rather than appending. When null (test
    /// harnesses, some fixtures), dedup silently no-ops and behaviour
    /// matches the pre-Phase-2 append-only shape.
    /// </summary>
    public DesireEngine(
        IStateStore state,
        IMemoryPersistence persist,
        IOptions<AniOptions> options,
        ILogger<DesireEngine> log,
        IOllamaClient? ollama = null)
    {
        _state   = state;
        _persist = persist;
        _options = options.Value;
        _log     = log;
        _ollama  = ollama;
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
        var state = await _state.GetDesireStateAsync(ct).ConfigureAwait(false);

        // Auto-expire cooldown
        if (state.CooldownActive && DateTimeOffset.UtcNow >= state.CooldownUntil)
        {
            state.CooldownActive = false;
            _log.LogDebug("Cooldown expired — lifting");
            await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
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

        // Enforce night outreach limit — Feature 21: strict zero-send zone 10pm–6am
        var isNight = IsNightHours();
        if (!isNight && _wasNight) _nightOutreachCount = 0;  // reset on transition to day
        _wasNight = isNight;
        if (isNight && _nightOutreachCount >= _options.MaxNightOutreach)
        {
            _log.LogInformation("Night outreach limit reached ({Limit}) — sleeping", _options.MaxNightOutreach);
            return false;
        }

        // Feature 21: Morning window — one allowed send between 6–8am
        var isMorning = IsMorningWindow();
        if (!isMorning && _wasMorningWindow) _morningSendCount = 0;  // reset when window closes
        _wasMorningWindow = isMorning;
        if (isMorning && _options.AllowSingleMorningSend && _morningSendCount >= 1)
        {
            _log.LogInformation("Morning window send already used — waiting for normal hours");
            return false;
        }

        // Night (non-morning): higher threshold (0.80–0.95) so only strong desire breaks through
        // Morning window: slightly elevated threshold (0.70–0.90) — a gentler gate
        double floor, range;
        if (isNight && !isMorning)
        {
            floor = 0.80;
            range = 0.15;
        }
        else if (isMorning)
        {
            floor = 0.70;
            range = 0.20;
        }
        else
        {
            floor = _options.OutreachThresholdFloor;
            range = _options.OutreachThresholdRange;
        }
        var threshold = floor + (Random.Shared.NextDouble() * range);

        // Feature 35 (Borotschnig): Emotional state modulates outreach threshold
        // High warmth lowers threshold (positive state makes reaching out feel natural)
        // High worry lowers threshold (concern drives check-in behavior)
        var emotionalState = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var warmthExcess = Math.Max(0f, emotionalState.Warmth - emotionalState.WarmthBaseline);
        var worryExcess = Math.Max(0f, emotionalState.Worry - emotionalState.WorryBaseline);
        var thresholdReduction = (warmthExcess * 0.08f) + (worryExcess * 0.12f); // max ~0.10 reduction
        threshold = Math.Max(0.40, threshold - thresholdReduction); // never below 0.40

        var passes = state.DesireToConnect >= threshold;
        _log.LogInformation("Outreach gate: desire={Desire:F2} threshold={Threshold:F2} (emotion adj={Adj:F2}) → {Result}",
            state.DesireToConnect, threshold, thresholdReduction, passes ? "PASS" : "blocked");
        return passes;
    }

    // ── State reads ───────────────────────────────────────────────────────────

    public async Task<DesireState> GetStateAsync(CancellationToken ct = default)
        => await _state.GetDesireStateAsync(ct).ConfigureAwait(false);

    // ── State mutations (single code path per write) ──────────────────────────

    /// <summary>
    /// Applies temporal drift and refreshes the circadian modifier.
    /// Called once per cognitive cycle after the inner thought completes.
    ///
    /// Drift is dampened by a composite satisfaction score derived from:
    ///   - Conversation recency (exponential decay with configurable half-life)
    ///   - Emotional warmth (high warmth = connection need partly met)
    ///   - Inner life engagement (energy + playfulness = rich inner life)
    ///
    /// Formula: effectiveDrift = baseDrift × (1 - satisfaction × dampeningFactor)
    /// This provides natural downward pressure — satisfied Ani doesn't escalate desire.
    /// </summary>
    public async Task ApplyDriftAsync(float motivationMultiplier = 1.0f, CancellationToken ct = default)
    {
        var state   = await _state.GetDesireStateAsync(ct).ConfigureAwait(false);

        // Use the more recent of LastContactInbound or LastOutreach — Ani's own messages
        // partially satisfy her connection need, so drift should slow after she texts too
        var lastConnection = state.LastContactInbound > state.LastOutreach
            ? state.LastContactInbound : state.LastOutreach;
        var elapsed = DateTimeOffset.UtcNow - lastConnection;

        // Compute satisfaction from available signals
        var emotionalState = await _state.GetEmotionalStateAsync(ct).ConfigureAwait(false);
        var satisfaction = ComputeSatisfaction(state, emotionalState, elapsed);

        var previousDesire = state.DesireToConnect;
        var baseDrift = (float)Math.Min(elapsed.TotalHours * _options.DriftPerHour, _options.DriftCapPerCycle);

        // Dampen drift by satisfaction — high satisfaction = less upward pressure
        var dampening = 1.0f - (float)(satisfaction * _options.SatisfactionDampeningFactor);
        var drift = baseDrift * dampening;

        // Feature 35 (Borotschnig): Emotion→desire modulation
        // High worry accelerates drift (concern makes her want to check in)
        // Low energy suppresses drift (subdued state reduces outreach impulse)
        var emotionModifier = ComputeEmotionDesireModifier(emotionalState);
        drift *= emotionModifier;

        // Feature 33 (Liu et al.): Motivation scoring — high-quality thoughts
        // accelerate desire, routine thoughts contribute less
        drift *= motivationMultiplier;

        state.DesireToConnect   = Math.Min(1.0f, state.DesireToConnect + drift);
        state.LastInnerThought  = DateTimeOffset.UtcNow;

        // Circadian hour uses local time intentionally — we want Ani's clock, not UTC
        state.CircadianModifier = ComputeCircadianModifier();

        _log.LogInformation(
            "Desire drift: {Previous:F2} + {Drift:F2} → {New:F2} (base={BaseDrift:F2}, satisfaction={Satisfaction:F2}, dampening={Dampening:F2}, emotion={EmotionMod:F2}, motivation={MotivationMod:F2}, elapsed={Hours:F1}h, circadian={Circadian:F2})",
            previousDesire, drift, state.DesireToConnect, baseDrift, satisfaction, dampening, emotionModifier, motivationMultiplier, elapsed.TotalHours, state.CircadianModifier);

        await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a new trigger and proportionally elevates desire.
    /// Each new trigger contributes weight * TriggerDesireMultiplier to
    /// desire (capped at 1.0).
    ///
    /// <para>
    /// F-1 Phase 2 (2026-08-15): when <see cref="AniOptions.TriggerSemanticDedupEnabled"/>
    /// is on and an <see cref="IOllamaClient"/> is available, an embedding
    /// of <paramref name="description"/> is computed and compared against
    /// existing <see cref="DesireTrigger.SemanticKey"/> vectors. If any
    /// existing trigger with the SAME <paramref name="type"/> AND
    /// <paramref name="source"/> is cosine-similar ≥
    /// <see cref="AniOptions.TriggerSemanticDedupThreshold"/>, the existing
    /// trigger is REFRESHED (weight bumped to max, CreatedAt reset) and no
    /// new envelope is appended. The desire scalar is not bumped again in
    /// the merge case — duplicate triggers shouldn't double-count against
    /// the outreach threshold.
    /// </para>
    ///
    /// <para>
    /// Dedup is scoped to (Type, Source) so that semantically-identical
    /// text arriving from different producer sites keeps distinct
    /// provenance rather than being folded into an earlier envelope under
    /// the earlier tag (Mark review 2026-08-15 P1 finding).
    /// </para>
    ///
    /// <para>
    /// A hard cap <see cref="AniOptions.TriggerMaxActive"/> is enforced
    /// as a safety net (drops oldest) independent of dedup, so a
    /// pathological run of embedding failures cannot let ActiveTriggers
    /// grow unboundedly.
    /// </para>
    /// </summary>
    /// <param name="type">The trigger family enum (kept for bookkeeping continuity).</param>
    /// <param name="weight">Weight in [0,1] — how strongly this trigger elevates desire.</param>
    /// <param name="description">The human-readable text rendered into the outreach prompt.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="source">
    /// Optional call-site source discriminator (e.g. <c>"inner-thought-valence"</c>,
    /// <c>"held-back-outreach"</c>). Populates <see cref="DesireTrigger.Source"/>;
    /// scopes semantic-dedup match against other triggers of the same source only;
    /// composes into the rendered <c>SourceType</c> tag as <c>trigger.{source}</c>.
    /// When null, dedup matches against triggers where <c>Source</c> is also null
    /// (grouped as the untagged legacy family), and the rendered tag falls back to
    /// the <see cref="TriggerType"/>-derived default.
    /// </param>
    public async Task AddTriggerAsync(
        TriggerType type, float weight, string description, CancellationToken ct = default,
        string? source = null)
    {
        // #111 fix (2026-08-27) — serialise read-mutate-write so two
        // concurrent callers can't both miss the same near-duplicate on
        // semantic dedup and both append. See _addTriggerLock XML doc.
        await _addTriggerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await _state.GetDesireStateAsync(ct).ConfigureAwait(false);

            // F-1 Phase 2: try to compute an embedding for semantic dedup.
            // Failure is non-fatal — degrades to append-only path (pre-Phase-2 shape).
            // Cancellation MUST propagate (Serge review 2026-08-15) — don't swallow OCE.
            float[]? embedding = null;
            if (_options.TriggerSemanticDedupEnabled && _ollama is not null
                && !string.IsNullOrWhiteSpace(description))
            {
                try
                {
                    embedding = await _ollama.EmbedAsync(description, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Trigger embedding failed — appending without semantic dedup");
                }
            }

            // If we have an embedding, look for a near-duplicate to merge into.
            // Match is scoped to same (Type, Source) so cross-source-type duplicates
            // do NOT collapse and destroy provenance (Mark review P1 finding).
            if (embedding is not null)
            {
                var threshold = (float)_options.TriggerSemanticDedupThreshold;
                var match = state.ActiveTriggers.FirstOrDefault(t =>
                    t.Type == type &&
                    string.Equals(t.Source, source, StringComparison.Ordinal) &&
                    t.SemanticKey is not null &&
                    VectorMath.CosineSimilarity(embedding, t.SemanticKey) >= threshold);

                if (match is not null)
                {
                    // Refresh the existing trigger — do NOT bump desire (already counted).
                    // Leave SemanticKey and Description UNCHANGED (Mark review P2 finding):
                    // key must stay paired with the displayed text so chained
                    // near-threshold merges cannot walk the key away from Description.
                    var prevWeight = match.Weight;
                    match.Weight    = Math.Max(match.Weight, weight);
                    match.CreatedAt = DateTimeOffset.UtcNow;

                    _log.LogDebug(
                        "Trigger merged: {Type}/{Source} desc='{Desc}' weight={PrevWeight:F2}→{NewWeight:F2} (semantic match with '{ExistingDesc}')",
                        type, source ?? "(null)", TrimForLog(description), prevWeight, match.Weight, TrimForLog(match.Description));

                    await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
                    return;
                }
            }

            // No merge — append a new envelope and bump desire as before.
            state.ActiveTriggers.Add(new DesireTrigger
            {
                Type        = type,
                Weight      = weight,
                Description = description,
                CreatedAt   = DateTimeOffset.UtcNow,
                SemanticKey = embedding,
                Source      = source,
            });

            // Enforce hard cap independent of dedup — drops OLDEST first.
            var cap = _options.TriggerMaxActive;
            if (cap > 0 && state.ActiveTriggers.Count > cap)
            {
                var toDrop = state.ActiveTriggers.Count - cap;
                state.ActiveTriggers
                    .Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
                state.ActiveTriggers.RemoveRange(0, toDrop);
                _log.LogDebug("Trigger cap enforced: dropped {Count} oldest (cap={Cap})", toDrop, cap);
            }

            var bump = weight * (float)_options.TriggerDesireMultiplier;
            state.DesireToConnect = Math.Min(1.0f, state.DesireToConnect + bump);

            _log.LogDebug("Trigger added: {Type} weight={Weight:F2} bump={Bump:F2} → desire={Desire:F2} (active={Count})",
                type, weight, bump, state.DesireToConnect, state.ActiveTriggers.Count);

            await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _addTriggerLock.Release();
        }
    }

    private static string TrimForLog(string s)
        => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>
    /// Marks cooldown active. The heartbeat's next ComputeNextWakeTime call will return
    /// a longer delay because desire will be lower after outreach.
    /// </summary>
    public async Task ApplyCooldownAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var state = await _state.GetDesireStateAsync(ct).ConfigureAwait(false);
        state.CooldownActive = true;
        state.CooldownUntil  = DateTimeOffset.UtcNow + duration;
        _log.LogDebug("Cooldown activated until {Until} (desire={Desire:F2})", state.CooldownUntil, state.DesireToConnect);
        await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that the primary contact reached out. Updates the LastContactInbound timestamp
    /// so desire drift uses the correct elapsed time.
    /// </summary>
    public async Task RecordInboundContactAsync(CancellationToken ct = default)
    {
        var state = await _state.GetDesireStateAsync(ct).ConfigureAwait(false);
        state.LastContactInbound = DateTimeOffset.UtcNow;
        _log.LogDebug("Inbound contact recorded at {Time}", state.LastContactInbound);
        await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets all desire state after a successful outreach.
    /// Desire is zeroed, triggers cleared, cooldown lifted, timestamp recorded.
    /// </summary>
    /// <summary>
    /// Resets desire after a conversation reply. Does NOT count toward daily/night
    /// outreach limits — conversation replies are responses to the contact's messages,
    /// not unprompted outreach. The contact initiated; Ani replied.
    /// </summary>
    public async Task ResetAfterConversationReplyAsync(CancellationToken ct = default)
    {
        var state = await _state.GetDesireStateAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Conversation reset: desire {Previous:F2} → 0.00, clearing {TriggerCount} triggers",
            state.DesireToConnect, state.ActiveTriggers.Count);
        state.DesireToConnect = 0.0f;
        state.LastOutreach    = DateTimeOffset.UtcNow;
        state.ActiveTriggers.Clear();

        // Cooldown still applies — prevents rapid-fire replies
        state.CooldownActive = true;
        state.CooldownUntil  = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_options.MinOutreachGapMinutes);
        _log.LogInformation("Cooldown activated until {Until} ({Minutes} min)",
            state.CooldownUntil, _options.MinOutreachGapMinutes);

        await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets desire after unprompted outreach. Counts toward daily/night limits.
    /// </summary>
    public async Task ResetAfterOutreachAsync(CancellationToken ct = default)
    {
        var state = await _state.GetDesireStateAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Outreach reset: desire {Previous:F2} → 0.00, clearing {TriggerCount} triggers",
            state.DesireToConnect, state.ActiveTriggers.Count);
        state.DesireToConnect = 0.0f;
        state.LastOutreach    = DateTimeOffset.UtcNow;
        state.ActiveTriggers.Clear();

        // Track daily, night, and morning outreach counts — only for unprompted outreach
        _outreachCountToday++;
        if (IsNightHours()) _nightOutreachCount++;
        if (IsMorningWindow()) _morningSendCount++;

        // Activate cooldown — prevents rapid-fire messages
        state.CooldownActive = true;
        state.CooldownUntil  = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_options.MinOutreachGapMinutes);
        _log.LogInformation("Cooldown activated until {Until} ({Minutes} min)",
            state.CooldownUntil, _options.MinOutreachGapMinutes);

        await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Partial desire decay — reduces desire by a fraction without full reset.
    /// Used by Feature 28 (Dispatch Coherence Gate) when Door C suppresses a message:
    /// the desire to connect is real, but the composed message wasn't coherent enough to send.
    /// </summary>
    public async Task DecayDesireAsync(float fraction, string reason, CancellationToken ct = default)
    {
        var state = await _state.GetDesireStateAsync(ct).ConfigureAwait(false);
        var previous = state.DesireToConnect;
        state.DesireToConnect *= (1.0f - fraction);
        _log.LogInformation("Desire decay: {Previous:F2} → {New:F2} ({Fraction:P0} reduction) — {Reason}",
            previous, state.DesireToConnect, fraction, reason);
        await _persist.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the current local hour falls within configured night hours.
    /// Used by ShouldReachOutAsync to enforce nighttime outreach limits.
    /// </summary>
    public bool IsNightHours()
    {
        var hour = DateTimeOffset.Now.Hour;
        // Handle wrap-around (e.g., 22–6 spans midnight)
        return _options.NightStartHour > _options.NightEndHour
            ? hour >= _options.NightStartHour || hour < _options.NightEndHour
            : hour >= _options.NightStartHour && hour < _options.NightEndHour;
    }

    /// <summary>
    /// Feature 21: Morning window check — the 6–8am window where one send is allowed.
    /// This is a sub-window within night hours, so a morning hour is both "night" and "morning".
    /// The morning send is the right instinct — Ani reaching out as Mark starts his day.
    /// </summary>
    public bool IsMorningWindow()
    {
        var hour = DateTimeOffset.Now.Hour;
        return hour >= _options.MorningWindowStartHour && hour < _options.MorningWindowEndHour;
    }

    /// <summary>
    /// Composite satisfaction score (0.0–1.0) derived from existing signals.
    /// High satisfaction = connection need is being met, desire should drift slower.
    ///
    /// Three components weighted equally:
    ///   1. Conversation recency — exponential decay from last contact (half-life configurable)
    ///   2. Emotional warmth — how warm Ani feels relative to baseline
    ///   3. Inner life engagement — energy + playfulness indicate a rich inner life
    ///      that partially satisfies the need for connection
    /// </summary>
    public float ComputeSatisfaction(DesireState desire, EmotionalState emotions, TimeSpan sinceLastContact)
    {
        // 1. Conversation recency: exponential decay — recently talked = high satisfaction
        //    At half-life hours, recency = 0.5. At 2× half-life, recency ≈ 0.25.
        var halfLife = _options.SatisfactionRecencyHalfLifeHours;
        var recency = (float)Math.Exp(-0.693 * sinceLastContact.TotalHours / halfLife);

        // 2. Emotional warmth relative to baseline — warmth above baseline means
        //    the connection need is partly met through recent emotional experiences
        var warmthExcess = Math.Max(0f, emotions.Warmth - emotions.WarmthBaseline);
        var warmthRange = 1f - emotions.WarmthBaseline;
        var warmthSatisfaction = warmthRange > 0 ? warmthExcess / warmthRange : 0f;

        // 3. Inner life engagement — rich inner life (high energy + playfulness)
        //    partially satisfies the need for external connection
        var energyAbove = Math.Max(0f, emotions.Energy - emotions.EnergyBaseline);
        var playAbove = Math.Max(0f, emotions.Playfulness - emotions.PlayfulnessBaseline);
        var engagement = (energyAbove + playAbove) / 2f;
        // Normalize: max possible is ~0.5 each above baseline
        var engagementSatisfaction = Math.Min(1f, engagement * 2f);

        // Equal-weight composite
        var satisfaction = (recency + warmthSatisfaction + engagementSatisfaction) / 3f;
        return Math.Clamp(satisfaction, 0f, 1f);
    }

    /// <summary>
    /// Feature 35 (Borotschnig 2025): Emotional state modulates desire drift rate.
    /// Emotions function as "biasing action selection" — concern biases toward outreach,
    /// low energy biases toward silence, warmth biases toward connection.
    ///
    /// Returns a multiplier (0.5–1.5) applied to the base drift:
    ///   - High worry → multiplier > 1.0 (concern accelerates desire)
    ///   - High warmth → multiplier > 1.0 (positive state encourages connection)
    ///   - Low energy → multiplier < 1.0 (subdued state suppresses outreach impulse)
    ///   - Combined: worry and warmth compound, energy provides a brake
    ///
    /// Guards against feedback loops: multiplier is clamped to [0.5, 1.5].
    /// The satisfaction dampening (upstream) provides the primary brake;
    /// this modifier adds emotional coloring to the drift rate.
    /// </summary>
    public static float ComputeEmotionDesireModifier(EmotionalState emotions)
    {
        // Worry above baseline accelerates desire (concern → check in)
        var worryExcess = Math.Max(0f, emotions.Worry - emotions.WorryBaseline);
        var worryBoost = worryExcess * 1.5f; // max ~0.3 at worry=0.4 above baseline

        // Warmth above baseline gently accelerates desire (positive connection)
        var warmthExcess = Math.Max(0f, emotions.Warmth - emotions.WarmthBaseline);
        var warmthBoost = warmthExcess * 0.8f; // max ~0.3 at warmth=0.4 above baseline

        // Low energy suppresses desire (subdued state → less outreach impulse)
        var energyDeficit = Math.Max(0f, emotions.EnergyBaseline - emotions.Energy);
        var energyBrake = energyDeficit * 1.2f; // max ~0.3 at energy=0.25 below baseline

        var modifier = 1.0f + worryBoost + warmthBoost - energyBrake;
        return Math.Clamp(modifier, 0.5f, 1.5f);
    }

    // Local time is intentional here — circadian rhythm maps to Ani's (contact's) timezone
    private static float ComputeCircadianModifier() => DateTimeOffset.Now.Hour switch
    {
        >= 6  and < 10  => 1.2f,   // morning  — curious, engaged
        >= 10 and < 17  => 1.0f,   // afternoon — neutral
        >= 17 and < 21  => 1.15f,  // evening  — warm, reflective
        >= 21 and < 23  => 0.8f,   // late evening — quieter
        >= 23           => 0.2f,   // late night — winding down, rare thoughts
        _               => 0.1f,   // deep night (midnight–6 AM) — deep sleep, almost silent
    };
}
