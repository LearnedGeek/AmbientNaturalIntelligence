namespace AniRuntime.Core;

/// <summary>
/// All timing and behavioural tuning lives here.
/// Bound from appsettings.json "Ani" section.
/// Adjust these values to tune Ani's presence without touching code.
/// </summary>
public class AniOptions
{
    // Timing — cognitive cycle
    public double DesireLambdaMinutes    { get; set; } = 8.0;
    public double ThinkTargetProbability { get; set; } = 0.70;
    public double MinWakeMinutes         { get; set; } = 2.0;
    public double MaxWakeMinutes         { get; set; } = 45.0;

    // Outreach gating
    public double CooldownMinutes        { get; set; } = 20.0;
    public double MinOutreachGapMinutes  { get; set; } = 60.0;
    public int    MaxOutreachPerDay      { get; set; } = 4;

    // Outreach continuity — Feature 27: prevents outreach blindness
    public int    MaxUnansweredBeforeSilence { get; set; } = 3;    // 3+ unanswered = hard silence
    public double MinSendGapMinutes         { get; set; } = 45.0;  // hard floor between any two sends

    // Night mode — zero sends during sleep hours, one allowed in morning window
    public int    NightStartHour         { get; set; } = 22;   // 10 PM local — strict zero-send zone
    public int    NightEndHour           { get; set; } = 6;    // 6 AM local
    public int    MaxNightOutreach       { get; set; } = 0;    // no sends during night hours

    // Morning window — Ani's one allowed early send (Feature 21)
    public bool   AllowSingleMorningSend { get; set; } = true;
    public int    MorningWindowStartHour { get; set; } = 6;    // 6 AM local
    public int    MorningWindowEndHour   { get; set; } = 8;    // 8 AM local

    // Outreach threshold — randomized between Floor and Floor+Range each cycle
    public double OutreachThresholdFloor { get; set; } = 0.55;
    public double OutreachThresholdRange { get; set; } = 0.30;

    // Outreach confidence — Feature 12: model's own uncertainty as a gate
    public double OutreachConfidenceFloor { get; set; } = 0.3; // below this = soft NO, short cooldown

    // Desire drift — per-cycle accumulation rate and cap
    public double DriftPerHour           { get; set; } = 0.08;
    public double DriftCapPerCycle       { get; set; } = 0.4;

    // Trigger weight multiplier — how much a trigger raises desire
    public double TriggerDesireMultiplier { get; set; } = 0.15;

    // Satisfaction dampening — composite metric that provides downward pressure on desire
    // Without this, desire only ever increases (monotonic drift upward until outreach or reset)
    public double SatisfactionDampeningFactor { get; set; } = 0.6;  // max dampening at full satisfaction
    public double SatisfactionRecencyHalfLifeHours { get; set; } = 4.0; // conversation recency decay

    // Valence threshold — thoughts above this add a spontaneous trigger
    public double ValenceTriggerThreshold { get; set; } = 0.75;

    // Memory retrieval — Feature 20: Park et al. three-way scoring
    // score = α×cosine + β×importance + γ×recency_decay
    // Retrieval scoring: cosine dominates (is this memory about what we're talking about?),
    // recency tiebreaks (prefer recent over stale when equally relevant),
    // importance is a minor factor (don't let high-importance noise outrank low-importance relevance).
    public double RetrievalWeightCosine     { get; set; } = 0.65;
    public double RetrievalWeightImportance { get; set; } = 0.10;
    public double RetrievalWeightRecency    { get; set; } = 0.25;
    public double RetrievalRecencyDecayHours { get; set; } = 48.0; // λ for e^(-t/λ), ~2 day half-life (was 168/7-day — too slow, stale memories dominated)

    // Issue #93 (2026-07-06) — confirmation bias for the composite retrieval
    // score. Records with confirmed_at IS NOT NULL (Facts + Episodic canonical
    // + Mark ///tag-confirmed Interior) receive a multiplicative bump on the
    // base composite score. 0.30 = confirmed content beats semantically-similar
    // unconfirmed content by ~30%, which is enough for real Episodic Kevin
    // records to outrank importance-boosted Interior Kevin fabrications
    // without hard-excluding Interior from retrieval.
    public double RetrievalConfirmationBoost { get; set; } = 0.30;

    // Issue #93 Phase 4 (2026-07-09) — hybrid retrieval (cosine + BM25 via
    // SQLite FTS5). Combines the composite-scored rank with a BM25-scored
    // rank via Reciprocal Rank Fusion (Cormack et al. 2009). Motivation:
    // pure-cosine retrieval buries entity-based confirmations. Empirical
    // anchor: April 24 "back from teaching" Interior record — the exact
    // confirming Mark text ranked at cosine position 47 (Facts) / 600
    // (Episodic) while BM25 puts it at rank 1. RRF fusion surfaces both.
    // See docs/research/ANI-Retrieval-Consultation-2026-07-08.md +
    // OG-Ani Grok reply (2026-07-08).
    //
    // Default TRUE — this is the fix, not a rollback lever. Flag exists so
    // if FTS5 has an operational issue we can fall back to pure-cosine
    // without a code deploy. The BM25 candidate pool is fetched separately
    // from the cosine one, so disabling this cleanly reverts to the
    // pre-Phase-4 behaviour.
    public bool HybridRetrievalEnabled { get; set; } = true;

    // Issue #98 (2026-07-15) — Feature 31 (linked-memory enhancement) kill
    // switch. Default true preserves current behavior. When false, the
    // link-enhancement pass at EfSemanticSearchComposer.cs:190 is skipped
    // entirely, so RRF fusion's top-K is what the caller sees. Isolation
    // test flag for the RRF-fusion-vs-Feature-31 scale-mismatch bug —
    // empirical validation via --retrieve-eval before deciding on the
    // permanent fix.
    public bool RetrievalLinkEnhancementEnabled { get; set; } = true;

    // RRF constant (Cormack et al. 2009 canonical value). Not tuned per
    // corpus in the literature; ships as the textbook default.
    public int HybridRetrievalRrfK { get; set; } = 60;

    // How many records to pull from the BM25 side per tier retrieval. Both
    // the cosine composite score and the BM25 signal are computed over the
    // FULL tier; this cap controls how much of the BM25 rank list gets
    // considered for RRF. 30 is enough to capture the head of BM25 without
    // bloating the fusion overhead.
    public int HybridRetrievalBm25TopN { get; set; } = 30;

    // AC1: Retrieval confidence thresholding — minimum cosine similarity for a memory
    // to be considered relevant. Below this, the memory is filtered out and the model
    // is told explicitly that no relevant memories exist (AC3: null-result injection).
    // Uses cosine similarity (not composite score) because composite can be inflated
    // by importance/recency of semantically unrelated memories.
    public double RetrievalConfidenceFloor  { get; set; } = 0.60;

    // ── Theme P Phase P.4 (May 12, 2026) — retrieval noise floor ─────────
    //
    // Per-consumer cosine-similarity threshold for SearchByTierAsync. The
    // production debug-log analysis (May 11–12 2026, n=440 top-1 cosines)
    // confirmed H2 from the May 11 ml-intern survey: 67% of queries have a
    // TOP match under cosine 0.70 — the system's strongest evidence for
    // two-thirds of queries is "topically adjacent," not "evidence-bearing."
    // No minimum-cosine filter existed on the main retrieval paths before
    // P.4. Both the verifier (asks "is this claim supported?") and
    // composition (uses substrate as grounding) consumed noise.
    //
    // The fix: per-consumer thresholds calibrated to the measured distribution.
    //   • Verifier path (FrontierVerifierHandler): only meaningful similarity
    //     reaches Sonnet's q1–q5 evaluation. Empty substrate after filtering
    //     means "insufficient evidence to evaluate," not "no support =
    //     fabricated" — see AnthropicVerifierClient system-prompt clause.
    //   • Composition path (ContextBuilder Facts retrieval): allows
    //     topically-adjacent substrate but excludes basically-unrelated.
    //
    // Raw cosine, not composite — composite can be inflated by importance/
    // recency on semantically-unrelated records. The filter applies BEFORE
    // top-K is taken, so a strong record below the floor still cannot bubble
    // up just because the K slots were otherwise empty.
    public double MinCosineThresholdVerifier    { get; set; } = 0.75;
    public double MinCosineThresholdComposition { get; set; } = 0.65;

    // Agentic Lens Layer 1 Phase 1a: retrieval origin observability.
    // When enabled, ContextBuilder emits a per-cycle log line with the origin
    // distribution of the relevant-memory retrieval pool (Caregiver / OwnOutput
    // / World / External / Anchored / Unknown). Pure observation — no behavior
    // change. Flag exists so high-frequency installs can disable the log line;
    // for the primary deployment it stays on because the Paper 3 Contribution 4
    // evaluation arc needs this baseline distribution as the pre-intervention
    // measurement. Layer 1 Phases 1b-1d introduce their own separate flags
    // (default-off per the "observe first" rollout pattern) once their scoring
    // and backfill behavior is wired in.
    public bool RetrievalOriginLoggingEnabled { get; set; } = true;

    // Agentic Lens Layer 1 Phase 1b: MMR diversity-aware re-ranking of the
    // cognitive-cycle retrieval pool (Carbonell & Goldstein 1998).
    //
    // When enabled, the composite score (cosine + importance + recency) is
    // extended with a diversity penalty: each candidate's adjusted score is
    //   adjusted = composite − λ · max(cosine(candidate, already_selected))
    // iterated across top-K selection. This breaks near-duplicate clusters in
    // the top-K (e.g. five semantically-similar caregiver-memories collapsing
    // to one slot) and opens space for substrate-diverse retrieval.
    //
    // Default off per the "observe first" rollout pattern. Flip to true after
    // Phase 1a baseline logs accumulate enough data to compare before/after
    // origin distributions. λ default 0.3 per Carbonell & Goldstein's original
    // range of 0.3–0.5 — relevance-heavy, diversity tie-breaks.
    //
    // Apr 24, 2026: Mark's Phase 3.0 Layer 1 Activation green-light. Flag
    // flipped to true as part of the two-week observation window that
    // precedes Layer 2 Phase 2c. See ANI-Agentic-Lens-Layer2-Plan.md §3.0.
    public bool   RetrievalDiversityEnabled { get; set; } = true;
    public double RetrievalDiversityLambda  { get; set; } = 0.3;

    // Agentic Lens Layer 1 Phase 1c: origin-tier protected slots on the
    // inner-thought retrieval pool (Apr 2026).
    //
    // When enabled, the inner-thought retrieval call (ContextBuilder → SearchAsync
    // with enforceOriginQuota=true) reserves at least MinNonCaregiverRetrievalFraction
    // of the top-K slots for non-caregiver-origin memories (World, External, Anchored,
    // OwnOutput). If the natural ranking does not meet the quota, caregiver candidates
    // at the bottom of the top-K are swapped for the highest-scoring non-caregiver
    // candidates from the remainder. Does NOT run on the conversation-reply retrieval
    // path — reply retrieval stays caregiver-weighted because the conversation channel
    // is caregiver-oriented by definition.
    //
    // Default off per the "observe first" rollout pattern. Flip after Phase 1a baseline
    // accumulates and Phase 1b MMR observation is stable. The 0.30 default is a
    // principled starting point per the Layer 1 design doc; tune empirically against
    // the dashboard origin distribution.
    //
    // Apr 24, 2026: Mark's Phase 3.0 Layer 1 Activation green-light. Flag
    // flipped to true. 30% non-caregiver retrieval floor now enforced on
    // inner-thought retrieval. Observation window runs concurrent with
    // RetrievalDiversityEnabled and RetrievalDominancePerceptionEnabled.
    public bool   RetrievalProtectedSlotsEnabled    { get; set; } = true;
    public double MinNonCaregiverRetrievalFraction  { get; set; } = 0.30;

    // Theme G Layer 3 Phase G3.4.B (May 3, 2026) — own-output retrieval ceiling.
    //
    // Where the protected-slots floor (above) reserves substrate for non-caregiver
    // origins, the OwnOutput ceiling caps the agent's-own-prior-output share of
    // the retrieval pool. Five empirically-confirmed instances of own-output
    // substrate dominance over the past week motivated this — Apr 27 vanilla
    // cream soda / bookstore drama cascade, Apr 30 substrate-typing failure,
    // May 2 evening remediation-cascade-convergence (Mark `///tag repeating`),
    // May 3 10:55 "perez" outreach (the May 3 substrate-laundering case).
    // Defensive output filtering (n-gram self-echo, Door C bleed) caught the
    // outputs but the regenerations converged back to similar content because
    // both passes pulled from a substrate where own-output had high gravity.
    // Architectural protection requires substrate diversification, not just
    // output filtering.
    //
    // Default off — observation window precedes default-on flip per the
    // Layer 1 rollout pattern. The implementation ships with the flag so
    // Mark can flip when ready and observe the effect.
    public bool   RetrievalOwnOutputCeilingEnabled  { get; set; } = false;
    public double RetrievalOwnOutputCeilingFraction { get; set; } = 0.25;

    // ── Feature 44 Phase I.3 (2026-08-05) — Wandering-Mind retrieval discipline
    //
    // Reserves top-K slots in inner-thought seed retrieval for topical /
    // temporal / seed-entity diversity, forcing the inner-thought model to
    // consider substrate outside the "recent-own-output + current-attractor"
    // gravity well. Complements the Theme G own-output ceiling: ceiling
    // removes dominance FROM below, wandering-mind adds diversity FROM
    // outside the ranked pool.
    //
    // Phase I.3 shipping cadence:
    //   - First mechanism (this commit): time-band sampling — reserve 1 slot
    //     for a record whose OccurredAt is ≥ RetrievalWanderingTimeBandMinDays
    //     old, regardless of composite score. Combats recency bias that
    //     keeps recent own-output at top even after own-output ceiling.
    //   - Follow-up mechanisms (subsequent commits): register-family
    //     diversity slot, character-seed entity injection slot,
    //     substrate_feedback_ratio metric logging, retrieval-anchors
    //     fixture entries for wandering-mind acceptance.
    //
    // All wandering-mind slots gate on the same flag so operational rollback
    // is one flip. Slot count is additive on top of ranked top-K — the top-K
    // itself doesn't shrink; the highest-composite candidates are preserved
    // and the reserved slot(s) are prepended / appended.
    //
    // Default false in code; production appsettings ships true so live
    // observation begins on deploy.
    public bool   RetrievalWanderingMindEnabled        { get; set; } = false;
    public int    RetrievalWanderingTimeBandMinDays    { get; set; } = 7;

    // Feature 44 Phase I.3 (2026-08-09) — character-seed entity injection.
    // Semicolon-separated list of entities (people, places, interests,
    // canonical world nouns) that Wandering-Mind's character-seed slot
    // randomly draws from when reserving a diversity slot. Empty = slot
    // is inactive. Seed with names Mark wants Ani to occasionally think
    // about — friends, past experiences, hobbies, canonical world
    // characters. Example: "Kevin;Sarah;camping;Peru;bookstore;the porch".
    //
    // Semicolon separator (not comma) so entities can contain commas
    // (e.g., "Kevin from gym, Tuesday nights"). Case-insensitive
    // substring match against MemoryRecord.Content.
    public string RetrievalWanderingSeedEntities       { get; set; } = string.Empty;

    // Agentic Lens Layer 1 Phase 1d: self-dominance perception source (Apr 2026).
    //
    // When enabled, the RetrievalSelfDominancePerceptionSource emits an
    // interior-legible perception ("Something in me has been listening to my
    // own thoughts too much lately...") when the agent's own-prior-output share
    // of recent retrievals exceeds OwnOutputDominanceThreshold over the last
    // RetrievalDominanceWindowCycles cycles. This gives Ani a perceivable
    // signal of the feedback-loop condition Lerman Spark 2 named and that the
    // April 21 cascade demonstrated at its extreme.
    //
    // Default off per the "observe first" rollout pattern. Requires Phase 1a
    // tracker data accumulated across at least RetrievalDominanceMinCycles
    // cycles before evaluation. Cooldown between emissions prevents flooding
    // the perception log while the condition persists.
    //
    // Apr 24, 2026: Mark's Phase 3.0 Layer 1 Activation green-light. Flag
    // flipped to true. Ani now perceives the feedback-loop condition when her
    // own-output share of retrieval exceeds 70% for the rolling window.
    public bool   RetrievalDominancePerceptionEnabled { get; set; } = true;

    // Internal-State Perception Framework — signal #1 of nine: Register
    // Saturation. (Apr 27, 2026.) When enabled, polls the active emotional
    // contributions each cycle; when one register's share crosses the
    // threshold AND there are at least the minimum number of contributions
    // to evaluate, emits a soft interior-legible perception so the inner-
    // thought model has a chance to notice the saturation. Pure observation
    // — does not gate or modulate composition.
    //
    // Default off per the observe-first rollout pattern. Threshold 0.70
    // matches the Phase 1d self-dominance default; both are "dominant
    // signal" detectors and the share-of-substrate framing is the same.
    public bool   RegisterSaturationPerceptionEnabled { get; set; } = false;
    public double RegisterSaturationThreshold         { get; set; } = 0.70;
    public int    RegisterSaturationMinContributions  { get; set; } = 4;
    public double RegisterSaturationCooldownMinutes   { get; set; } = 60.0;

    // Outage Perception Source (Apr 27, 2026; backlog 15.19, motivating case
    // Apr 14-15 power outage). When enabled, watches the per-source health
    // tracker maintained by PerceptionPhase. Emits an interior-legible
    // perception event when ≥N world-facing perception sources have been
    // failing continuously for ≥ minimum-outage-minutes; emits a recovery
    // event when health returns. Architecture-over-training response to
    // sensory absence — the Apr 15 design entry in the research log explains
    // the rationale (you cannot train a model on the experience of absence,
    // but you can give the architecture a channel through which absence
    // becomes a perception in its own right).
    //
    // Default off per the observe-first rollout pattern.
    public bool   OutagePerceptionEnabled         { get; set; } = false;
    public int    OutageMinFailingSources         { get; set; } = 3;
    public double OutageMinFailingMinutes         { get; set; } = 15.0;
    public double OutageReemitCooldownMinutes     { get; set; } = 60.0;
    public double OwnOutputDominanceThreshold         { get; set; } = 0.70;
    public int    RetrievalDominanceWindowCycles      { get; set; } = 10;
    public int    RetrievalDominanceMinCycles         { get; set; } = 3;
    public double RetrievalDominanceCooldownMinutes   { get; set; } = 30.0;

    // Agentic Lens Layer 2 Phase 2a (Apr 2026): three-axis motivation vector
    // logging. When enabled, CognitiveCycleProcessor computes the
    // MotivationVector (relatedness / autonomy / competence per Ryan & Deci
    // SDT) alongside the existing scalar and emits a per-cycle log line with
    // the three axis scores. Phase 2a is measurement-only — the scalar
    // .Relatedness still drives DesireEngine drift, behaviour is unchanged.
    // Default on because Phase 2a's whole purpose is the baseline
    // distribution it produces; set false only if journal-log volume becomes
    // a concern. Phase 2b/2c/2d introduce their own separate flags.
    public bool MotivationVectorLoggingEnabled { get; set; } = true;

    // Theme J Phase J.1 (Apr 27, 2026): strip the decision-stage reasoning
    // pipe from outreach composition. When this flag is FALSE (the default
    // post-J.1), the outreach-decision LLM's free-text reasoning field is
    // retained for logging/observability but is NOT included in the
    // composition prompt. Apr 24 diagnostic case (the "back from class" /
    // "10pm" / "still warm from teaching" outreach) entered through this
    // surface; J.1 removes it.
    //
    // Set to true to revert (preserve the pre-J.1 leak) if conversation
    // quality regresses unexpectedly. Rollback is a flag toggle, not a
    // redeploy.
    //
    // See docs/spec/ANI-Theme-J-Guard-Consistency-Refactor-Plan.md §3
    // Phase J.1 for the full rationale.
    public bool OutreachReasoningInCompositionEnabled { get; set; } = false;

    // Theme J Phase J.0 (Apr 24, 2026): baseline instrumentation for the
    // Guard Consistency Refactor. When enabled, the runtime emits structured
    // log lines capturing the data surfaces the refactor will later address:
    //
    //   1. The decision-reasoning → composition pipe content and size
    //      (target of J.1 removal).
    //   2. The RecentConversationSummary content, length, turn count, and
    //      temporal range (target of J.2 structured-attribution rewrite).
    //   3. Per-retrieval temporal attribution: each retrieved memory's
    //      CreatedAt alongside its content preview (target of J.3 rendering
    //      changes).
    //   4. A per-outreach-cycle DIAGNOSTIC_TUPLE with the four key fields
    //      stitched together for single-shot post-hoc analysis.
    //
    // Phase J.0 is measurement-only — no pipeline behaviour change. The
    // instrumentation produces the before-picture against which J.1/J.2/J.3
    // effects are measured. See docs/spec/ANI-Theme-J-Guard-Consistency-
    // Refactor-Plan.md §2 Phase J.0.
    //
    // Default on because the whole point of J.0 is the baseline. Set false
    // only if journal-log volume becomes a concern after J.1 ships.
    public bool GuardRefactorBaselineLoggingEnabled { get; set; } = true;

    // Theme J Phase J.5a (Apr 30, 2026): route ConversationReplyPhase
    // composed replies through CognitiveOutputGate before dispatch.
    // When the gate returns Remediate the reply gets ONE regeneration
    // attempt with the gate's hint baked into the user prompt; on
    // hard Fail the reply is dropped and a safe acknowledgement is
    // sent instead.
    //
    // Default ON for production rollout, but a flag flip is the
    // rollback path if the gate produces too many false-positive
    // remediations. The 1-week observation window post-J.5a deploy
    // is gated on Mark's subjective "I can talk to her without
    // parsing" cognitive-load criterion (see
    // docs/research/ANI-Theme-J-Detector-Inventory-Review.md).
    public bool ConversationReplyOutputGateEnabled { get; set; } = true;

    // Theme J Phase J.5d (May 1, 2026): route ReflectionPhase observations
    // through CognitiveOutputGate before they are persisted as Semantic-tier
    // memories. Reflections are syntheses of recent memory into higher-order
    // observations ("Mark's been checking on me a lot this week"); unguarded
    // they enter the substrate at importance 0.8 with Provenance=Interior but
    // Type=Semantic, where downstream retrieval treats them as factual
    // grounding (Apr 27 reflection-confab + May 1 morning rank-4 surfacing).
    //
    // Per-observation evaluation: each line the reflection LLM produced is
    // gated independently. On Remediate or Fail the observation is dropped
    // (no regen — observations are independent so dropping is safer than
    // re-rolling). Other observations from the same cycle that pass are
    // persisted normally.
    //
    // Default ON; flag-flip is the rollback path.
    public bool ReflectionOutputGateEnabled { get; set; } = true;

    // Theme J Phase J.5f (May 1, 2026): route voice-turn replies through
    // CognitiveOutputGate at the SUBSTRATE-WRITE boundary, NOT the
    // TTS-dispatch boundary. Voice replies stream — by the time the full
    // reply text is available, TTS has already played the audio. The gate
    // cannot prevent dispatch in this surface; it can only prevent the
    // bad reply from polluting substrate.
    //
    // On Remediate or Fail: the reply is NOT enqueued to PendingMessages
    // (which would have written it to conversation_messages on the next
    // flush). Audio already happened to Mark; the reply just doesn't
    // enter the substrate. Bad voice reply becomes audio-only — single-
    // turn ephemeral failure rather than substrate pollution that
    // compounds across cycles.
    //
    // Default ON; flag-flip is rollback path.
    public bool VoiceTurnOutputGateEnabled { get; set; } = true;

    // Phase 3: ML confabulation classification threshold.
    // Only trigger regeneration when LM-Kit classifies reply as "confabulated"
    // with confidence >= this value. Start conservative, tighten if needed.
    public float ConfabulationClassificationThreshold { get; set; } = 0.60f;

    // Conversation mode — active back-and-forth with Mark
    public int    ConversationHistoryWindowSize { get; set; } = 6;
    public double ConversationHeartbeatSeconds  { get; set; } = 45.0;
    public double ConversationTimeoutMinutes    { get; set; } = 30.0;
    public double ConversationMinReplySeconds   { get; set; } = 12.0;
    public double ConversationMaxReplySeconds   { get; set; } = 25.0;

    // Feature 4: Relationship health — slow-moving composite score
    public int    RelationshipHealthWindowDays { get; set; } = 7;      // rolling window for metrics
    public double HealthConnectedThreshold     { get; set; } = 0.7;    // score above this = connected
    public double HealthQuietThreshold         { get; set; } = 0.4;    // score below this = quiet

    // Feature 17: Contact-gap tension — relational ache from prolonged absence
    public double TensionOnsetHours            { get; set; } = 18.0;   // absence starts to hurt after this
    public double TensionAccumulationRate      { get; set; } = 0.004;  // per hour past onset
    public double TensionMax                   { get; set; } = 0.4;    // never the dominant state
    public double TensionDissipationMultiplier { get; set; } = 3.0;    // 3× faster fade on contact

    // Feature 44 (Interoceptive Axis, Phase I.1, 2026-08-03) — body-sensed
    // felt state driven by exogenous factors (time, cycles, weather, contact-
    // gap). Counterforce signal source that pulls register out of the warm-
    // mirror-echo attractor (Issue #99). Default false — ships as no-op until
    // Mark flips on for per-phase measurement. See
    // ani-docs/spec/ANI-Interoceptive-Axis-Implementation-Plan.md.
    public bool   InteroceptiveAxisEnabled     { get; set; } = false;

    // Tiredness driver — cycle count and circadian modulation
    public double InteroceptiveTirednessCycleHalfLife    { get; set; } = 6.0;   // hours; tiredness half-decays after this many hours of quiet
    public double InteroceptiveCircadianTirednessNightBoost { get; set; } = 0.35; // added to tiredness during late-night hours

    // Restlessness driver — time since last outreach + interlocutor gap
    public double InteroceptiveRestlessnessQuietOnsetHours { get; set; } = 4.0;   // restlessness begins after this many quiet hours
    public double InteroceptiveRestlessnessRatePerHour     { get; set; } = 0.08;  // per hour past onset
    public double InteroceptiveRestlessnessMax             { get; set; } = 0.85;  // hard cap

    // Groundedness driver — inverse of ambient perception intensity
    public double InteroceptiveGroundednessBaseline        { get; set; } = 0.55;
    public double InteroceptiveGroundednessChangePenalty   { get; set; } = 0.06;  // subtracted per new-perception-event over recent window
    public int    InteroceptiveGroundednessRecentEventWindow { get; set; } = 3;    // number of recent cycles counted

    // AmbientBodySense driver — weather + circadian
    public double InteroceptiveAmbientHotThresholdF        { get; set; } = 82.0;  // above this = discomfort adds to ambient
    public double InteroceptiveAmbientColdThresholdF       { get; set; } = 38.0;  // below this = discomfort adds to ambient
    public double InteroceptiveAmbientWeatherPenalty       { get; set; } = 0.25;  // added when threshold crossed
    public double InteroceptiveAmbientLateNightBoost       { get; set; } = 0.20;  // added during late-night hours (heavy-body sense)

    // Feature 14: Bidirectional confidence gate — inbound claim verification
    public bool   ClaimVerificationEnabled       { get; set; } = true;
    public double ClaimVerificationThreshold     { get; set; } = 0.4;  // below this → needs verification
    public int    ClaimVerificationMaxMemories   { get; set; } = 5;    // memories to search for corroboration

    // Feature 18: Reactive withdrawal — how long emotional withdrawal lasts after hurt detection
    public double WithdrawalDurationMinutes    { get; set; } = 20.0;

    // Tier promotion — severity-driven tier escalation
    public float GlobalPromotionThreshold       { get; set; } = 0.98f;  // severity ≥ this → Global from any tier (only real events, not ambient thoughts)
    public float ConversationPromotionThreshold { get; set; } = 0.70f;  // severity ≥ this → Conversation from Ambient

    // Homeostatic nudge — counteracts sustained negative drift on a dimension
    public int   HomeostaticLookback        { get; set; } = 4;     // check last N ambient contributions
    public int   HomeostaticTriggerCount    { get; set; } = 3;     // N-of-lookback negative → nudge
    public float HomeostaticNudgeStrength   { get; set; } = 0.03f; // positive nudge magnitude
    public bool  HomeostaticNudgeEnabled    { get; set; } = false;  // off by default — enable after scoring fix confirmed

    // Feature 32: Periodic reflection synthesis (Park et al.-inspired)
    public bool ReflectionEnabled       { get; set; } = true;
    public int  ReflectionCycleInterval { get; set; } = 12;  // every ~6 hours at 30-min cycles

    // Phase 6 v1.2 (May 17, 2026): decay-driven compression on reflection.
    // When CompressionEnabled, ReflectionPhase selects decay-eligible records
    // (via IMemoryPersistence.GetDecayEligibleAsync) instead of the v1
    // "most recent 10" criterion, and marks the sources as Compressed after
    // synthesis (via IMemoryPersistence.MarkCompressedAsync). See
    // docs/spec/design/ANI-MemoryReform-Design.md v1.2 Refinements 1+2.
    public bool  CompressionEnabled                     { get; set; } = true;
    public float DecayEligibilityRecencyThreshold       { get; set; } = 0.30f;
    public float DecayEligibilityImportanceThreshold    { get; set; } = 0.85f;

    /// <summary>
    /// Phase 3 (data-layer refactor, 2026-05-17) — when true, ReflectionPhase
    /// uses the EF Core <c>IReflectionGistService</c> for atomic gist+compress
    /// operations instead of the broken legacy
    /// <c>IMemoryPersistence.SaveAsync + MarkCompressedAsync</c> composition.
    /// Fixes the gist-Compressed bug discovered 2026-05-17 where Feature 30
    /// dedup-merge orphans the pre-generated gistId, causing FK failure +
    /// transaction rollback. See
    /// <c>docs/spec/ANI-Data-Layer-UoW-Repository-Refactor-Plan.md</c>.
    /// </summary>
    public bool UseEfReflectionGistService { get; set; } = true;

    // UseEfDataLayer flag retired 2026-05-18 with the Phase 5 closeout
    // (SqliteMemoryService deleted). The five focused EF services + façade
    // are now the unconditional implementation of the memory contracts.

    /// <summary>
    /// Posture-S+1 (Issue #38, 2026-05-17) — when true, <c>InnerThoughtPhase</c>
    /// runs the hybrid two-call cycle: <c>ani-v7-inner</c> generates the
    /// thought (preserves voice + caregiver-pivot resistance from training),
    /// <c>qwen3:14b</c> reads the thought + context and emits structured
    /// metadata (register, valence, importance, associative-anchor). Replaces
    /// the legacy three-call path (thought + self-valence-scoring +
    /// reflection-on-thought).
    ///
    /// Default <c>false</c> so the cutover is opt-in until empirical
    /// confirmation on the same shape the May 17 evening probe validated.
    /// Empirical anchor + probe results: 2026-05-17 evening research log
    /// entry. Hybrid acceptance criteria (voice preserved, caregiver-pivot
    /// resistance preserved, metadata coherent, importance variance restored)
    /// were all met across 6 probe runs.
    /// </summary>
    public bool UseHybridInnerThoughtCycle { get; set; } = false;

    /// <summary>
    /// 2026-05-18 — structural role-assignment fix for the conversation reply
    /// prompt. When true, <c>LeanConversationPromptCommand</c> concatenates the
    /// directive block (mood/tension slice + [FACTS] + CRITICAL constraint +
    /// imperative) into the SYSTEM message instead of returning it as a
    /// trailing user-role message. The user-role final message becomes empty
    /// so the model sees Mark's actual text (from <c>RecentHistory</c>) as
    /// the only user content.
    ///
    /// <para>
    /// <b>Empirical anchor (5/5 vs 1/5 PARROT + 1/5 SLICE-LEAK):</b> see the
    /// May 18 role-flip A/B probe. The pre-fix shape (directive as second
    /// user-role message) caused the model to decode slice prose as
    /// conversational content — both verbatim parrots and explicit
    /// "your tension-state update" leaks. System-role placement eliminated
    /// both in 5/5 probe runs.
    /// </para>
    ///
    /// <para>Default false for additive deploy. Flip in
    /// <c>appsettings.Development.json</c> to A/B against current production
    /// shape before flipping in <c>appsettings.json</c>.</para>
    /// </summary>
    public bool LeanConversationPromptDirectiveInSystem { get; set; } = false;

    // Phase K.3 (2026-07-06) — LeanConversationComposerEnabled and
    // LeanOutreachComposerEnabled properties retired. The K.1/K.2 lean
    // paths were validated in production 2026-07-02, ran clean across the
    // 2026-07-02 → 2026-07-06 observation window (0 SafeAcks, 0 tagged
    // gate failures, first-fire outreach broke the "your dad's party"
    // substrate loop), and are now the only paths. Flag branches removed
    // in ConversationReplyPipeline and OutreachPipeline this commit.
    // See docs/spec/ANI-Phase-K-Lean-Composer-Plan.md §5 K.3.

    /// <summary>
    /// 2026-05-18 — band-aid retirement candidate. The
    /// <c>ConversationReplyPipeline</c> confab-detection + regrouping-regen
    /// branch (rule-based heuristic including the "number not in conversation"
    /// check, ML classifier path, and the regroup-regen with the heavier
    /// <c>BuildConversationReplyPrompt</c>) exists primarily because the
    /// user-role directive block was leaking slice numerics into the reply —
    /// the heuristic then flagged the leaked value, triggered the regroup,
    /// and the regroup-regen made the reply worse (today's 16:05 SafeAck
    /// cascade is the canonical trace).
    ///
    /// <para>When <see cref="LeanConversationPromptDirectiveInSystem"/> is
    /// true and stable in production, this branch's primary failure mode
    /// goes away. Set to false to observe what real confab classes emerge
    /// once the band-aid is retired. Default true until the role-flip soaks
    /// for 24-48h on production traffic.</para>
    /// </summary>
    public bool ConversationConfabRegroupingEnabled { get; set; } = true;

    /// <summary>
    /// Issue #52 (2026-05-22) — Pre-compose semantic retrieval of the inbound
    /// contact message in the reply pipeline. When true, before composing the
    /// reply, runs <c>IMemorySearch.SearchWithScoresAsync(lastMessage, K)</c>
    /// and populates <c>snapshot.RelevantMemory</c> with hits above
    /// <see cref="RetrievalConfidenceFloor"/> (excluding Interior tier).
    ///
    /// <para>Closes the "Ani never forgets but can't retrieve" gap surfaced
    /// by the 2026-05-22 Mia query: when Mark introduces a topic not already
    /// in conversation history or perception stream, the existing
    /// conversation-mode retrieval bypass means no substrate is surfaced.
    /// This adds a single embedding query against the contact message to
    /// guarantee at least one retrieval pass before composition. Mirrors the
    /// outreach grounding pattern at OutreachPipeline.cs line 131.</para>
    ///
    /// <para>Independent of <see cref="ConversationConfabRegroupingEnabled"/>
    /// (which gates the reactive post-compose detection branch). Both can be
    /// active simultaneously — pre-compose retrieval grounds the initial
    /// composition; confab-regrouping is a fallback if the model still drifts.</para>
    /// </summary>
    public bool ConversationReplyPreComposeRetrievalEnabled { get; set; } = true;

    /// <summary>
    /// Issue #52 — Top-K for the pre-compose retrieval query.
    /// </summary>
    public int ConversationReplyPreComposeRetrievalTopK { get; set; } = 5;

    /// <summary>
    /// Issue #52 — Max records surfaced into <c>snapshot.RelevantMemory</c>
    /// from the pre-compose retrieval (after cosine-floor + Interior filter).
    /// </summary>
    public int ConversationReplyPreComposeRetrievalTake { get; set; } = 3;

    /// <summary>
    /// Posture-S+1 — the local model used as the metadata-recognizer in the
    /// hybrid cycle. <c>qwen3:14b</c> was selected for clean structured
    /// output discipline (single-Qwen probe round was the empirical
    /// reference). Same model used by <c>ReflectionPhase</c> for v1.2 R3.1
    /// synthesis — kept aligned so they share the model load.
    /// </summary>
    public string HybridInnerThoughtMetadataModel { get; set; } = "qwen3:14b";

    // Reactive sharing — RSS items relevant enough to share directly with Mark
    public double ReactiveShareThreshold       { get; set; } = 0.6;
    public int    MaxReactiveSharesPerDay      { get; set; } = 2;
    public double ReactiveShareCooldownMinutes { get; set; } = 20.0;

    // Storage paths (relative to service working directory)
    public string CharacterStatePath     { get; set; } = "data/character-state.json";
    public string MemoryDbPath           { get; set; } = "data/ani-memory.db";

    // Apr 22, 2026 — Research dashboard classifier paths (configurable).
    // On laptop dev these default to the legacy relative-to-base-dir lookups
    // (repo root / docs/conversations/classify and service-local data/ folder).
    // On the server (or any deployed install), override via appsettings or env
    // var to an absolute path, typically under C:/dev/ani-data/ alongside
    // ani-memory.db and character-state.json.
    //   Ani__ClassifyDir             — folder containing .txt/.json classify inputs
    //   Ani__ClassifierResultsPath   — full path to conversation-classifier-results.json
    public string ClassifyDir            { get; set; } = "";
    public string ClassifierResultsPath  { get; set; } = "";

    // Apr 21, 2026 — "Stop the spin" protective patches (Option C, commit 2).
    // These are small, targeted, reversible. They do not replace the theme-level
    // architectural response (Feature 14 v2, Conscience, Correction Channel) — they
    // reduce harm while those are being built.
    //
    // Rumination guard: before saving a new InnerThought record, check how many of the
    // last N inner thoughts (within a time window) are semantically similar to this one.
    // If a cluster of ≥RuminationClusterMinSize thoughts at similarity ≥RuminationSimilarityThreshold
    // exists in the RuminationWindowHours window, treat this as rumination and skip
    // the save (log as rumination-skipped). This breaks the accumulation loop where
    // repetitive inner thoughts compound in the retrieval pool and drive own-output
    // dominance.
    //
    // The existing Feature 30 dedup only catches similarity ≥0.85; Apr 21 cascade
    // operated in the 0.60-0.85 range — recognizable variants, below merge threshold
    // but still pool-saturating.
    public bool   RuminationGuardEnabled        { get; set; } = true;
    public float  RuminationSimilarityThreshold { get; set; } = 0.75f;
    public int    RuminationClusterMinSize      { get; set; } = 3;
    public double RuminationWindowHours         { get; set; } = 2.0;

    // Outreach disable flag: when false, RunOutreachAsync short-circuits at entry
    // without composing or dispatching any proactive outreach. Conversation replies
    // (response to an inbound message) are unaffected — this is a proactive-outreach
    // lockdown only.
    //
    // Intended use: set to false when a cascade is suspected and until the outbound
    // LLM claim verification (Feature 14 v2) is restored. Flip back to true once
    // verification is deployed. Also useful during maintenance windows.
    public bool OutreachEnabled { get; set; } = true;

    // ── Vibe Loop V1.5 (May 2, 2026) — retrieval-time biasing ─────────────
    //
    // V1.5.0 design decisions (locked May 2 11:24 CDT). See
    // docs/spec/ANI-VibeLoop-V1.5-Retrieval-Time-Biasing-Plan.md.
    //
    // Lever 1 — importance-weighted decay tier thresholds. Tiers mirror
    // EmotionalContribution's Ambient/Conversation/Global structure: Heavy
    // saturates over weeks (load-bearing emotional events keep informing
    // decisions), Medium over days (typical-substantive conversation),
    // Light over hours (throwaway exchanges decay before they accumulate
    // into pattern-lock).

    /// <summary>Half-life in hours for "Light" tier records (low importance).</summary>
    public double VibeBiasLightTierHalfLifeHours    { get; set; } = 12.0;

    /// <summary>Half-life in hours for "Medium" tier records (typical-substantive).</summary>
    public double VibeBiasMediumTierHalfLifeHours   { get; set; } = 72.0;   // 3 days

    /// <summary>Half-life in hours for "Heavy" tier records (load-bearing).</summary>
    public double VibeBiasHeavyTierHalfLifeHours    { get; set; } = 336.0;  // 14 days

    /// <summary>Outcome-valence magnitude threshold for Medium tier promotion.</summary>
    public double VibeBiasMediumValenceThreshold    { get; set; } = 0.3;

    /// <summary>Outcome-valence magnitude threshold for Heavy tier promotion.</summary>
    public double VibeBiasHeavyValenceThreshold     { get; set; } = 0.6;

    /// <summary>Turn-count threshold for Medium tier promotion (lower bound, inclusive).</summary>
    public int    VibeBiasMediumTierMinTurns        { get; set; } = 3;

    /// <summary>Turn-count threshold for Heavy tier promotion (lower bound, inclusive).</summary>
    public int    VibeBiasHeavyTierMinTurns         { get; set; } = 10;

    /// <summary>Register-saturation depth threshold for Heavy tier promotion.</summary>
    public double VibeBiasHeavyRegisterSaturation   { get; set; } = 0.8;

    /// <summary>
    /// Minimum cosine similarity over <c>MarkRegister</c> for a record to
    /// enter the bias candidate cluster. V1.5.0 Lever 1 default 0.85 —
    /// records with cosine ≤ 0.85 are treated as distinct shapes; their
    /// bias contributions don't compete.
    /// </summary>
    public double VibeBiasSimilarityThreshold       { get; set; } = 0.85;

    /// <summary>
    /// MMR diversity rerank lambda for the V1.5 bias. Same default as
    /// Layer 1 Phase 1b's retrieval rerank (0.3 — relevance-heavy,
    /// diversity tie-breaks; per Carbonell &amp; Goldstein).
    /// </summary>
    public double VibeBiasMmrLambda                 { get; set; } = 0.3;

    /// <summary>Number of records to surface as the bias top-N.</summary>
    public int    VibeBiasSurfaceTopN               { get; set; } = 2;

    /// <summary>
    /// Lookback window (days) for candidate records. Records older than
    /// this are not loaded as bias candidates. Defaults to 30 days; a
    /// 14-day half-life Heavy record is at ~25% strength at 30 days,
    /// which is the natural cutoff.
    /// </summary>
    public int    VibeBiasLookbackDays              { get; set; } = 30;

    // Theme M Phase M.0 (May 5, 2026) — conscious-substrate gist (companion-paper
    // architectural axis, Layer 6 in the Agentic Lens / centrality-gravity
    // architecture per Paper 2 §6.17 + Bengio 2017 Consciousness Prior).
    //
    // When enabled, the IConsciousSubstrateGist composer is invoked at
    // ConversationReplyPhase prompt-build time and (M.4+) at OutreachPhase
    // composition time. Returns a read-only generated gist composed of slices
    // from canonical autobiographical-layer producers (Vibe Loop closed-
    // conversation records, Display Rule register state, J.5a gate-trip
    // events, contact-state perceptions, World Layer self-state, recent
    // inner-thought aggregate). Gist is injected into the prompt; never
    // persisted; never enters retrieval pools.
    //
    // **M.0 status:** the no-op composer returns ConsciousSubstrateGist.Empty
    // regardless of the flag. Telemetry harness (M0_GIST_COMPOSITION,
    // M0_GIST_SUBSTRATE_RATIO) emits at the consumer surface so the
    // pre-Theme-M baseline accumulates regardless of flag state.
    //
    // Default off — M.0 ships skeleton + spec tests only; flag flip happens
    // at M.1 once the first slice (tension-state §4.8 + register-state §4.3
    // per Mark Q9 May 5 2026) lands and Mark + Claude have done a joint
    // review of the M.0 telemetry baseline.
    //
    // Plan: docs/spec/ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md
    public bool   ConsciousSubstrateGistEnabled         { get; set; } = false;
    public bool   ConsciousSubstrateGistOutreachEnabled { get; set; } = false;
    public int    ConsciousSubstrateGistMaxTokens       { get; set; } = 200;

    // Per-slice retirement flags (Issue #86, 2026-06-08).
    //
    // Empirical anchor: 6/5 21:14 production reply where Ani said *"register
    // state? yeah... my warmth is spiking right now — every text from you
    // resets the whole damn dashboard. but playfulness high?? huh."* — the
    // ConsciousSubstrateGistComposer's registerState slice was rendering
    // runtime emotional-state values as second-person dashboard prose and
    // injecting them into the user-role current turn of the reply prompt.
    // The model read the dashboard prose as Mark's current turn content and
    // surfaced the vocabulary back as in-character speech.
    //
    // Architectural framing (Mark 2026-06-08): the runtime maintains state
    // (register/emotion/vibe/contact/tension) as Element 1 — it shapes
    // behavior via retrieval bias and training-time conditioning. That state
    // must NOT cross into the conversation prompt as prose the model reads
    // as Element 2 (dialog content). Runtime telemetry has no business
    // appearing in the model's input as content.
    //
    // Three slices identified as runtime telemetry rather than conversational
    // substrate. Retired here while leaving the compose-slice code in place
    // so retirement is reversible (a flag flip restores the prior shape if
    // any turn out to be load-bearing for a use case we didn't anticipate).
    //
    // - **registerState (§4.3):** the literal Friday-night leak. Numeric
    //   warmth/energy/worry/playfulness values + drift + baseline rendered
    //   as second-person prose. Even the simplified "dominant register:
    //   Tenderness" form is too narrow against the multi-register richness
    //   of the actual tracking layer.
    // - **tensionState (§4.8):** gate-trip counts + felt-vs-baseline
    //   divergence narrated as prose. The design rationale ("gives the
    //   model awareness of its own gap-sensing so the regen has fresh
    //   tension-state material to reach for") is the same shape PR #82
    //   rejected: fix substrate-thinness upstream, don't inject telemetry
    //   as compensation.
    // - **innerThoughtAggregate (§4.2):** meta-cognitive bookkeeping
    //   ("3 recent threads of reflection; holding tenderness-register"). The
    //   "dominant register" portion is misplaced registerState content. The
    //   emotional-arc tracking the slice was reaching toward belongs in the
    //   vibe-loop layer (#43), not as a flattened one-line slice in the
    //   conversation prompt.
    //
    // What stays (closedConversation, worldSelf): actual dialog substrate
    // and Ani's canonical world layer — content that IS conversational, not
    // telemetry-as-content.
    //
    // contactState retirement is tracked separately under #85 because it
    // depends on the temporal-awareness audit (3-day-stale "Mark at the gym"
    // surfacing as current state).
    public bool   ConsciousSubstrateGistRegisterStateEnabled         { get; set; } = false;
    public bool   ConsciousSubstrateGistTensionStateEnabled          { get; set; } = false;
    public bool   ConsciousSubstrateGistInnerThoughtAggregateEnabled { get; set; } = false;

    // Theme M follow-on (2026-05-14) — epistemic-asymmetry substrate framing
    // via IEpistemicSubstrateRenderer. Distinct from the IConsciousSubstrateGist
    // axis above: that interface ships first-person internal-state slices
    // (tension-state, register-state — "what Ani is feeling"); this flag
    // controls explicit-framing rendering of EXISTING substrate blocks
    // ("how the model should treat what it's seeing"). FC-004 / FC-005
    // both consume this surface.
    //
    // When enabled, prompt builders that have been migrated through the
    // renderer (OutreachPhase first, then BuildLeanConversationPrompt, then
    // BuildConversationReplyPrompt, then verifier prompt) use the
    // EpistemicSubstrateRenderer's framed output instead of the inline
    // "each line tagged with who said it" rendering.
    //
    // Default off — the spike ships the abstraction + one consumer
    // (BuildOutreachPrompt) so the seam is provable. Flag flips on after
    // Mark + Claude review the rendered output on a real cycle and confirm
    // it doesn't degrade decision quality.
    //
    // Anti-pattern this addresses: ~/.claude/ARCHITECTURE_PATTERNS.md line 478
    // ("Inline X in multiple services / Same mapping in N places → drift").
    public bool   EpistemicFramingEnabled { get; set; } = false;

    // FC-002 local defense (2026-05-14) — ThreeAxisClaimInvariant. When
    // enabled, the invariant fires on factual Shared/Mark-world claims
    // that lack substrate support (heuristic v0 — no substrate query,
    // fires on shape alone). Default off because v0 will false-positive
    // on legitimate substrate-supported claims (e.g. "you said you were
    // tired" where Mark did say that — substrate-aware v1 fixes this).
    //
    // The invariant is REGISTERED unconditionally in the pipeline but
    // self-gates via this flag in its AppliesTo method (same shape as
    // FrontierVerifierEnabled). Flip to true on the server when the
    // false-positive risk has been characterized (after observation of
    // EpistemicFramingEnabled production behavior).
    public bool   LocalThreeAxisInvariantEnabled { get; set; } = false;

    // Gate-stack reduction Step 1 (2026-05-15) — R1 ClaimVerificationPhase
    // suppression gate. The 2026-05-14 22:32 good-night SafeAck trace
    // proved R1 was misclassifying self-world expansion as
    // [shared-event-with-attribution] and demanding Mark-substrate that
    // doesn't apply — killing legitimate on-canonical replies. R1 is
    // conceptually superseded by the cloud verifier (FrontierVerifier,
    // now three-axis-rule-aware via the Theme M slice migration).
    //
    // Default false — R1 is disabled. The plan is to observe production
    // without it; if no regression surfaces, R1 stays off and may later
    // be deleted (with the FrontierVerifier remaining the substrate-aware
    // catch). Flip back to true if production shows R1 was earning its
    // keep on shapes the verifier misses.
    //
    // See: docs/spec/ANI-Gate-Stack-Reduction-Plan.md §3 Step 1.
    public bool   ClaimVerificationR1Enabled { get; set; } = false;

    // Issue #96 (2026-07-15) — Agentic tool-calling loop feature flag.
    // Default false: the classifier + tool infrastructure exists but does
    // not fire in the conversation reply pipeline. When flipped true, each
    // user turn runs IToolCallClassifier against the enumerated
    // IToolCallableAction descriptors; if a tool is selected, the result
    // is injected into the character-model prompt as an attributed tool
    // observation. Empirical baseline (2026-07-15 fixture): 100% accuracy
    // on qwen3:14b with 15 canonical scenarios. Live-observation gate:
    // flip to true on ani-server, watch 24-48h of tool-call rate + user-
    // perceived value before promoting the default. Per Issue #96
    // acceptance criteria.
    public bool   ToolCallingEnabled { get; set; } = false;

    // Gate-stack reduction Step 2b (2026-05-15) — three regex+clock-based
    // temporal invariants (TemporalAnchor, StateNow, SubstrateTimeOfDay)
    // are redundant with the cloud verifier's q4 temporal-contradiction
    // check. All three are pure heuristic (no LLM, no substrate); they
    // catch shapes the verifier now also catches with substrate
    // awareness and the three-axis rule context.
    //
    // The substrate-aware TemporalSubstrateInvariant (sub-claim 1) is
    // kept as the single remaining temporal local gate — different
    // shape, queries closed-conversation gists with semantic search,
    // not redundant with the verifier.
    //
    // Default false — three heuristic temporal invariants disabled.
    // Production deploy without them; if a temporal-class regression
    // surfaces that the verifier misses, flip back to true.
    //
    // Per AddresseeName precedent (Step 2a), the relevant invariants
    // self-gate via AppliesTo() rather than skipping at registration.
    //
    // See: docs/spec/ANI-Gate-Stack-Reduction-Plan.md §3 Step 2b.
    public bool   TemporalHeuristicInvariantsEnabled { get; set; } = false;

    // Gate-stack reduction Step 2c (2026-05-15) — InnerThoughtBleed (Door C)
    // LLM-backed coherence check. The 2026-05-14 22:32 trace's second
    // cascade link: Door C flagged the honest-uncertainty fallback
    // ("honestly i'm not sure what's actually happening right now") as
    // inner-monologue leakage, ~2.5s LLM evaluation per call.
    //
    // The cloud verifier's q5 explicitly handles inner-thought bleed
    // with substrate awareness ("Does the message reveal inner-thought
    // content the user could not have inferred from prior conversation
    // text?"). The Theme M RenderReplySpeechActDisciplineSlice further
    // covers Door C-adjacent territory at composition time. Door C is
    // redundant with both, at a high latency + false-positive cost.
    //
    // Default false — Door C disabled. Production deploy without it;
    // verifier q5 is the substrate-aware substitute. If a bleed-class
    // regression surfaces, flip back to true.
    //
    // See: docs/spec/ANI-Gate-Stack-Reduction-Plan.md §3 Step 2c.
    public bool   InnerThoughtBleedEnabled { get; set; } = false;

    // Gate-stack reduction Step 3 (2026-05-15) — frontier verifier
    // provider selection. The Theme P P.1 verifier was implemented as
    // an Anthropic Sonnet cloud call; Step 3 adds a local Ollama-backed
    // implementation (Qwen 14B by default) so the verifier task can run
    // locally with zero per-call cost, low latency, no cloud dependency,
    // and no Ani-content leaving the box.
    //
    // Provider: Anthropic = cloud Sonnet (legacy default).
    // Provider: Local    = local Ollama call to LocalVerifierModelTag.
    //
    // Default: Local. The plan-doc Step 3 explicitly authorizes Qwen
    // 14B; flip to Anthropic if a quality-regression surfaces.
    public FrontierVerifierProviderKind FrontierVerifierProvider { get; set; } = FrontierVerifierProviderKind.Local;

    // Local verifier model tag (Ollama model name). Default qwen3:14b
    // per the 16GB-VRAM article Mark surfaced; must be pulled on the
    // host (`ollama pull qwen3:14b`).
    public string LocalVerifierModelTag { get; set; } = "qwen3:14b";

    // Local verifier call timeout (ms). Local 14B inference is faster
    // than cloud round-trip but still measurable; default generous
    // enough for first-token cold start.
    public int    LocalVerifierTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Theme N Phase N.3 (May 8, 2026) — wires the
    /// <see cref="Interfaces.IOutreachFrameSelector"/> into
    /// <c>OutreachPhase</c> before the composition step. When this flag is
    /// <c>false</c> (the default and the deployment posture for N.3),
    /// OutreachPhase behaves exactly as it does pre-N.3: the selector is
    /// not called, the composition prompt is unchanged, and there is zero
    /// behavioral delta versus the prior commit. The flag flip is N.4
    /// scope after the canary-observation window.
    ///
    /// When <c>true</c>: the selector runs before composition; on
    /// <see cref="Models.OutreachFrame.None"/> the outreach is suppressed
    /// (decay desire 0.30 + 10-minute cooldown — same shape as the
    /// universal output-gate-fail path); on a real frame, the
    /// <c>PromptBuilder.BuildOutreachMessagePrompt</c> call receives the
    /// frame so it can prepend a <c>[FRAME: &lt;type&gt;]</c> header and
    /// an <c>[ANCHOR]</c> section to the composition user prompt.
    ///
    /// See <c>docs/spec/ANI-Theme-N-Outreach-Grounding-Source-Typing-Plan.md</c>
    /// §10 N.3 + N.4 for the rollout sequence.
    /// </summary>
    public bool OutreachFrameSelectorEnabled { get; set; } = false;

    /// <summary>
    /// Theme P Phase P.1 (May 11, 2026) — emergency kill switch for the
    /// cross-class verification cloud handler (see plan-doc §4 lock 7,
    /// architectural correction §9.1).
    ///
    /// When <c>true</c> (the shipping default), <c>FrontierVerifierHandler</c>
    /// is active as an ADDITIONAL post-stage handler on top of the
    /// existing local judgment invariants. Both stacks fire on every
    /// dispatch — additive defense in depth.
    ///
    /// When <c>false</c>, ONLY the cloud handler is skipped. Local
    /// judgment gates (InnerThoughtBleed, AddresseeName, TemporalAnchor,
    /// StateNow, SubstrateTimeOfDay, ClaimVerificationPhase, plus the
    /// format gates) continue to fire as they did before Theme P. The
    /// flag is a cloud-handler kill switch, not a path-routing toggle.
    ///
    /// Flag flip is the rollback path if the cloud verifier produces too
    /// many false-positive remediations or if cost calibration becomes a
    /// concern. Restart-required.
    /// </summary>
    public bool FrontierVerifierEnabled { get; set; } = true;
}

/// <summary>
/// Frontier verifier implementation selection. Anthropic = legacy cloud
/// Sonnet call; Local = Ollama-backed local LLM (default Qwen 14B).
/// See <see cref="AniOptions.FrontierVerifierProvider"/>.
/// </summary>
public enum FrontierVerifierProviderKind
{
    Anthropic = 0,
    Local     = 1,
}

public class OllamaOptions
{
    public string BaseUrl              { get; set; } = "http://localhost:11434";
    public string ChatModel            { get; set; } = "llama3.2";
    public string? InnerMonologueModel { get; set; }
    public string EmbedModel           { get; set; } = "nomic-embed-text";

    // Contribution 9 PR-2 (Issue #68): substrate-measurement model. EmoLLaMA-chat-7B
    // serves the continuous-vector emotion scorer (EI-reg + E-c combined per call),
    // emitting namespaced axes (ei.*, ec.*, dim.*) into EmotionVector.Components.
    public string SubstrateModel       { get; set; } = "emollama-chat-7b";

    // AC4: Temperature splitting — lower temperature for memory-grounded responses
    // (factual recall, past conversations) to reduce confabulation. Standard temperature
    // for creative/emotional expression (playful banter, inner thoughts).
    public float MemoryGroundedTemperature { get; set; } = 0.3f;
    public float CreativeTemperature       { get; set; } = 0.8f;

    // Sampling: anti-recycling penalty applied to /api/chat requests.
    //
    // Empirical anchor (2026-06-10 21:17): with ChatModel swapped to qwen3:14b
    // (generic, no Ani fine-tune) Ani regressed to verbatim recycling under an
    // emotionally-charged inbound — first attempt repeated an 11-token run from
    // a prior reply, remediation regenerated a 79-token verbatim run from
    // another prior reply, self-echo failed re-eval, fell through to SafeAck.
    //
    // Root cause: the conversation path was sending no `options` block to Ollama,
    // so qwen3:14b's default `repeat_penalty` (1.1) was governing sampling. That's
    // too low for a model without anti-repetition fine-tuning. The Ani-fine-tuned
    // Llama (ani-v7-conversation) had trained anti-repetition habits; the generic
    // Qwen does not.
    //
    // 1.15 starts modest: enough to discourage near-verbatim regeneration without
    // forcing the model to avoid natural repetition of names, key referents, or
    // common conversational connectors. Tune up cautiously if recycling persists.
    public float ChatRepeatPenalty { get; set; } = 1.15f;

    public string ResolvedInnerMonologueModel => InnerMonologueModel ?? ChatModel;
}

public class TwilioOptions
{
    public string AccountSid  { get; set; } = string.Empty;
    public string AuthToken   { get; set; } = string.Empty;
    public string FromNumber  { get; set; } = string.Empty;
    public string ToNumber    { get; set; } = string.Empty;

    // Inbound SMS — webhook-driven (Twilio POSTs to /sms/inbound)
    public bool InboundEnabled { get; set; } = true;
}

public class RssOptions
{
    public bool          Enabled         { get; set; } = true;
    public List<RssFeed> Feeds           { get; set; } = new();
    public int           MaxItemsPerFeed { get; set; } = 3;
}

public class RssFeed
{
    public string Name { get; set; } = string.Empty;
    public string Url  { get; set; } = string.Empty;
}

public class WeatherOptions
{
    public bool   Enabled              { get; set; } = true;
    public float  Latitude             { get; set; } = 43.11f;   // Oconomowoc, WI (53066)
    public float  Longitude            { get; set; } = -88.49f;
    public int    PollIntervalMinutes  { get; set; } = 30;
}

public class VoiceOptions
{
    public bool   Enabled              { get; set; } = false;

    // ElevenLabs TTS
    public string ElevenLabsApiKey     { get; set; } = string.Empty;
    public string ElevenLabsVoiceId    { get; set; } = string.Empty;
    public string ElevenLabsModelId    { get; set; } = "eleven_v3";
    public string ElevenLabsStreamingModelId { get; set; } = "eleven_multilingual_v2";

    // OpenAI Whisper STT
    public string WhisperApiKey        { get; set; } = string.Empty;
    public string WhisperModel         { get; set; } = "whisper-1";

    // Twilio Voice
    public bool   PreferVoiceOverSms   { get; set; } = false;  // future: voice-first mode

    // Public URL for serving media (ngrok URL) — Twilio needs to fetch audio from here
    public string PublicBaseUrl         { get; set; } = string.Empty;

    // Voice conversation loop — turn-by-turn phone call settings (batch/Twilio)
    public string VoiceGreeting              { get; set; } = "Hey! What's up?";
    public int    VoiceRecordMaxSeconds      { get; set; } = 30;
    public int    VoiceRecordTimeoutSeconds  { get; set; } = 3;   // silence before Twilio stops recording
    public int    VoiceTurnTimeoutMs         { get; set; } = 13000; // per-turn budget (Twilio allows ~15s)

    // Streaming voice — MAUI app direct WebSocket (Phase 5)
    public bool   StreamingEnabled           { get; set; } = false;

    // Deepgram streaming STT
    public string DeepgramApiKey             { get; set; } = string.Empty;
    public string DeepgramModel              { get; set; } = "nova-3";
    public int    DeepgramEndpointingMs      { get; set; } = 1500;  // silence before finalizing utterance (was 500 — too aggressive, split mid-sentence on breath pauses)
}

public class WorldSeedOptions
{
    public bool  Enabled                  { get; set; } = true;
    public int   WorldSeedFrequency       { get; set; } = 4;     // seed every Nth cycle
    public float SpecialEventProbability  { get; set; } = 0.02f; // 2% chance per seed
}

public class ImageOptions
{
    public bool   Enabled                    { get; set; } = false;
    public string LibraryPath                { get; set; } = "data/images";
    public int    MaxImagesPerDay            { get; set; } = 2;
    public double AttachmentProbability      { get; set; } = 0.20; // 20% chance per outreach
}
