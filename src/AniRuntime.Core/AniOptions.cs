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
    public double RetrievalWeightCosine     { get; set; } = 0.5;
    public double RetrievalWeightImportance { get; set; } = 0.3;
    public double RetrievalWeightRecency    { get; set; } = 0.2;
    public double RetrievalRecencyDecayHours { get; set; } = 168.0; // λ for e^(-t/λ), ~7 day half-life

    // Conversation mode — active back-and-forth with Mark
    public double ConversationHeartbeatSeconds  { get; set; } = 45.0;
    public double ConversationTimeoutMinutes    { get; set; } = 15.0;
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

    // Feature 18: Reactive withdrawal — how long emotional withdrawal lasts after hurt detection
    public double WithdrawalDurationMinutes    { get; set; } = 20.0;

    // Reactive sharing — RSS items relevant enough to share directly with Mark
    public double ReactiveShareThreshold       { get; set; } = 0.6;
    public int    MaxReactiveSharesPerDay      { get; set; } = 2;
    public double ReactiveShareCooldownMinutes { get; set; } = 20.0;

    // Storage paths (relative to service working directory)
    public string CharacterStatePath     { get; set; } = "data/character-state.json";
    public string MemoryDbPath           { get; set; } = "data/ani-memory.db";
}

public class OllamaOptions
{
    public string BaseUrl              { get; set; } = "http://localhost:11434";
    public string ChatModel            { get; set; } = "llama3.2";
    public string? InnerMonologueModel { get; set; }
    public string EmbedModel           { get; set; } = "nomic-embed-text";

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

public class VoiceOptions
{
    public bool   Enabled              { get; set; } = false;

    // ElevenLabs TTS
    public string ElevenLabsApiKey     { get; set; } = string.Empty;
    public string ElevenLabsVoiceId    { get; set; } = string.Empty;
    public string ElevenLabsModelId    { get; set; } = "eleven_monolingual_v1";

    // OpenAI Whisper STT
    public string WhisperApiKey        { get; set; } = string.Empty;
    public string WhisperModel         { get; set; } = "whisper-1";

    // Twilio Voice
    public bool   PreferVoiceOverSms   { get; set; } = false;  // future: voice-first mode

    // Public URL for serving media (ngrok URL) — Twilio needs to fetch audio from here
    public string PublicBaseUrl         { get; set; } = string.Empty;
}
