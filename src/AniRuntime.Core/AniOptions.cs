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

    // AC1: Retrieval confidence thresholding — minimum cosine similarity for a memory
    // to be considered relevant. Below this, the memory is filtered out and the model
    // is told explicitly that no relevant memories exist (AC3: null-result injection).
    // Uses cosine similarity (not composite score) because composite can be inflated
    // by importance/recency of semantically unrelated memories.
    public double RetrievalConfidenceFloor  { get; set; } = 0.60;

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
}

public class OllamaOptions
{
    public string BaseUrl              { get; set; } = "http://localhost:11434";
    public string ChatModel            { get; set; } = "llama3.2";
    public string? InnerMonologueModel { get; set; }
    public string EmbedModel           { get; set; } = "nomic-embed-text";

    // AC4: Temperature splitting — lower temperature for memory-grounded responses
    // (factual recall, past conversations) to reduce confabulation. Standard temperature
    // for creative/emotional expression (playful banter, inner thoughts).
    public float MemoryGroundedTemperature { get; set; } = 0.3f;
    public float CreativeTemperature       { get; set; } = 0.8f;

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
