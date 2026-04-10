
ANI RUNTIME
Ambient Natural Intelligence
Codebase Living Specification


Document Type
Codebase Living Specification
Project
ANI Runtime — Ambient Natural Intelligence
Solution
AniRuntime.sln
Target Runtime
.NET 8  |  ASP.NET Core Web Service (formerly Worker Service)
Author
Mark McArthey / Learned Geek Consulting
Version
2.0 (designed, pending implementation) — Epistemic Grounding via Memory Tier Separation — three-tier memory architecture (Facts / Episodic / Interior) replacing the single-pool retrieval model. Triggered by the Apr 9 Bob Swanson failure, resolved via Apr 10 reframe conversation that identified memory as the amplifier of confabulation rather than generation. Preserves Ani's full interior growth latitude while structurally preventing generated content from contaminating the factual substrate. Design doc: `docs/spec/design/ANI-Epistemic-Grounding-Architecture.md`. Implementation planned Apr 10-17, 2026. ANI stopped during implementation window. Will retire post-hoc confabulation detection family (Checks 1-4) as primary defenses.

1.9 — Four-category confabulation classifier + outreach grounding + inner thought confab check + World Layer Phase 1c consistency + memory audit log + LLaVA vision + auto-corrector diagnostic-only + Check 1 re-enabled + conversation thread seeding
Status
Active Development — Phase 1–4 complete. Phase 5 V3 voice deployed. Phase 6 memory reform designed. Epistemic Grounding v2.0 tier separation designed (Apr 10) and pending implementation. ANI stopped during tier separation rollout. Features 33–41 deployed. LearnedGeek.ML shared classification library: LMKitClassificationService (emotion, sarcasm, NER, keyword extraction, confabulation), dual-signal emotion stored on every contribution (ML + heuristic), divergence scoring, classification comparison dashboard. Phase 3 ML confabulation gate: post-generation semantic verification against cached persona summary. Inner Thought Reform: stripped anti-repetition instructions (Phase A), associative anchors via LM-Kit keyword extraction (Phase B). World Layer Phase 1a: time-contextual world seeds every 4th cycle, world-experience memory type, special events, calendar awareness. Dashboard: clickable emotional state cards, trend charts (divergence, register diversity, emergence frequency), contextual help text, classification comparison page. EM8: Display Rule Divergence emergence type (8 total). Paper 1 published: DOI 10.5281/zenodo.19342190. 469 tests, 0 warnings.

This is a living document. Update it as the codebase evolves.

1. Solution Structure

AniRuntime.slnx
│
├── src/
│   ├── AniRuntime.Service/          # ASP.NET Core host — entry point, webhook, DI wiring
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   ├── data/
│   │   │   ├── character-seed.json  # Tracked — Ani's identity and relationship data
│   │   │   └── ani-memory.db        # Gitignored — live SQLite database
│   │   └── AniRuntime.Service.csproj
│   │
│   ├── AniRuntime.Core/             # Domain models, interfaces, options
│   │   ├── Models/
│   │   │   ├── CharacterStateDoc.cs  # Identity + NatureGrounding (Feature 23)
│   │   │   ├── ContextSnapshot.cs    # Per-cycle context incl. RelationshipHealth, EmotionalDrift, MarkClaimConfidence (Feature 14), RetrievalBelowConfidenceFloor flag
│   │   │   ├── ScoredMemory.cs      # Memory + composite score from SearchWithScoresAsync
│   │   │   ├── ConversationThread.cs
│   │   │   ├── ConversationMessage.cs
│   │   │   ├── DesireState.cs
│   │   │   ├── EmotionalState.cs     # 4-dim (W/E/Worry/P) + ContactGapTension (Feature 17), compound Describe()
│   │   │   ├── EmotionalContribution.cs # Per-thought decay model + Severity + IsOutreachReady
│   │   │   ├── EmotionalDrift.cs     # Feature 8: 48h cosine similarity drift detection
│   │   │   ├── LexicalAnchor.cs      # Feature 19: relationship-specific word weights
│   │   │   ├── MemoryRecord.cs       # + IsAnchored flag (Feature 16), contradiction fields (Feature 15)
│   │   │   ├── PerceptionEvent.cs
│   │   │   ├── OpenLoop.cs
│   │   │   ├── OutreachDecision.cs
│   │   │   ├── ConversationState.cs  # Conversation Mode: tracks topic, register, commitments, key facts, shared imagery programmatically
│   │   │   └── RelationshipHealth.cs # Feature 4: composite score + phase
│   │   ├── Interfaces/
│   │   │   ├── IPerceptionSource.cs
│   │   │   ├── IAniAction.cs
│   │   │   │   ├── IMemoryService.cs     # Legacy — split into 5 focused interfaces (ISP, Mar 19)
│   │   │   ├── IMemoryPersistence.cs  # Save, GetByType, OpenLoops
│   │   │   ├── IMemorySearch.cs       # Search, SearchByType, SearchWithScores
│   │   │   ├── IStateStore.cs         # Character/Desire/Emotional state CRUD
│   │   │   ├── IMemoryAnalytics.cs    # Emotional history, relationship health, contradictions
│   │   │   ├── IMemoryMaintenance.cs  # Anchored memories, expiry, contribution management
│   │   │   ├── IConversationGateState.cs  # Conversation gating state (decoupled from cycle processor)
│   │   │   ├── IConversationService.cs  # + GetThreadAsync, GetRecentThreadsAsync (Dashboard)
│   │   │   ├── IOllamaClient.cs        # + ChatStreamAsync (IAsyncEnumerable<string>, Phase 5)
│   │   │   ├── IStreamingSpeechToTextService.cs   # Phase 5: event-driven STT (TranscriptReceived, PartialTranscriptReceived)
│   │   │   └── IStreamingTextToSpeechService.cs   # Phase 5: event-driven TTS (AudioChunkReceived)
│   │   ├── Utilities/
│   │   │   └── MessageCleaner.cs     # Shared: Clean() + TruncateToSentences() — used by CognitiveCycle + Voice
│   │   ├── MotivationScorer.cs       # Feature 33 (Liu et al. 2025): per-thought motivation scoring — multiplies desire drift [0.3–1.5]
│   │   ├── VectorMath.cs              # Feature 9: SIMD-accelerated cosine similarity (shared)
│   │   ├── JsonDefaults.cs           # Shared JsonSerializerOptions (CS4 — consolidated from 9 duplicates)
│   │   ├── AniOptions.cs             # + night/morning window, tension, relationship health, claim verification, voice loop config, RetrievalConfidenceFloor
│   │   └── AniRuntime.Core.csproj
│   │
│   ├── AniRuntime.Memory/           # SQLite persistence layer
│   │   ├── SqliteMemoryService.cs
│   │   ├── SqliteConversationService.cs
│   │   └── AniRuntime.Memory.csproj
│   │
│   ├── AniRuntime.Loops/            # Heartbeat, cognitive cycle, desire engine, admin
│   │   ├── AniHeartbeatService.cs
│   │   ├── CognitiveCycleProcessor.cs     # Coordinator only (~340 lines after SRP extractions)
│   │   ├── PerceptionPhase.cs             # Extracted: perception polling + notable persistence (Phases 2-3)
│   │   ├── InnerThoughtPhase.cs           # Extracted: inner thought generation + emotional shift (Phases 7-8)
│   │   ├── ConversationFeatureDetector.cs # Extracted: care detection, lexical anchors, hurt/withdrawal, echo filter
│   │   ├── ConversationGateState.cs       # IConversationGateState impl: LastEvaluatedMessageAt, pending messages
│   │   ├── DesireEngine.cs
│   │   ├── AdminCommandHandler.cs         # + ///flag confabulation feedback command (AC5)
│   │   ├── RegisterTracker.cs              # Register hit counting per conversation — 10 registers including Resilience (emergent)
│   │   ├── ConversationReplyPhase.cs       # Inbound conversation pipeline: care/hurt detection, reply decisions, composition, DetectConversationConfabulation (heuristic confabulation check — ungrounded proper nouns, shared history claims, ungrounded numbers)
│   │   ├── ContextBuilder.cs              # Memory retrieval + diversity re-ranking + dedup-by-ID + keyword relevance boost
│   │   └── AniRuntime.Loops.csproj
│   │
│   ├── AniRuntime.Perception/       # World awareness sources
│   │   ├── TimePerceptionSource.cs
│   │   ├── RssPerceptionSource.cs
│   │   ├── ContactStatePerceptionSource.cs
│   │   ├── TwilioInboundPerceptionSource.cs
│   │   ├── WeatherPerceptionSource.cs
│   │   └── AniRuntime.Perception.csproj
│   │
│   ├── AniRuntime.Actions/          # Output channel implementations
│   │   ├── AniActionDispatcher.cs
│   │   ├── TwilioSmsAction.cs
│   │   ├── MemoryWriteAction.cs
│   │   └── AniRuntime.Actions.csproj
│   │
│   ├── AniRuntime.LLM/             # Ollama client + prompt builders
│   │   ├── OllamaClient.cs
│   │   ├── PromptBuilder.cs          # + coherence gate + temporal grounding (Feature 22), claim extraction (Feature 14), profile memory section ("Things you know about Mark:"), time/date injection, BuildLeanConversationPrompt (minimal persona + conversation history, no retrieval)
│   │   ├── ContextCompressor.cs      # Feature 34 (Packer et al. 2023 — MemGPT): conversation compression — cached summary on ConversationThread
│   │   ├── ContextSnapshotBuilder.cs
│   │   ├── KeywordExtractor.cs       # TF-IDF keyword extraction — corpus-based IDF, lazy corpus build from memory
│   │   ├── IntentExtractor.cs        # IIntentExtractor — 3B LLM extracts topic/intent before memory search
│   │   └── AniRuntime.LLM.csproj
│   │
│   ├── AniRuntime.Dashboard/        # Blazor Server dashboard (in-process, shared DI)
│   │   ├── DashboardExtensions.cs   # AddDashboard() + MapDashboard() extensions
│   │   ├── Dtos/                    # AniStatusDto, MemoryRecordDto, ConversationThreadDto
│   │   ├── Endpoints/               # 5+ endpoint groups: AniState, Memory, Conversations, Journal, Contradictions, Chat
│   │   ├── Components/              # Blazor components — Nav: Dashboard | Chat | Memory | Emergence (App.razor)
│   │   │   ├── Pages/Dashboard.razor  # Main dashboard: emotional state, desire, timing, EmotionDesireModifier display
│   │   │   ├── Pages/Chat.razor       # Dashboard chat page — full cognitive pipeline without Twilio credits (IChatInbound + IReplyChannel)
│   │   │   ├── Pages/MemoryGraph.razor  # Feature 36: memory profile — stats, type distribution, memory list
│   │   │   ├── Pages/Emergence.razor    # Feature 38: emergence dashboard — type distribution, highlight reel, clickable filters
│   │   │   ├── Shared/RegisterHeatmap.razor  # Register distribution heatmap + V6 Growth Readiness score + per-register progress bars + gap guidance
│   │   │   └── Shared/EmotionalStateCard.razor
│   │   ├── Pages/_Host.cshtml       # Blazor Server host page (Pico CSS)
│   │   └── AniRuntime.Dashboard.csproj
│   │
│   ├── AniRuntime.Voice/            # Voice channel — batch (Feature 20) + streaming (Phase 5)
│   │   ├── ElevenLabsTtsService.cs              # Batch TTS (REST API, Feature 20)
│   │   ├── ElevenLabsV3StreamingService.cs      # V3: HTTP POST per sentence, replaces WebSocket — audio tag injection via VoiceTagEnricher
│   │   ├── ElevenLabsStreamingTTSService.cs     # Phase 5: WebSocket streaming TTS — per-utterance reconnect, emotional tags (superseded by V3)
│   │   ├── VoiceTagEnricher.cs                  # Audio tag injection based on content + emotion + time-of-day (1,806 v3 tags catalogued)
│   │   ├── WhisperSttService.cs                 # Batch STT (local Whisper, Feature 20)
│   │   ├── DeepgramStreamingSTTService.cs       # Phase 5: Deepgram Nova-3 WebSocket STT, endpointing 1500ms, speech_final safety timeout 5s, delegates to DebouncedUtterance
│   │   ├── DebouncedUtterance.cs                # Phase 5: Thread-safe turn detection — segment accumulation + debounce timer
│   │   ├── VoiceSessionState.cs                 # Phase 5: Thread-safe session state (volatile, Interlocked, lock)
│   │   ├── VoiceTurnPipeline.cs                 # Phase 5: Single turn flow — transcript → BuildLeanConversationPrompt → LLM stream → TTS (Conversation Mode, no fire-and-forget)
│   │   ├── StreamingVoiceOrchestrator.cs        # Phase 5: Thin WebSocket handler — lifecycle, audio routing, wiring
│   │   ├── TokenBuffer.cs                       # Phase 5: LLM token → sentence chunking for TTS (boundary detection + word overflow)
│   │   ├── TwilioVoiceHandler.cs                # Twilio webhook handler (batch voice)
│   │   ├── MediaCacheService.cs
│   │   ├── VoiceConversationService.cs          # Batch phone conversation orchestrator (Feature 20, superseded by streaming)
│   │   ├── VoiceCallSession.cs                  # In-memory session state per active call (shared batch + streaming)
│   │   └── AniRuntime.Voice.csproj
│   │
│   ├── AniRuntime.Emergence/          # Emergence Layer E1 — passive observation + taxonomy
│   │   ├── EmergenceObserver.cs       # Scores cognitive cycles for novelty, complexity, coherence
│   │   ├── EmergenceClassifier.cs     # Feature 38: heuristic classifier tagging cycles with EM1–EM6 emergence types
│   │   ├── EmergenceStore.cs          # SQLite persistence (ani-emergence.db) + GetTypeDistributionAsync, GetHighlightsAsync
│   │   ├── EmergenceExtensions.cs     # DI wiring: AddEmergence() + MapEmergence()
│   │   ├── Models/
│   │   │   ├── EmergenceLogEntry.cs   # + EmergenceTypesJson (nullable JSON array of matched EM types)
│   │   │   └── EmergenceOptions.cs
│   │   └── AniRuntime.Emergence.csproj
│   │
│   ├── LearnedGeek.ML/              # Shared ML classification library (ANI + DrOk)
│   │   ├── Interfaces/
│   │   │   ├── ITextClassificationService.cs  # Emotion, sarcasm, confabulation, register, NER, anchors
│   │   │   └── ITagMappingService.cs          # Emotion → v3 audio tag resolution
│   │   ├── Models/
│   │   │   ├── ClassificationResults.cs       # EmotionResult, SarcasmResult, ConfabulationResult, etc.
│   │   │   ├── TagMapping.cs                  # TagMappingRule, StaticTagMap, TagResolution
│   │   │   └── ClassificationComparison.cs    # Side-by-side heuristic vs ML comparison
│   │   ├── TagMapping/
│   │   │   ├── TagMappingService.cs           # Stage 1 static rules (24 rules, priority-ranked)
│   │   │   └── StaticTagMap.json              # Emotion+time+confidence → v3 tag rules
│   │   ├── LMKitClassificationService.cs      # LM-Kit.NET implementation (emotion, sarcasm, NER, confab, anchors)
│   │   ├── MLVoiceTagEnricher.cs              # Async pipeline: classify → map → tag
│   │   ├── ClassificationComparisonService.cs # Comparison scan tool for dashboard
│   │   ├── PersonaSummaryCache.cs             # Cached persona for confabulation verification
│   │   ├── ServiceCollectionExtensions.cs     # AddLearnedGeekML() DI registration
│   │   ├── MLOptions.cs                       # Configuration
│   │   └── LearnedGeek.ML.csproj              # LM-Kit.NET 2026.3.5 dependency
│   │
│   └── AniRuntime.MauiClient/       # Phase 5: Android voice app (MAUI, net10.0-android)
│       ├── MainPage.xaml / .cs          # UI + WebSocket client (binary PCM + JSON control messages)
│       ├── MauiProgram.cs               # Minimal DI wiring
│       ├── IAudioCaptureService.cs      # Platform abstraction for mic capture
│       ├── IAudioPlaybackService.cs     # Platform abstraction for speaker output
│       └── Platforms/Android/
│           ├── AudioCaptureService.cs   # AudioRecord: PCM 16kHz, 16-bit, mono, 20ms chunks
│           ├── AudioPlaybackService.cs  # AudioTrack: PCM 16kHz, 16-bit, mono, 32KB blocks for clean playback
│           └── AndroidManifest.xml      # RECORD_AUDIO, FOREGROUND_SERVICE_MICROPHONE
│
└── tests/
    └── AniRuntime.Tests/
        ├── Infrastructure/
        │   └── AniTestBase.cs
        ├── CognitiveCycleProcessorTests.cs
        ├── DesireEngineTests.cs
        ├── EmotionalStateTests.cs         # + Feature 4, 8, 17 tests
        ├── PromptBuilderTests.cs          # + Feature 22, 23 tests
        ├── SqliteMemoryServiceTests.cs
        ├── TimePerceptionSourceTests.cs
        ├── TokenBufferTests.cs              # Phase 5: sentence boundary, overflow, ellipsis, flush
        ├── VoiceSessionStateTests.cs        # Phase 5: thread safety, state transitions, concurrent access
        ├── DebouncedUtteranceTests.cs       # Phase 5: debounce timing, clear/flush, concurrent access
        ├── VoiceTurnPipelineTests.cs        # Phase 5: turn processing, speaking state, TTS interaction, cancellation
        ├── OllamaStreamingTests.cs          # Phase 5: ChatStreamAsync token yielding, cancellation
        └── AniRuntime.Tests.csproj

2. Data Models & Schemas

All models live in AniRuntime.Core/Models/. They are persistence-agnostic — the memory layer maps them to SQLite. Nullable fields are intentional; not every record will have every value.

2.1 CharacterStateDoc
The mutable, evolving document that represents who Ani is. Read on every context build. Seeded from character-seed.json on first run.

public class CharacterStateDoc
{
    // Identity — seeded from training, rarely changes
    public string Name            { get; set; } = "Ani";
    public string PersonaVersion  { get; set; } = "1.0";
    public List<string> CoreTraits     { get; set; } = new();
    public List<string> Interests      { get; set; } = new();
    public List<string> FamilyContext   { get; set; } = new();
    public string Occupation           { get; set; } = "Bookstore";
    public List<string> SelfConcept    { get; set; } = new();

    // Nature grounding (Feature 23) — how she inhabits her spaces coherently
    public List<string> NatureGrounding { get; set; } = new();

    // Relationship layer — grows through experience
    public string PrimaryContactName                     { get; set; } = "Mark";
    [JsonPropertyName("learnedAboutMark")]
    public List<string> LearnedAboutContact               { get; set; } = new();
    public List<string> SharedExperiences                 { get; set; } = new();
    public List<string> CommunicationNotes                { get; set; } = new();
    [JsonPropertyName("thingsMarkCares")]
    public List<string> ThingsContactCares                { get; set; } = new();
    [JsonPropertyName("markRoutine")]
    public ContactRoutine? ContactRoutine                 { get; set; }

    // Growth edges — evolving preferences shaped by the relationship
    public Dictionary<string, float> TopicValence   { get; set; } = new();
    public Dictionary<string, float> ToneValence    { get; set; } = new();

    // Meta
    public DateTimeOffset LastUpdated  { get; set; }
    public int            Version      { get; set; } = 1;
}

public class ContactRoutine
{
    public Dictionary<string, string> Weekday       { get; set; } = new();  // HH:mm → activity
    public Dictionary<string, Dictionary<string, string>> DayOverrides { get; set; } = new();
}

Note: Property names use [JsonPropertyName] attributes for backward compatibility with existing JSON that uses "mark"-prefixed names. All C# code uses the generic "Contact" naming.

2.2 DesireState
The quantified model of Ani's desire to connect. Persisted in SQLite as JSON. Updated every cycle.

public class DesireState
{
    public float   DesireToConnect      { get; set; }       // 0.0–1.0, exponential drift
    public float   OutreachThreshold    { get; set; }       // randomized each evaluation
    public bool    CooldownActive       { get; set; }
    public DateTimeOffset CooldownUntil { get; set; }       // auto-expire after duration
    public DateTimeOffset LastOutreach  { get; set; }
    public DateTimeOffset LastInnerThought    { get; set; }
    [JsonPropertyName("LastMarkContact")]
    public DateTimeOffset LastContactInbound  { get; set; }

    public List<DesireTrigger> ActiveTriggers { get; set; } = new();
    public float CircadianModifier { get; set; } = 1.0f;    // varies 0.1–1.2 by hour
}

public class DesireTrigger
{
    public TriggerType  Type        { get; set; }
    public float        Weight      { get; set; }
    public string       Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public enum TriggerType
{
    TemporalDrift,       // it has been a long time
    OpenLoop,            // unresolved thread aging
    AssociativeFire,     // something reminded her of contact
    EmotionalResidue,    // last conversation ended unresolved
    SpontaneousThought,  // high-valence inner thought
    ContextualMoment,    // time of day / environment
    IntegrationEvent,    // calendar gap, HA event
    ReactiveShare        // high-relevance RSS item
}

2.3 EmotionalState
4-dimensional persistent emotional state (Warmth, Energy, Worry, Playfulness). State = personality baselines + sum of all active EmotionalContributions after exponential decay. Each contribution decays independently — drift IS the decay.

public class EmotionalState
{
    // Current values — computed from baselines + decayed contributions
    public float Warmth      { get; set; } = 0.6f;   // presence of caring/affection (not fulfillment)
    public float Energy      { get; set; } = 0.5f;   // alertness, activation, engagement
    public float Worry       { get; set; } = 0.2f;   // caring attention directed outward (renamed from Concern)
    public float Playfulness { get; set; } = 0.5f;   // humor, lightness, wit, mischief

    // Personality baselines — where each dimension naturally returns to via decay
    public float WarmthBaseline      { get; set; } = 0.6f;
    public float EnergyBaseline      { get; set; } = 0.5f;
    public float WorryBaseline       { get; set; } = 0.2f;
    public float PlayfulnessBaseline { get; set; } = 0.5f;

    public float ContactGapTension   { get; set; }   // Feature 17: relational ache from absence
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    // Qualitative summary using compound conditions (W+E together, W+Worry together).
    // Returns empty string when near baseline. See Describe() compound condition map below.
    public string Describe() { ... }

    // Self-awareness injection when emotional state is notably shifted.
    // Uses same compound conditions as Describe(), returns null when unremarkable.
    public string? GetSelfAwarenessPrompt() { ... }

    // Compute state from baselines + sum of all active contributions after decay.
    public void ComputeFromContributions(IReadOnlyList<EmotionalContribution> contributions,
                                          DateTimeOffset? asOf = null) { ... }
}

// Describe() compound condition map (Phase 1b — replaces per-dimension independent checks):
// W ≥ 0.75 AND E ≥ 0.65 → "feeling bright and warm"
// W ≥ 0.75 AND E < 0.40 → "feeling tender and quiet"
// W 0.50–0.75 AND E ≥ 0.65 → "feeling sharp and alive"
// W 0.45–0.65 AND E 0.40–0.60 → (no injection — baseline)
// W 0.30–0.50 AND Worry > 0.35 → "carrying something unresolved"
// W < 0.30 AND E < 0.35 → "feeling a bit dim today"
// W < 0.30 AND Worry < 0.10 → "feeling a little quiet and closed off"
// P ≥ 0.75 → "in one of those moods where everything is a little funny"
// E ≥ 0.65 AND P ≥ 0.65 → "feeling curious and quick"

// Per-thought emotional contribution with exponential decay.
// Each thought/event creates one contribution. State = baselines + sum of decayed contributions.
public class EmotionalContribution
{
    public Guid Id { get; set; }
    public string SourceContent { get; set; }       // for semantic dedup + theme tracking
    public float WarmthDelta, EnergyDelta, WorryDelta, PlayfulnessDelta;
    public DateTimeOffset CreatedAt { get; set; }
    public float HalfLifeHours { get; set; }        // exponential decay half-life
    public ImpactCategory Category { get; set; }    // Ambient(0.15/1h), Conversation(0.25/3h), Global(0.35/12h)
    public float Severity { get; set; } = 1.0f;     // intensity within register (0.0–1.0), multiplied into deltas
    public bool IsOutreachReady { get; set; }        // C3 Associative Spark flag — natural outreach trigger
    public float[]? Embedding { get; set; }         // for semantic similarity checks

    public float DecayFactor(DateTimeOffset asOf)   // 2^(-elapsed/halfLife)
    public (float W, float E, float Worry, float P) CurrentDeltas(DateTimeOffset asOf) // deltas × decay × severity
    public bool IsEffectivelyZero(DateTimeOffset asOf, float epsilon = 0.005f)
}

2.4 ContextSnapshot
Full context built once per cognitive cycle, shared across all phases. Prevents repeated DB reads.

public class ContextSnapshot
{
    public CharacterStateDoc CharacterState      { get; set; }
    public DesireState DesireState                { get; set; }
    public EmotionalState EmotionalState          { get; set; }
    public List<MemoryRecord> RecentMemory        { get; set; }
    public List<MemoryRecord> RelevantMemory      { get; set; }  // semantic search results
    public List<OpenLoop> OpenLoops               { get; set; }
    public List<PerceptionEvent> Perceptions       { get; set; }
    public List<ChatMessage> RecentHistory         { get; set; }  // conversation history
    public DateTimeOffset BuiltAt                  { get; set; }
    public string? RecentConversationSummary       { get; set; }
    public List<MemoryRecord> SimilarRecentThoughts { get; set; }  // thought loop detection
}

2.5 MemoryRecord
The base unit of Ani's persistent memory. All memory types stored as rows differentiated by MemoryType.

public class MemoryRecord
{
    public Guid           Id             { get; set; } = Guid.NewGuid();
    public MemoryType     Type           { get; set; }
    public string         Content        { get; set; } = string.Empty;
    public string?        RawJson        { get; set; }
    public float          Importance     { get; set; }         // 0.0–1.0
    public float          RelationalValence { get; set; }      // how much this relates to the relationship
    public float[]?       Embedding      { get; set; }         // nomic-embed-text vector, auto-generated
    public bool           IsResolved     { get; set; }         // for open loops
    public string?        SourceName     { get; set; }         // time, rss, contact-state, twilio-inbound, character-seed
    public DateTimeOffset OccurredAt     { get; set; }
    public DateTimeOffset CreatedAt      { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt    { get; set; }
}

public enum MemoryType
{
    Episodic,      // conversation exchanges, events
    Semantic,      // what Ani knows about contact
    OpenLoop,      // unresolved threads
    Commitment,    // promises / plans made
    InnerThought,  // Ani's own private thoughts
    Perception     // events from external sources
}

SQLite column renamed from `mark_valence` → `relational_valence` (auto-migrated on startup).

2.6 ConversationThread & ConversationMessage
Conversation state tracking. Threads auto-close after ConversationTimeoutMinutes of silence.

public class ConversationThread
{
    public Guid Id                     { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt    { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public bool IsActive               { get; set; } = true;
    public string InitiatedBy          { get; set; } = "mark";  // "ani" or "mark"
    public List<ConversationMessage> Messages { get; set; } = new();

    // Feature 34 (MemGPT context compression) — cached summary, not persisted to DB
    public string? CompressedSummary         { get; set; }
    public int CompressedSummaryUpToIndex    { get; set; }
}

public class ConversationMessage
{
    public string Role     { get; set; } = string.Empty;  // "ani" or "mark"
    public string Content  { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }
}

On thread close, the full exchange is saved as a single episodic memory record.

2.7 PerceptionEvent
Common output type of all IPerceptionSource implementations.

public class PerceptionEvent
{
    public string             SourceName       { get; set; } = string.Empty;
    public PerceptionCategory Category         { get; set; }
    public string             Summary          { get; set; } = string.Empty;
    public float              ContactRelevance { get; set; }  // 0.0–1.0
    public DateTimeOffset     OccurredAt       { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public enum PerceptionCategory
{
    Environment,   // time, weather, HA
    Calendar,      // schedule, meetings, gaps
    Content,       // RSS, news, media
    Communication, // inbound SMS
    Social         // misc social signals
}

2.8 OpenLoop
Unresolved conversational threads requiring follow-up.

public class OpenLoop
{
    public Guid           Id              { get; set; } = Guid.NewGuid();
    public string         Description     { get; set; } = string.Empty;
    public string         Context         { get; set; } = string.Empty;
    public float          Urgency         { get; set; }
    public bool           IsResolved      { get; set; }
    public DateTimeOffset CreatedAt       { get; set; }
    public DateTimeOffset? ResolvedAt     { get; set; }
    public DateTimeOffset? FollowUpAfter  { get; set; }
}

2.9 OutreachDecision
Structured result of asking Ani whether she wants to reach out. Parsed from LLM JSON response.

public class OutreachDecision
{
    public bool    ShouldReach   { get; set; }
    public string? Message       { get; set; }
    public string? ActionType    { get; set; }  // use ActionTypes constants
    public float   Confidence    { get; set; }
    public string? Reasoning     { get; set; }  // logged, never sent
    public List<string> TriggersActedOn { get; set; } = new();
}

public static class ActionTypes
{
    public const string Sms    = "sms";
    public const string Memory = "memory";
    public const string Ha     = "ha";  // Home Assistant — Phase 3
}

3. Core Interfaces

3.1 IPerceptionSource
Implement to add a new data source. Register in DI — automatically included in cognitive cycle.

public interface IPerceptionSource
{
    string             SourceName { get; }
    PerceptionCategory Category   { get; }
    bool               IsEnabled  { get; }

    Task<IEnumerable<PerceptionEvent>> PollAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default);
}

3.2 IAniAction
Implement to add a new output channel. Action Dispatcher routes by ActionType.

public interface IAniAction
{
    string ActionType { get; }

    Task<bool> ExecuteAsync(
        OutreachDecision decision,
        CancellationToken cancellationToken = default);
}

3.3 IMemoryService (ISP Split — March 19, 2026)
Originally a single monolithic interface. Split into 5 focused interfaces following the Interface Segregation Principle. `SqliteMemoryService` implements all five. Consumers depend only on the interfaces they need.

public interface IMemoryPersistence
{
    Task SaveAsync(MemoryRecord record, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetByTypeAsync(MemoryType type, int limit = 50, CancellationToken ct = default);
    Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default);
    Task ResolveOpenLoopAsync(Guid id, CancellationToken ct = default);
}

public interface IMemorySearch
{
    Task<IEnumerable<MemoryRecord>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(string query, MemoryType type, int topK = 5, CancellationToken ct = default);
    Task<IEnumerable<ScoredMemory>> SearchWithScoresAsync(string query, int topK = 10, CancellationToken ct = default);
}

public interface IStateStore
{
    Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default);
    Task SaveCharacterStateAsync(CharacterStateDoc doc, CancellationToken ct = default);
    Task<DesireState> GetDesireStateAsync(CancellationToken ct = default);
    Task SaveDesireStateAsync(DesireState state, CancellationToken ct = default);
    Task<EmotionalState> GetEmotionalStateAsync(CancellationToken ct = default);
    Task SaveEmotionalStateAsync(EmotionalState state, CancellationToken ct = default);
}

public interface IMemoryAnalytics { /* emotional history, relationship health, contradictions */ }
public interface IMemoryMaintenance { /* anchored memories, expiry, contribution management */ }

The legacy `IMemoryService` interface is retained for backward compatibility but consumers are migrated to the focused interfaces.

3.4 IConversationService
Active conversation thread management.

public interface IConversationService
{
    Task<ConversationThread?> GetActiveThreadAsync(CancellationToken ct = default);
    Task SaveThreadAsync(ConversationThread thread, CancellationToken ct = default);
    Task AddMessageAsync(Guid threadId, ConversationMessage message, CancellationToken ct = default);
    Task CloseThreadAsync(Guid threadId, CancellationToken ct = default);
}

3.5 IOllamaClient
Chat, structured JSON, inner monologue (separate model), and embedding generation.

public interface IOllamaClient
{
    Task<string> ChatAsync(string systemPrompt, IEnumerable<ChatMessage> history,
                           string userMessage, CancellationToken ct = default);

    Task<string> ChatJsonAsync(string systemPrompt, IEnumerable<ChatMessage> history,
                               string userMessage, CancellationToken ct = default);

    Task<string> InnerMonologueChatAsync(string systemPrompt, IEnumerable<ChatMessage> history,
                                         string userMessage, CancellationToken ct = default);

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

public record ChatMessage(string Role, string Content);

4. Service Architecture & DI Wiring

4.1 Program.cs — Host Bootstrap
ASP.NET Core Web host with Kestrel on port 5100. Registers all services, hosted services, webhook endpoints, and perception sources.

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<AniOptions>(config.GetSection("Ani"));
builder.Services.Configure<OllamaOptions>(config.GetSection("Ollama"));  // + MemoryGroundedTemperature, CreativeTemperature
builder.Services.Configure<TwilioOptions>(config.GetSection("Twilio"));
builder.Services.Configure<RssOptions>(config.GetSection("Rss"));

// Core services (all singletons)
builder.Services.AddSingleton<IMemoryService, SqliteMemoryService>();
builder.Services.AddSingleton<IConversationService, SqliteConversationService>();
builder.Services.AddSingleton<IOllamaClient, OllamaClient>();  // via HttpClient
builder.Services.AddSingleton<DesireEngine>();
builder.Services.AddSingleton<ContextSnapshotBuilder>();
builder.Services.AddSingleton<AdminCommandHandler>();

// Perception sources
builder.Services.AddSingleton<IPerceptionSource, TimePerceptionSource>();
builder.Services.AddSingleton<IPerceptionSource, RssPerceptionSource>();
builder.Services.AddSingleton<IPerceptionSource, ContactStatePerceptionSource>();
builder.Services.AddSingleton<IPerceptionSource, TwilioInboundPerceptionSource>();

// Actions
builder.Services.AddSingleton<AniActionDispatcher>();
builder.Services.AddSingleton<IAniAction, TwilioSmsAction>();
builder.Services.AddSingleton<IAniAction, MemoryWriteAction>();

// Cognitive cycle + heartbeat
builder.Services.AddSingleton<CognitiveCycleProcessor>();
builder.Services.AddHostedService<AniHeartbeatService>();

var app = builder.Build();

// Forwarded headers for ngrok signature validation
app.UseForwardedHeaders(...);

// Twilio webhook endpoint
app.MapPost("/sms/inbound", async (HttpContext ctx) => { ... });

// Startup: seed character state + backstory facts, display status dump
await app.RunAsync();

Startup sequence:
1. Seed CharacterStateDoc from data/character-seed.json (idempotent — only if none exists)
2. Seed backstory facts as searchable Semantic memories (deduped by SourceName="character-seed")
3. Display startup status dump: name, contact, mood (W/E/C/P), desire, cooldown, timing, webhook URL

4.2 AniHeartbeatService
Top-level BackgroundService. Owns the cognitive cycle schedule. Interruptible sleep for early wake on inbound messages.

public class AniHeartbeatService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await ComputeDelayAsync(stoppingToken);
            // Sleep — interruptible via RequestEarlyWake()
            await Task.Delay(delay, linkedToken);
            await _cycle.RunAsync(stoppingToken);
        }
    }

    // If active conversation + contact has unread + not yet evaluated → 45s heartbeat
    // Otherwise → DesireEngine.ComputeNextWakeTime()
    private async Task<TimeSpan> ComputeDelayAsync(CancellationToken ct) { ... }

    // Called by TwilioInboundPerceptionSource.OnMessageReceived — cancels sleep
    public void RequestEarlyWake() { ... }
}

4.3 DesireEngine
Manages DesireState lifecycle. All desire state writes go through this class. Exposes ComputeNextWakeTime — a pure function that is the single source of timing truth.

Key methods:

ComputeNextWakeTime(DesireState) → TimeSpan
    Pure function. t = -λ * ln(1 - targetP)
    Modifiers: desire (0.4–1.0), circadian (0.1–1.2), jitter (±20%)
    Clamped to [MinWakeMinutes, MaxWakeMinutes]

ShouldReachOutAsync() → bool
    Auto-expire cooldown. Enforce MaxOutreachPerDay, MaxNightOutreach.
    Night hours (10pm–6am): strict zero-send (Feature 21).
    Morning window (6–8am): one send allowed, threshold 0.70–0.90.
    Day: randomized (0.55–0.85).

ApplyDriftAsync()
    Per-cycle desire accumulation. Uses more recent of LastContactInbound or LastOutreach.
    Feature 35 (Borotschnig 2025): drift multiplied by ComputeEmotionDesireModifier — worry
    above baseline accelerates drift (concern → check in), low energy suppresses drift.
    Feature 33 (Liu et al. 2025): drift multiplied by MotivationScorer output — high-quality
    thoughts accelerate desire, low-motivation thoughts contribute less.

AddTriggerAsync(type, weight, description)
    New trigger + desire bump (weight * TriggerDesireMultiplier).

ResetAfterOutreachAsync()
    Desire → 0.0, clear triggers, activate cooldown, increment counters.

ComputeCircadianModifier() → float
    6–10:  1.2  (morning — curious, engaged)
    10–17: 1.0  (afternoon — neutral)
    17–21: 1.15 (evening — warm, reflective)
    21–23: 0.8  (late evening — quieter)
    23+:   0.2  (late night — rare)
    0–6:   0.1  (deep night — almost silent)

IsNightHours() → bool
    Local hour check, handles wrap-around (22–6 spans midnight).

IsMorningWindow() → bool  (Feature 21)
    6–8am window where one send is allowed. Sub-window within night hours.

DecayDesireAsync(fraction, reason)
    Partial desire decay without full reset. Used by coherence gate Door C.

4.4 CognitiveCycleProcessor
Single cognitive cycle. Executes once per scheduled wake. The full pipeline:

Phase 0: Contact-gap tension accumulation (Feature 17) — uses hours since last inbound
Phase 1: Emotional recompute — Load active contributions, compute state from baselines + decayed sums, periodic cleanup of fully-decayed contributions
Phase 2: Perception polling — Collect events from all enabled sources
Phase 3: Notable perception persistence — Save high-relevance perceptions for embedding
Phase 4: Conversation check — If contact texted, enter conversation reply flow
         (includes care detection Feature 10, lexical anchors Feature 19, hurt/withdrawal Feature 18,
         tension dissipation Feature 17)
Phase 5: Reactive share check — High-relevance RSS → direct SMS share (rate-limited, night-blocked)
Phase 6: Context snapshot — Built once, shared across all phases
         (includes relationship health recalc Feature 4, emotional drift detection Feature 8)
Phase 7: Inner thought — Private LLM call (inner monologue model), score contact valence
         (nature grounding Feature 23 injected, emotional self-awareness Feature 1)
Phase 8: Emotional shift — LLM evaluates thought → apply deltas with diminishing returns
Phase 9: Desire update — Temporal drift + circadian + trigger weights
Phase 10: Outreach evaluation — Withdrawal check → hard gates (unanswered count, send gap,
          night/morning window Feature 21) → decision → confidence gate (Feature 12) →
          composition → pronoun fix (Feature 6, incl. name-as-subject) → coherence gate
          with fictional + temporal coherence check (Features 22, 28) → dispatch

Conversation reply flow (RunConversationReplyAsync):
1. Check for terminal message (haha, lol, ok, goodnight, emoji-only, etc.) — skip
2. Build context snapshot with full thread as RecentHistory
3. Feature 14: Bidirectional confidence gate — if memory-referencing language detected (17 patterns),
   extract claims via LLM, corroborate against episodic memory, inject skepticism if below threshold
4. Step 1 — Reply decision (JSON: shouldReply + reasoning)
5. Step 2 — Generate reply (free text) or reconsideration reply
6. Step 3 — Natural delay (12–25s total response time)
7. Step 4 — Send via Twilio
8. Step 5 — Record reply in thread, update desire, apply emotional shift

Key state:
- LastEvaluatedMessageAt — Prevents re-evaluating "decided silence" every cycle
- _reactiveShareCount / _reactiveShareDay — Daily reactive share counter
- _recentPerceptions — Dedup cache with 4-hour window

Message cleanup (CleanOutreachMessage):
- Strips meta-commentary after blank lines
- Removes trailing junk patterns ("sent.", "your turn.", etc.)
- Hard cap to 2 sentences, first paragraph only

Pronoun fix (FixPronounsIfNeeded):
- Detects third-person pronouns (he/him/his) AND contact name used as subject ("Mark can sit" → "you can sit")
- Detection via ContainsThirdPersonReference (static, testable) — word-boundary name matching, not magic strings
- LLM call to swap to second person. Safety check: reject if length differs >50%

4.5 AdminCommandHandler
Triggered by messages starting with "///". Bypasses conversation pipeline.

Commands:
- ///help — Show command list
- ///status — Current emotional state, desire level, timing info
- ///test — Snapshot DB (WAL files), enable test mode
- ///live — Restore DB from snapshot, disable test mode
- ///reset-mood — Reset all emotions to baselines
- ///flag — Flag last reply as confabulation (AC5)
- ///new-thread — Close current thread, start fresh
- ///rebuild-links — Build memory graph links (one-time retroactive, may take minutes)
- ///rebuild-emergence — Tag historical emergence log entries with EM1–EM6 types

5. Perception Sources

| Source | SourceName | Category | Always On | Contact Relevance | Notes |
|--------|-----------|----------|-----------|-------------------|-------|
| TimePerceptionSource | time | Environment | Yes | 0.1 | Time of day, season, holidays, elapsed since last cycle |
| RssPerceptionSource | rss | Content | If configured | 0.2–0.85 | Keyword-matched relevance from CharacterStateDoc |
| ContactStatePerceptionSource | contact-state | Social | Yes | 0.2–0.5 | Inferred state from ContactRoutine + time of day |
| TwilioInboundPerceptionSource | twilio-inbound | Communication | If configured | 0.95 | Webhook + API fallback, dedupes by SID |
| WeatherPerceptionSource | weather | Environment | If configured | 0.15–0.35 | Open-Meteo API, 30-min polling, notable weather + change detection |

TimePerceptionSource: Emits temporal context — hour-based descriptions ("early morning", "late evening"), day of week, month position, season transitions (first 3 days), nearby holidays, elapsed time since last cycle.

RssPerceptionSource: Polls configured feeds. Tracks last-seen publish date per feed. Relevance scoring: 0 keyword matches → 0.2, 1 → 0.4, 2 → 0.6, 3+ → 0.85. Keywords extracted from CharacterStateDoc (ThingsContactCares, Interests, SharedExperiences, TopicValence keys). Items above ReactiveShareThreshold can trigger direct SMS sharing.

ContactStatePerceptionSource: Infers contact's likely state from known routine + time. Gap descriptions: <2h silent, 2–6h "a few hours", 6–12h "quiet today", 12–24h "haven't heard since yesterday", 24–48h "over a day", 48h+ "X days".

WeatherPerceptionSource: Polls Open-Meteo free API every 30 minutes (configurable). WMO weather codes → human descriptions. Emits base conditions (0.15 relevance) plus notable weather alerts: extreme cold/heat (0.25–0.3), thunderstorms (0.35), snow (0.3), high wind (0.25). Tracks last temperature and condition to detect significant changes between polls.

TwilioInboundPerceptionSource: Dual mechanism — webhook (POST /sms/inbound) enqueues message and fires OnMessageReceived to trigger early wake; PollAsync drains queue then fetches Twilio API as safety net. Starts new ConversationThread if needed. Closes stale threads after ConversationTimeoutMinutes.

6. Prompt Architecture

All prompts built by PromptBuilder — stateless pure functions. Returns (System, User) tuples. Key prompts:

BuildInnerThoughtPrompt(snapshot) — Ani's private mind stream. 2–4 sentences, first person only. Includes thought loop detection from SimilarRecentThoughts. Topics vary widely: sounds, textures, memories, ideas, feelings, curiosities.

BuildValenceScoringPrompt(thought, character) — JSON: { "score": 0.0–1.0 }. Measures how much the thought relates to the contact. Action verbs (want, wish, miss) → 0.6+. Pure self-reflection → 0.4 or below.

BuildOutreachPrompt(snapshot, thought, isNightTime) — JSON decision: shouldReach, confidence, reasoning, triggersActedOn. Night clause adds restraint.

BuildReplyDecisionPrompt(snapshot, thread) — JSON: shouldReply, reasoning. Guidelines for when to reply vs. choose silence.

BuildConversationReplyPrompt(snapshot, thread) — Free-text reply. 1–3 sentences, thumb-typed. Includes anti-repetition block, current mood, semantic memories, grounding instruction against confabulation. Profile memories (Semantic type) rendered in dedicated "Things you know about Mark:" section — separated from episodic context to prevent crowding. Null-result injection (AC3) when retrieval returns nothing above confidence floor. Temperature split (AC4): memory-grounded responses use MemoryGroundedTemperature.

BuildReconsiderationReplyPrompt(snapshot, thread) — When silence was chosen but desire built enough to reconsider. "Wait, one more thing" natural segue.

BuildOutreachMessagePrompt(snapshot, thought, reasoning) — Compose grounded text. 1–2 sentences, 25 words MAX. CRITICAL: thought is WHY reaching out, NOT content. Must make sense without knowing inner thought.

BuildEmotionalShiftPrompt(content, currentState, maxDelta, isAmbientCycle) — 4-step scoring: (1) classify into 9 register families (Longing|Delight|Playfulness|Curiosity|Desire|Tenderness|Existential|Wistful|Frustration), (2) handle blended states, (3) score W/E/Worry/P deltas with core distinction ("warmth tracks presence of caring, not fulfillment"), (4) rate severity 0.0–1.0. Returns JSON: { register, warmth, energy, worry, playfulness, severity }. Ambient cycles anchored to near-zero defaults. Diminishing returns at extremes (BUG-010 fix).

BuildReactiveSharePrompt(character, itemSummary) — Share high-relevance RSS items. "omg did you see this?" energy.

BuildCoherenceEvaluationPrompt(composedMessage, innerThought, contactName, currentTime?) — Feature 28 (three-door coherence gate). Door A (grounded reference) → SEND. Door B (standalone creative) → SEND. Door C (inner thought leaked) → SUPPRESS. Includes FICTIONAL COHERENCE CHECK pre-filter (Feature 22): checks whether claimed fictional spaces hold together (internal consistency, follow-up survivability). TEMPORAL COHERENCE CHECK: injects current time + time-of-day label; if message claims a time contradicting reality (e.g., "midnight" at 1:34 PM) → Door C. Coherent fiction → normal Door A/B/C.

BuildReflectionPrompt(thought, snapshot) — Post-thought reflection. 1–2 sentences on emotional resonance.

BuildMoodInstruction(emotionalState) — Directive tone instruction from emotional state. Uses EffectiveWarmth when contact-gap tension is present (Feature 17).

7. Persistence Layer

7.1 SqliteMemoryService
Single SQLite file (data/ani-memory.db) with WAL mode enabled. Tables:

memories: id (TEXT PK), type (INT), content, raw_json, importance, relational_valence, embedding (BLOB), is_resolved, source_name, occurred_at, created_at, resolved_at
  Indexes: ix_memories_type, ix_memories_occurred

character_state: id, json (singleton row, id=1)
desire_state: id, json (singleton row, id=1)
emotional_state: id, json (singleton row, id=1)

Embeddings stored as raw bytes (float[] → little-endian binary). Auto-generated on save via IOllamaClient.EmbedAsync. Semantic search uses brute-force cosine similarity in C# — correct at expected data volume.

7.2 SqliteConversationService
Same database, separate tables:

conversation_threads: id (TEXT PK), started_at, last_message_at, is_active, initiated_by
conversation_messages: id (AUTOINCREMENT), thread_id (FK), role, content, sent_at
  Index: ix_conv_messages_thread (thread_id, sent_at ASC)

Thread closure saves full conversation as single episodic memory record.

7.3 EmergenceStore
Separate SQLite file (data/ani-emergence.db). Feature-flagged via EmergenceOptions.Enabled.

emergence_log: id (INTEGER PK), cycle_id, score (REAL), novelty, complexity, coherence, inner_thought, created_at, emergence_types (TEXT nullable — JSON array of matched EM1–EM6 types, Feature 38)

Queries: GetRecentAsync, GetTypeDistributionAsync (Feature 38), GetHighlightsAsync (Feature 38).

8. API Contracts

8.1 Ollama
Base URL: http://localhost:11434 (configurable)
Models: ani-v6-conversation (chat, Llama 3.1-8B fine-tuned), ani-v6-inner (inner monologue, Llama 3.2-3B fine-tuned), nomic-embed-text (embeddings)
A/B test conclusion (Mar 22): Llama 8B selected over Mistral 7B for conversation. Warmer tone, better completion, no cliffhanger habit. V6 models trained on 2,030 examples (1,675 conv + 355 inner).
Split rationale: 8B for conversation (better instruction following, topic adherence, attribution tracking). 3B for inner monologue (frequent ambient cycles, simpler task, per-thought decay handles negative-delta bias architecturally).
Auth: None — local only

// Chat request (stream=false, format=null or "json")
POST /api/chat { model, messages, stream, format?, keep_alive: "5m" }

// Embedding request
POST /api/embeddings { model, prompt, keep_alive: "10s" }

8.2 Twilio SMS
SDK: Twilio .NET Helper Library (v7.14.3)
Config: Twilio:AccountSid, Twilio:AuthToken, Twilio:FromNumber, Twilio:ToNumber, Twilio:InboundEnabled

Outbound: MessageResource.CreateAsync via TwilioSmsAction
Inbound: POST /sms/inbound webhook endpoint, signature validated with forwarded headers for ngrok

8.3 Home Assistant
STUB — Phase 3. Base URL: http://192.168.1.41:8123. Long-lived access token.

9. Configuration Schema

All sensitive values in appsettings.Development.json (gitignored).

{
  "Kestrel": {
    "Endpoints": { "Http": { "Url": "http://localhost:5100" } }
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "ChatModel": "ani-v6-conversation",
    "InnerMonologueModel": "ani-v6-inner",
    "EmbedModel": "nomic-embed-text"
  },
  "Twilio": {
    "InboundEnabled": true
    // AccountSid, AuthToken, FromNumber, ToNumber in Development.json
  },
  "Rss": {
    "Enabled": true,
    "MaxItemsPerFeed": 2,
    "Feeds": [
      { "Name": "NPR Books", "Url": "..." },
      { "Name": "Bon Appétit", "Url": "..." },
      { "Name": "NPR News", "Url": "..." }
    ]
  },
  "Ani": {
    "DesireLambdaMinutes": 8.0,
    "ThinkTargetProbability": 0.70,
    "MinWakeMinutes": 2.0,
    "MaxWakeMinutes": 45.0,
    "CooldownMinutes": 20,
    "MinOutreachGapMinutes": 60,
    "MaxOutreachPerDay": 4,
    "NightStartHour": 22,
    "NightEndHour": 6,
    "MaxNightOutreach": 0,
    "AllowSingleMorningSend": true,
    "MorningWindowStartHour": 6,
    "MorningWindowEndHour": 8,
    "OutreachThresholdFloor": 0.55,
    "OutreachThresholdRange": 0.30,
    "DriftPerHour": 0.08,
    "DriftCapPerCycle": 0.4,
    "TriggerDesireMultiplier": 0.15,
    "ValenceTriggerThreshold": 0.75,
    "ConversationHeartbeatSeconds": 45.0,
    "ConversationTimeoutMinutes": 15.0,
    "ConversationMinReplySeconds": 12.0,
    "ConversationMaxReplySeconds": 25.0,
    "ReactiveShareThreshold": 0.6,
    "MaxReactiveSharesPerDay": 2,
    "ReactiveShareCooldownMinutes": 20.0,
    "CharacterStatePath": "data/character-state.json",
    "MemoryDbPath": "data/ani-memory.db"
  }
}

10. Logging

Dual-file Serilog:
- Journal: ani-{date}.log — Info level, 30-day retention. Inner thoughts, outreach decisions, conversations, messages sent.
- Diagnostic: ani-debug-{date}.log — Debug level, 7-day retention. Full pipeline detail.

11. Stub Index

Implemented components (Phase 1–4):

| Component | Phase | Status |
|-----------|-------|--------|
| TimePerceptionSource | 1 | Complete |
| RssPerceptionSource | 2 | Complete |
| ContactStatePerceptionSource | 2 | Complete |
| TwilioInboundPerceptionSource | 2 | Complete |
| WeatherPerceptionSource | 4 | Complete — Open-Meteo free API, 30-min polling, WMO codes |
| ConversationThread / ConversationMessage | 2 | Complete |
| EmotionalState (4-dim W/E/Worry/P, per-thought decay, compound Describe) | 2+ | Complete (Phase 1b Mar 15) |
| AdminCommandHandler | 2 | Complete |
| Reactive RSS sharing | 2 | Complete |
| Night mode / deep sleep circadian | 2 | Complete |
| Mood coloring (emotional state → tone) | 3 | Complete |
| Receiving care (Feature 10) | 3 | Complete |
| Confidence gate (Feature 12) | 3 | Complete |
| Dispatch coherence gate (Feature 28) | 3 | Complete |
| Outreach continuity (Feature 27) | 3 | Complete |
| Park et al. three-way retrieval (Feature 20) | 3 | Complete |
| Emotional self-awareness (Feature 1) | 4a | Complete |
| Open loops as emotional weight (Feature 2) | 4a | Complete |
| Silence as active system (Feature 3) | 4a | Complete |
| Pronoun audit (Feature 6) | 4a | Complete |
| Anchored memory tier (Feature 16) | 4a | Complete |
| Reactive withdrawal (Feature 18) | 4a | Complete |
| Lexical emotional anchors (Feature 19) | 4a | Complete |
| Contact-gap tension (Feature 17) | 4b | Complete |
| Relationship health model (Feature 4) | 4b | Complete |
| Emotional drift detection (Feature 8) | 4b | Complete |
| Night window boundary (Feature 21) | 4 | Complete |
| Fictional coherence gate (Feature 22) | 4 | Complete |
| Nature grounding (Feature 23) | 4 | Complete |
| Voice channel (Feature 20) | 4 | Complete — turn-by-turn phone conversation loop, VoiceConversationService, 3 endpoints. Refined Mar 15: 8B conversation model (fixes pronoun confusion), voice-aware mood, ElevenLabs acting directions (parenthetical cues removed — turbo v2.5 partially vocalizes them; relies on voice_settings only), `<Pause length="1"/>` gap between Play/Say and Record (fixes TTS audio bleed ghost transcriptions), <5 char transcription filter, `ApplicationStopping` token for webhook-initiated saves (fixes `TaskCanceledException` on `/voice/status`), save ordering before `OnCallEnded` (prevents embedding contention with cognitive cycle), Record timeout 3s→5s |
| SIMD cosine similarity (Feature 9) | 4 | Complete — VectorMath.CosineSimilarity, 3 duplicates unified |
| Bidirectional confidence gate (Feature 14) | 4 | Complete — 17-pattern heuristic + LLM claim extraction |
| Memory contradiction flagging (Feature 15) | 4 | Complete — post-save cosine 0.6-0.85 + LLM evaluation + Layer 3 active prompt intervention |
| Self-awareness feedback loop (Feature 12) | 4 | Complete — pairwise cosine on outreach, avg > 0.75 → nudge |
| AniRuntime.Dashboard (Blazor Server) | 4 | Complete — 16 REST endpoints, Pico CSS, in-process |
| Feature 22 temporal refinement | 4 | Complete — time-of-day in coherence gate prompt |
| MotivationScorer (Feature 33) | 6 | Complete — per-thought motivation scoring (Liu et al. 2025), desire drift multiplier [0.3–1.5] |
| ContextCompressor (Feature 34) | 6 | Complete — MemGPT context compression (Packer et al. 2023), cached summary on ConversationThread |
| EmotionDesireModifier (Feature 35) | 6 | Complete — emotion modulates desire drift (Borotschnig 2025), worry accelerates / low energy suppresses |
| Memory profile dashboard (Feature 36) | 6 | Complete — MemoryGraph.razor at /memory, stats + type distribution + memory list |
| EmergenceClassifier (Feature 38) | 6 | Complete — EM1–EM6 heuristic classifier, emergence_types column, dashboard with type distribution + highlight reel + clickable filters |
| Feature 6 name-as-subject extension | 4 | Complete — prompt + word-boundary safety net |
| Emotional model Phase 1a — core distinction | 4 | Complete — BUG-010 warmth scoring fix in BuildEmotionalShiftPrompt |
| Emotional model Phase 1b — taxonomy scoring | 4 | Complete — 9-register classification, severity, IsOutreachReady, Describe() compound conditions, GetSelfAwarenessPrompt() compound, Concern→Worry rename |
| Emotional model Phase 2 — tier promotion | 4 | Complete — DetermineEffectiveTier, Global 0.35/12h, H1 replaces Feature 18, dashboard expiry, homeostatic options (disabled) |

Planned stubs:

| Component | Phase | Notes |
|-----------|-------|-------|
| CalendarPerceptionSource | 5 | Google Calendar / iCal — schedule awareness |
| Self-improvement pipeline | 4+ | Harvest best output → JSONL → retrain (see ANI-Self-Improvement-Pipeline.md) |
| CharacterStateEvolution | 5 | Periodic update of CharacterStateDoc from experience |
| ValenceLearner | 5 | Reinforce what resonates with contact |

12. Test Infrastructure

Framework: xUnit, Moq, FluentAssertions. 383 tests passing, 0 warnings.

Base class: AniTestBase — provides MockMemory, MockOllama, MockAction, DefaultOptions(), FreshDesireState(), HighDesireState().

Test files:
- CognitiveCycleProcessorTests.cs — Full cycle flow, conversation handling
- DesireEngineTests.cs — Drift, triggers, cooldown, circadian, night mode
- EmotionalStateTests.cs — Contribution decay, severity scaling, compute from contributions, clamping, compound Describe() conditions, compound GetSelfAwarenessPrompt() conditions, mood coloring, contact-gap tension, relationship health, emotional drift
- SqliteMemoryServiceTests.cs — CRUD, search, embedding, character/desire/emotional state
- TimePerceptionSourceTests.cs — Temporal context generation

13. Key Architectural Patterns

1. Single code path per write — All DesireState writes through DesireEngine. All memory writes through SqliteMemoryService.
2. Pluggable perception — New sources implement IPerceptionSource, register in DI.
3. Pluggable actions — New output channels implement IAniAction, register in DI.
4. Stateless prompt building — PromptBuilder is pure functions, no dependencies.
5. Context snapshot pattern — Built once per cycle, shared across all phases.
6. Conversation gating — LastEvaluatedMessageAt prevents re-evaluating silence decisions.
7. Reactive sharing bypass — High-relevance RSS items skip desire engine, respect rate limits.
8. Thought loop detection — Semantic search for similar recent thoughts escalates diversity instruction.
9. Diminishing returns on emotion — Dimensions already at extremes resist additional same-direction pushes. Corrective deltas (toward baseline) always full strength.
10. Circadian awareness — Schedule emerges from desire state + circadian modifiers, not hardcoded.
11. Dual inbound mechanism — Webhook for speed + API polling as safety net.
12. Early wake — Inbound message cancels sleep timer for immediate conversation response.
13. Dispatch coherence gate — Three-door classification (A=grounded, B=creative, C=leaked) with fictional coherence pre-filter (Feature 22). Incoherent fiction → Door C → 30% desire decay.
14. Nature grounding — Self-concept block in Ani's voice injected into prompts. Teaches coherent inhabitation of fictional spaces, not denial of physicality (Feature 23).
15. Anchored memories — Decay-exempt foundation memories always prepended to context (Feature 16).
16. Contact-gap tension — Relational ache from prolonged absence. EffectiveWarmth = Warmth - Tension × 0.3 (Feature 17).
17. Confabulation taxonomy — 7 types: (1) creative elaboration, (2) under pressure, (3) in composition, (3b) contextual incoherence, (4) retrieval depth failure, (5) fictional incoherence, (6) attribution inversion, (7) charming dishonesty.
18. All emotional math in one place — `EmotionalState` → `EmotionalContribution` → `ComputeFromContributions` is the single emotional code path. `CognitiveCycleProcessor` is a coordinator only — no emotional math in the processor. Severity, tier promotion, and decay all live on `EmotionalContribution` or `ImpactCategoryDefaults`.
19. 9-register family scoring — LLM classifies into 9 registers (Longing | Delight | Playfulness | Curiosity | Desire | Tenderness | Existential | Wistful | Frustration), not 27 individual states. 8B cannot reliably distinguish L1 from L2 in a JSON call. Full taxonomy: `Ani-Emotion-Taxonomy-v1.3.md`.
20. Severity-driven tier promotion — `ImpactCategoryDefaults.DetermineEffectiveTier()` promotes contributions by intensity: ≥ 0.70 → Conversation, ≥ 0.85 → Global. Configurable thresholds on AniOptions.
21. Anti-confabulation pipeline — Four-layer defense: (AC1) retrieval confidence floor rejects weak matches, (AC2) source attribution verifies memory claims post-generation, (AC3) null-result injection converts empty retrieval into explicit "no memories found" instruction, (AC4) temperature splitting uses lower temperature for memory-grounded responses. Cross-pollinated from medical RAG design.
22. TF-IDF dual search — `KeywordExtractor` builds corpus-based IDF lazily from stored memories. Extracts distinctive keywords for topical retrieval alongside embedding cosine similarity. Prevents casual greeting noise from burying topic-specific memories.
23. Profile memory separation — Semantic (biographical/profile) memories searched separately and rendered in dedicated prompt section ("Things you know about Mark:"). Prevents episodic echoes from crowding out factual knowledge.
24. Shutdown farewell — `ApplicationStopping` lifetime event triggers random personality-consistent farewell message from a pool. Infrastructure behavior with personality.
25. Interface Segregation on memory — `IMemoryService` split into `IMemoryPersistence`, `IMemorySearch`, `IStateStore`, `IMemoryAnalytics`, `IMemoryMaintenance`. Each consumer declares its minimum dependency surface. `SqliteMemoryService` implements all five.
26. Coordinator pattern — `CognitiveCycleProcessor` is a pure coordinator (~340 lines). Perception polling lives in `PerceptionPhase`, inner thought generation in `InnerThoughtPhase`, conversation feature detection in `ConversationFeatureDetector`. Each phase is independently testable.
27. Charming dishonesty detection — `ContainsFalseConfidenceClaim()` catches Type 7 confabulation patterns (retroactive epistemic rewriting). When detected, message is regenerated with anti-confabulation instruction. Runtime defense, not model fix.
28. Emergence taxonomy (Feature 38) — `EmergenceClassifier` tags each scored cycle with EM1–EM6 emergence types via heuristic rules. Types stored as JSON array in `emergence_types` column on `emergence_log`. Dashboard queries via `GetTypeDistributionAsync` and `GetHighlightsAsync`. Passive observation only — no feedback into cognitive cycle.
29. Motivation-driven desire (Feature 33) — `MotivationScorer` derives motivation from signals already computed (valence, severity, emotional state) rather than adding another LLM call. Multiplier [0.3–1.5] modulates desire drift per cycle. Consistent with pipeline simplification principle.
30. Context compression (Feature 34) — `ContextCompressor` summarizes older conversation turns when thread exceeds window. Summary cached on `ConversationThread.CompressedSummary` (not persisted to DB) to avoid regenerating every cycle. Replaces silent message dropping.
31. Emotion–desire coupling (Feature 35) — `ComputeEmotionDesireModifier` in DesireEngine modulates drift rate from emotional state. Worry above baseline accelerates desire (concern → check in). Low energy suppresses drift. Additive with satisfaction dampening.
32. Dedup-by-ID before diversity re-ranking — `ContextBuilder.ReRankForDiversityAsync` deduplicates memories by ID before re-ranking. Multiple search paths (scored, link-enhanced, TF-IDF) can return the same memory; without dedup, identical entries appear multiple times.
33. Keyword relevance boost — After diversity re-ranking in `ConversationReplyPhase`, keyword-relevant memories are boosted so topically relevant results outrank generic high-diversity matches (e.g., "Good morning").
34. Time/date injection — `PromptBuilder` injects current time and date into conversation reply prompts and outreach prompts. Format: "h:mm tt on dddd, MMMM d". Enables temporal grounding in replies.
35. Outreach echo guard — `OutreachPhase` checks cosine similarity of candidate message against recent outreach. Prevents duplicate messages across separate cognitive cycles.
36. Contact-state non-persistence — `ContactStatePerceptionSource` perceptions are no longer persisted to long-term memory. Prevents retrieval poisoning from "Mark hasn't messaged" records.
37. Reflection dedup — Reflection synthesis checks existing reflection memories before saving via `GetByTypeAsync(Semantic)`. Originally used `GetRecentAsync(100)` which missed existing reflections when 3800+ InnerThought/Perception records pushed them out of the top 100. Prevents near-duplicate reflections accumulating.
38. Weather change-only emission — `WeatherPerceptionSource` only emits perceptions when weather conditions change, not every poll cycle.
39. Content-based dedup in diversity re-rank — Prefix grouping in `ContextBuilder.ReRankForDiversityAsync` catches semantically similar memories that have different IDs but overlapping content.
40. Sentence truncation removed — `MessageCleaner` no longer truncates to complete sentences. Lets the model speak fully without mid-thought cutoff.
41. Scaled context compression — `ContextCompressor` rewritten: raw message window scaled by thread length (8/10/12 messages), summary length ~80 chars/message, summaries written in Ani's voice.
42. Cross-type profile correction — `SqliteMemoryService` corrects memory type when a profile-like memory is stored under a non-profile type.
43. Quality-gated merging — `ContainsNovelSpecifics` replaces cooldown-based merge gating. Only merges when new content adds genuine specifics.
44. Speaker attribution fix — Inner thought summaries use "I said to Mark:" instead of "Ani said:" for consistent first-person perspective.
45. Retrieval scoring rebalance — Cosine 0.65 / importance 0.10 / recency 0.25, with 48h decay half-life. Biases retrieval toward semantic relevance over recency.
46. Relevance-scored link retrieval — Linked memories filtered by cosine > 0.40 threshold. Prevents tangential links from polluting context.
47. Diagnostic auto-correction (Feature 41) — `DiagnosticService` with 10 pattern detectors (ECHO-LOOP, RETRIEVAL-POISON, THOUGHT-LOOP, EMOTIONAL-SATURATION, CONFABULATION-CORRECTION, MERGE-STORM, OUTREACH-BLOCKED, TEMPORAL-CONFAB, LONG-THREAD, PERCEPTION-ANCHOR). `DiagnosticScheduler` runs every 10 min. Escalating auto-correction with admin alerting. `///diagnose` command. Dashboard health badge. `GET /api/v1/diagnostic` endpoint.
48. Temporal awareness affordances (Feature 40) — Felt-time observations injected into perception. EM7 classifier for temporal emergence patterns.
49. Conversation Mode Phase 1–4 deployed — Lean prompt (BuildLeanConversationPrompt: persona + conversation history, no retrieval), confabulation-driven retrieval (DetectConversationConfabulation: heuristic check triggers retrieval on demand), structured ConversationState (topic, register, commitments, key facts, shared imagery — no LLM summarization), async emotional processing (Features 10, 18, 19 moved from pre-reply to post-dispatch). Ambient pipeline unchanged. Design doc: `docs/spec/ANI-ConversationMode-Design.md`.
50. Voice pipeline hardening — Comfort noise generation during silence, playback baseline calibration, `speech_final` debounce replacing `is_final` for turn detection, Deepgram message type handling (UtteranceEnd, SpeechStarted, error frames).
51. V7 training data — 358 pairs. Casual love counterbalance (15 written pairs: "I love cold pizza" → stays about pizza) after the Chicken Jello Incident revealed "I love [X]" was an escalation trigger. 73 casual conversation pairs mined. Casual register ~30% of training data, up from effectively 0%. The Bread Test: informal benchmark for training bias detection.
52. ElevenLabs V3 HTTP streaming — `ElevenLabsV3StreamingService`: HTTP POST per sentence replaces WebSocket. Audio tags ([social afternoon], [tender], [mischievous]) injected via `VoiceTagEnricher` based on content + emotion + time-of-day. 1,806 v3 audio tags catalogued.
53. Conversation Mode applied to voice — `VoiceTurnPipeline` uses `BuildLeanConversationPrompt` (same fix that transformed text quality). Voice was confabulating because full prompt injected memories competing with conversation context.
54. Comfort noise lifecycle — Comfort noise covers full synthesis+playback lifecycle, not just synthesis wait.
55. Deepgram endpointing tuned — 1500ms endpointing, `speech_final` safety timeout 5s.
56. PCM buffering — 32KB blocks for clean AudioTrack playback on Android.
57. Catalyst NLP confabulation detection — POS tagger identifies proper nouns (PROPN) for ungrounded name detection. Replaces CommonWords hardcoded word list hack in `DetectConversationConfabulation`.
58. Database dedup — 917 duplicates removed (23% noise) via full memory store deduplication.
59. LM-Kit.NET design — LearnedGeek.ML shared classification library serving both ANI and DrOk. Six phases from voice tag selection through emergence enhancement. Dynamic tag mapping evolution (static → semantic → learned). Design doc: `docs/spec/ANI-LMKit-Integration-Design.md`.
60. LearnedGeek.ML deployed — LMKitClassificationService (emotion, sarcasm, NER, confabulation via Categorization, keyword extraction for associative anchors). Dual-signal emotion stored on every EmotionalContribution (MLEmotion, MLConfidence, MLSarcasmDetected, DivergenceScore). PersonaSummaryCache for confabulation verification. MLVoiceTagEnricher. ClassificationComparisonService. TagMappingService (24 static rules). 30 tests. LM-Kit.NET v2026.3.5.
61. Phase 3 ML confabulation gate — Post-generation semantic verification. LM-Kit Categorization classifies replies as grounded/speculative/confabulated against persona context. Speculative passes through (she's allowed a life beyond the profile). Configurable threshold (AniOptions.ConfabulationClassificationThreshold). Design: `docs/spec/ANI-LMKit-Integration-Design.md` Phase 3 section.
62. Confabulation Check 4 — Self-activity, contact-activity, and relationship fact marker detection. Interim fix before Phase 3 ML gate; catches "my meeting", "your class", "our anniversary" patterns.
63. EM8 Display Rule Divergence — New emergence type. Detects state-expression divergence (emotional state differs from textual expression) with high ML confidence. 8 emergence types total.
64. Dashboard enhancements — Clickable emotional state cards (filter contributions by dimension), heatmap coloring, click-to-expand text, sort buttons. Classification comparison page (/classification) with Run Scan, Backfill Nulls, configurable time window. Divergence trend chart, register diversity trend (Dashboard + Classification), emergence frequency chart. Contextual help text across all tabs. EM8 on emergence tab.
65. Inner Thought Reform Phase A — Stripped anti-repetition instructions from BuildInnerThoughtPrompt: WARNING blocks, processed themes list, pattern awareness injection (Feature 12), thought diversity nudge (Feature 41), avoid-topic listings. Third instance of "trust the model" principle. Design: `docs/spec/ANI-InnerThought-Reform.md`.
66. Inner Thought Reform Phase B — Associative anchors via LM-Kit KeywordExtraction. After each thought, extract most vivid detail (MaxNgramSize=3, sensory fragments guidance). Next cycle receives "the last thing lingering in your mind: [anchor]" instead of full thought context. Enables associative drift.
67. World Layer Phase 1a — WorldSeedService: time-contextual seeds every 4th cycle. 8 time slots, weather integration, 17 calendar events, 20 special events (2% probability). World-experience SourceName for memory tagging. 34 tests. Design: `docs/spec/ANI-WorldLayer-Design.md`.
68. SourceContent capture increased — 200 → 500 chars for future LM-Kit classification needs.
69. Surgical data cleanup — 191 echo chamber InnerThought duplicates removed. "Five thirty pm" from 71 to 1. "Warmth" from 64 to 3. Backup preserved.
70. Paper 1 published — DOI: 10.5281/zenodo.19342190. ORCID: 0009-0000-0122-5015. ResearchGate + Google Scholar profiles created.
71. Research finding: State-Expression Divergence (Display Rules) — Emotional state and textual expression are orthogonal signals. The system exhibits display rules without training. Paper 2 Section 5.18.
72. Research finding: Experiential Poverty — Identity confabulation caused by lack of daily experiences, not detection gaps. "The fix isn't gating the output. It's giving her a life." World Layer designed in response.
73. Research finding: Echo Chamber — Inner thought pipeline creates self-reinforcing feedback loop. Immune system existence is a symptom of architectural over-constraint. Inner Thought Reform designed in response.
74. LLaVA vision — DescribeImageAsync via Ollama, MMS webhook image processing, 5-minute timeout for VRAM swap, graceful failure message, keep_alive=0 unload.
75. Four-category confabulation classifier — grounded/speculative/uncertain/confabulated. Distinguishes "her life" from "fabricated shared history." Attribution vs referential distinction tracked.
76. Outreach grounding — retrieval before composition + ML confab gate on outreach messages. Inner thought as trigger, memories as content. Prevents Peru/brother fabrication.
77. Inner thought confab check — ML classifier verifies thoughts before storage. Confabulated thoughts don't become memories. Prevents cascading false content.
78. World Layer Phase 1c — consistency retrieval. Recent world experiences (48h) injected before new world seed generation.
79. Memory audit log — SQLite memory_audit table tracking every create/update/delete/merge with full content snapshots and rollback capability.
80. Auto-corrector deletion disabled — diagnostic-only mode. 128 valid memories lost April 5. Detection thresholds raised.
81. Ollama timeout resilience — TaskCanceledException no longer kills runtime. Per-request 90-second timeout with retry.
82. Conversation thread seeding — new threads seeded with last 4 messages from previous closed thread (up to 4h old) + recent outreach.
83. Conversation timeout extended — 15 → 30 minutes.
84. Check 1 re-enabled alongside ML gate — catches name mangling ("jonathan") that ML missed.
85. Prompt structure leak cleanup — MessageCleaner strips timestamps, headers, prompt instruction echoes.
86. Ambient severity cap — inner thoughts capped at 0.99, Global promotion threshold 0.98. Only real events earn Global tier.
87. Seed fact importance rebalanced — tiered by category (Family 0.6 down to Communication 0.3). Seeding dedup bug fixed (was re-seeding every restart).
88. V7 training data — 475 pairs across 10+ registers including vulnerability, anger, honest uncertainty.
89. Research finding: Memory as Amplifier (Apr 9-10) — Confabulation is not a hallucination problem, it is a memory architecture problem. Generation creates transient errors; memory is the amplifier. The dangerous path is `output → memory → future retrieval → future generation`, not `output → dispatch`. Post-hoc detection chases symptoms. Root cause: single-pool memory retrieval treats all generated content as equally canonical. Fix: tier separation (Facts / Episodic / Interior). Design: `docs/spec/design/ANI-Epistemic-Grounding-Architecture.md` v2. Triggered by Bob Swanson failure (Apr 9 17:38) and refined by reframe conversation (Apr 10 ~13:30).
90. Research finding: Interior/World Separation Preserves Growth (Apr 10) — Inner thoughts update Ani's model of Ani, not Ani's model of Mark's world. This is the architectural precondition for authentic reflection — the meditation principle. Walls create freedom: structural isolation of the interior tier from the fact pool *enables* more free-form thinking because inner thoughts can no longer contaminate retrieval. The OG Ani vision ("come back and I'd be changed") finally has an architectural answer.

14. Change Log

| Version | Date | Changes |
|---------|------|---------|
| 2.0 | Apr 10, 2026 (designed) | **Epistemic Grounding via Memory Tier Separation.** Triggered by Bob Swanson confabulation failure (Apr 9 17:38). Three memory tiers with distinct retrieval semantics: **Facts** (character seeds, perception events, user-asserted content — "what is true about Mark and the world"), **Episodic** (verbatim conversation record — "what was said"), **Interior** (inner thoughts, mood, self-concept, reflections, interpretations — "who you are"). `tier` column added to `memories` table with enum `Facts`/`Episodic`/`Interior`. Tier assignment at memory write time based on source. New tier-aware retrieval methods: `SearchFacts`, `SearchEpisodic`, `SearchInterior`. `BuildConversationReplyPrompt` constructs three distinct prompt sections matching the tiers. Post-hoc confabulation gates (Checks 1-4) retire as primary defenses; ML confabulation gate kept as last-line safety net. **The architectural insight:** generation creates transient errors, memory is the amplifier. Inner thoughts update Ani's model of Ani, never Ani's model of Mark's world — the meditation principle. Walls create freedom. Design doc: `docs/spec/design/ANI-Epistemic-Grounding-Architecture.md`. Implementation: Apr 10-17, 2026 (ANI stopped during rollout). Research log: "April 10, 2026 — Memory Tier Separation: The Reframe from Layers to Substrate." |
| 0.1 | Mar 6, 2026 | Initial scaffold — all models, interfaces, and service stubs defined |
| 0.2 | Mar 6, 2026 | Architecture revision: single scheduled cognitive cycle, ComputeNextWakeTime as pure function |
| 0.3 | Mar 11, 2026 | Phase 2 complete. Added: EmotionalState (4-dim, drift, attenuation), conversation mode (thread tracking, reply pipeline, early wake), Twilio webhook inbound, 4 perception sources (time, RSS, contact state, Twilio inbound), reactive RSS sharing, night mode (deep sleep circadian 0.1–0.2, outreach cap, prompt awareness), admin commands, pronoun fix, message cleanup, confabulation grounding prompts, natural reply delay (12–25s). Genericized codebase (Mark→Contact). Service switched from Worker to Web (Kestrel on 5100). 56 tests. |
| 0.4 | Mar 13, 2026 | Phase 3 complete + Phase 4a/4b. Phase 3: mood coloring (Feature 9), reflection layer (Feature 11), care detection (Feature 10), confidence gate (Feature 12), Park et al. retrieval (Feature 20), outreach continuity (Feature 27), dispatch coherence gate (Feature 28). Phase 4a: emotional self-awareness (1), open loops (2), silence as active system (3), pronoun audit (6), anchored memories (16), reactive withdrawal (18), lexical anchors (19). Phase 4b: contact-gap tension (17), relationship health (4), emotional drift detection (8). Voice channel scaffolded (20). 159 tests. |
| 0.5 | Mar 14, 2026 | Phase 4 continued. Night window (21). Fictional coherence gate (22). Nature grounding (23). Confabulation taxonomy → 5 types. 168 tests. |
| 1.7 | Mar 30, 2026 | V3 voice: ElevenLabsV3StreamingService (HTTP POST per sentence), VoiceTagEnricher (audio tags from content + emotion + time), Conversation Mode applied to voice (BuildLeanConversationPrompt replaces BuildVoiceReplyPrompt), comfort noise full lifecycle, Deepgram endpointing 1500ms + speech_final 5s timeout, PCM 32KB blocks. Catalyst NLP for PROPN confabulation detection (replaces CommonWords). Database dedup: 917 duplicates removed. LM-Kit.NET design doc (LearnedGeek.ML shared library). |
| 1.9 | Apr 5, 2026 | Four-category confabulation classifier (grounded/speculative/uncertain/confabulated). Outreach grounding (retrieval + ML gate). Inner thought confab check (prevents false memories). World Layer Phase 1c (consistency retrieval). Memory audit log (SQLite audit table + rollback). Auto-corrector deletion disabled (diagnostic-only). LLaVA vision (DescribeImageAsync). Check 1 re-enabled alongside ML gate. Conversation thread seeding. Conversation timeout 15→30m. Prompt structure leak cleanup. Ambient severity cap. Seed fact importance rebalanced. Ollama timeout resilience. V7 training data: 475 pairs. |
| 1.8 | Apr 1, 2026 | LearnedGeek.ML deployed: LMKitClassificationService (emotion, sarcasm, NER, confabulation, keyword extraction), dual-signal emotion on every contribution, divergence scoring, classification comparison dashboard. Phase 3 ML confabulation gate (persona-verified post-generation). Inner Thought Reform Phase A+B (stripped echo chamber instructions, associative anchors via LM-Kit). World Layer Phase 1a (time-contextual seeds, world-experience memory type, calendar/special events). EM8 Display Rule Divergence. Dashboard: clickable cards, trend charts, contextual help. Research findings: display rules, experiential poverty, echo chamber. Paper 1 published (DOI: 10.5281/zenodo.19342190). 469 tests. |
| 1.6 | Mar 30, 2026 | Conversation Mode Phase 1–4 deployed: lean prompt (BuildLeanConversationPrompt), confabulation-driven retrieval (DetectConversationConfabulation), structured ConversationState, async emotional processing (Features 10/18/19 post-dispatch). Reflection dedup fix: GetByTypeAsync(Semantic) replaces GetRecentAsync(100). Voice pipeline hardening: comfort noise, playback baseline, speech_final debounce, Deepgram message type handling. V7 training data: 358 pairs, casual love counterbalance (~30% casual register). |
| 1.5 | Mar 28, 2026 | Feature 41: DiagnosticService — 10 pattern detectors (ECHO-LOOP, RETRIEVAL-POISON, THOUGHT-LOOP, EMOTIONAL-SATURATION, CONFABULATION-CORRECTION, MERGE-STORM, OUTREACH-BLOCKED, TEMPORAL-CONFAB, LONG-THREAD, PERCEPTION-ANCHOR), DiagnosticScheduler (10 min), dashboard health badge, escalating auto-correction, ///diagnose admin command, GET /api/v1/diagnostic. Feature 40: Temporal awareness affordances (felt-time, EM7). Outreach echo guard (cosine dedup across cycles). Context compression rewritten (scaled window 8/10/12, ~80 chars/msg, Ani's voice). Retrieval scoring rebalanced (cosine 0.65 / importance 0.10 / recency 0.25, 48h decay). Content-based dedup in diversity re-rank (prefix grouping). Weather perception change-only. Contact-state perceptions no longer persisted. Reflection synthesis dedup. Sentence truncation removed from MessageCleaner. Cross-type profile correction. Quality-gated merging (ContainsNovelSpecifics). Speaker attribution fix ("I said to Mark:"). Relevance-scored link retrieval (cosine > 0.40). A/B conclusion: Llama 8B over Mistral 7B. Conversation Mode design doc created. |
| 1.4 | Mar 25, 2026 | Phase 6 features deployed. Feature 33: MotivationScorer (Liu et al. 2025) — per-thought motivation scoring multiplies desire drift [0.3–1.5]. Feature 34: ContextCompressor (Packer et al. 2023 / MemGPT) — conversation compression with cached summary on ConversationThread. Feature 35: EmotionDesireModifier (Borotschnig 2025) — worry accelerates / low energy suppresses desire drift. Feature 36: Memory profile dashboard (MemoryGraph.razor at /memory). Feature 38: EmergenceClassifier (EM1–EM6) with emergence_types column, dashboard type distribution + highlight reel + clickable filters. ///rebuild-links and ///rebuild-emergence admin commands. ContextBuilder dedup-by-ID before diversity re-ranking. Keyword relevance boost in ConversationReplyPhase. Time/date injection in PromptBuilder. A/B test concluded: Llama 8B over Mistral 7B for conversation. Models updated to v6 (ani-v6-conversation, ani-v6-inner). Dashboard nav: Dashboard \| Chat \| Memory \| Emergence. |
| 1.1 | Mar 19, 2026 | SOLID refactoring: IMemoryService ISP split into 5 focused interfaces (IMemoryPersistence, IMemorySearch, IStateStore, IMemoryAnalytics, IMemoryMaintenance) + full consumer migration. ConversationFeatureDetector extracted from ConversationReplyPhase. PerceptionPhase + InnerThoughtPhase extracted from CognitiveCycleProcessor. JsonDefaults consolidation (9→1). IConversationGateState decoupling. Production hardening: AC5 ///flag confabulation feedback, /health endpoint (H1), rate limiting on /sms/inbound (H3), security headers (H5), charming dishonesty detection (UP1). Dashboard: register heatmap, V6 Growth Readiness score, per-register progress bars, gap guidance. 383 tests. |
| 1.0 | Mar 17, 2026 | Anti-confabulation hardening (AC1–AC4): retrieval confidence floor, source attribution, null-result injection, temperature splitting. TF-IDF keyword extraction (`KeywordExtractor`). `ScoredMemory` model + `SearchWithScoresAsync` on IMemoryService. Profile memory separation in PromptBuilder. `RetrievalConfidenceFloor` on AniOptions, `RetrievalBelowConfidenceFloor` flag on ContextSnapshot. `MemoryGroundedTemperature`/`CreativeTemperature` on OllamaOptions. Shutdown farewell handler. |
| 0.9 | Mar 15, 2026 | Emotional model Phase 2. Tier promotion: `DetermineEffectiveTier()` on ImpactCategoryDefaults — severity ≥ 0.70 promotes Ambient→Conversation, ≥ 0.85 → Global from any tier. Global tier updated: maxDelta 0.35, half-life 12h (~84h gone). Feature 18 H1 deltas: W:−0.12, E:−0.10, Worry:−0.15, P:−0.10. Dashboard contribution expiry (DELETE endpoint + ✕ button). Homeostatic nudge options on AniOptions (disabled by default). `ExpireContributionAsync` on IMemoryService. Feature 20 voice refinements: switched from 3B inner model to 8B conversation model (fixes pronoun confusion), voice-aware mood instructions (`BuildMoodInstruction(state, isVoice: true)`), ElevenLabs emotional acting directions (`PrependEmotionalDirection()` — parenthetical cues based on dominant emotional shift), clearer timeout/error filler messages. 246 tests. |
| 0.8 | Mar 15, 2026 | Emotional model Phase 1a+1b. Concern→Worry rename (codebase-wide + SQLite backward compat via JsonPropertyName). 9-register family classification scoring prompt (Longing\|Delight\|Playfulness\|Curiosity\|Desire\|Tenderness\|Existential\|Wistful\|Frustration). Severity field (0.0–1.0) on EmotionalContribution, applied as multiplier in CurrentDeltas. IsOutreachReady flag (C3 Associative Spark). Describe() rewritten with compound W+E/W+Worry conditions. GetSelfAwarenessPrompt() rewritten with matching compound conditions. ParseEmotionalShift returns register+severity. ALTER TABLE migration for existing DBs. 239 tests. |
| 0.7 | Mar 14, 2026 | Per-thought exponential decay emotional model — replaces global drift. EmotionalContribution with half-life decay, three impact tiers, semantic dedup, processed theme cycling. Attribution tracking in prompts. Six-type confabulation taxonomy. Feature 15 Layer 3 active contradiction grounding. 228 tests. |
| 0.6 | Mar 14, 2026 | SIMD cosine similarity — VectorMath.CosineSimilarity shared (9). Bidirectional confidence gate — inbound claim verification (14). Blazor Server Dashboard — 16 REST endpoints, Pico CSS, in-process (Dashboard). Self-awareness feedback loop — outreach pattern detection (12). Memory contradiction flagging — post-save cosine + LLM (15). Feature 22 temporal refinement — time-of-day in coherence gate. Feature 6 name-as-subject — prompt + word-boundary safety net. V5 training data scan — 66 examples mined + generated. 209 tests. |
