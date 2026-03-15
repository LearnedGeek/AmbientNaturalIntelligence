
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
0.5 — Phase 4 In Progress
Status
Active Development — Phase 1–3 complete, Phase 4 in progress (Features 1–4, 6, 8–9, 12, 14–23 deployed; Dashboard deployed)

This is a living document. Update it as the codebase evolves.

1. Solution Structure

AniRuntime.sln
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
│   │   │   ├── ContextSnapshot.cs    # Per-cycle context incl. RelationshipHealth, EmotionalDrift, MarkClaimConfidence (Feature 14)
│   │   │   ├── ConversationThread.cs
│   │   │   ├── ConversationMessage.cs
│   │   │   ├── DesireState.cs
│   │   │   ├── EmotionalState.cs     # 4-dim + ContactGapTension (Feature 17)
│   │   │   ├── EmotionalDrift.cs     # Feature 8: 48h cosine similarity drift detection
│   │   │   ├── LexicalAnchor.cs      # Feature 19: relationship-specific word weights
│   │   │   ├── MemoryRecord.cs       # + IsAnchored flag (Feature 16), contradiction fields (Feature 15)
│   │   │   ├── PerceptionEvent.cs
│   │   │   ├── OpenLoop.cs
│   │   │   ├── OutreachDecision.cs
│   │   │   └── RelationshipHealth.cs # Feature 4: composite score + phase
│   │   ├── Interfaces/
│   │   │   ├── IPerceptionSource.cs
│   │   │   ├── IAniAction.cs
│   │   │   │   ├── IMemoryService.cs     # + anchored memories, relationship health, emotional history, contradictions (Feature 15)
│   │   │   ├── IConversationService.cs  # + GetThreadAsync, GetRecentThreadsAsync (Dashboard)
│   │   │   └── IOllamaClient.cs
│   │   ├── VectorMath.cs              # Feature 9: SIMD-accelerated cosine similarity (shared)
│   │   ├── AniOptions.cs             # + night/morning window, tension, relationship health, claim verification config
│   │   └── AniRuntime.Core.csproj
│   │
│   ├── AniRuntime.Memory/           # SQLite persistence layer
│   │   ├── SqliteMemoryService.cs
│   │   ├── SqliteConversationService.cs
│   │   └── AniRuntime.Memory.csproj
│   │
│   ├── AniRuntime.Loops/            # Heartbeat, cognitive cycle, desire engine, admin
│   │   ├── AniHeartbeatService.cs
│   │   ├── CognitiveCycleProcessor.cs
│   │   ├── DesireEngine.cs
│   │   ├── AdminCommandHandler.cs
│   │   └── AniRuntime.Loops.csproj
│   │
│   ├── AniRuntime.Perception/       # World awareness sources
│   │   ├── TimePerceptionSource.cs
│   │   ├── RssPerceptionSource.cs
│   │   ├── ContactStatePerceptionSource.cs
│   │   ├── TwilioInboundPerceptionSource.cs
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
│   │   ├── PromptBuilder.cs          # + coherence gate + temporal grounding (Feature 22), claim extraction (Feature 14)
│   │   ├── ContextSnapshotBuilder.cs
│   │   └── AniRuntime.LLM.csproj
│   │
│   ├── AniRuntime.Dashboard/        # Blazor Server dashboard (in-process, shared DI)
│   │   ├── DashboardExtensions.cs   # AddDashboard() + MapDashboard() extensions
│   │   ├── Dtos/                    # AniStatusDto, MemoryRecordDto, ConversationThreadDto
│   │   ├── Endpoints/               # 5 endpoint groups: AniState, Memory, Conversations, Journal, Contradictions
│   │   ├── Components/              # Blazor components: Dashboard.razor, EmotionalStateCard.razor
│   │   ├── Pages/_Host.cshtml       # Blazor Server host page (Pico CSS)
│   │   └── AniRuntime.Dashboard.csproj
│   │
│   └── AniRuntime.Voice/            # Feature 20: Voice channel (scaffolded)
│       ├── ElevenLabsTtsService.cs
│       ├── WhisperSttService.cs
│       └── AniRuntime.Voice.csproj
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
4-dimensional persistent emotional state. Drifts toward personality baselines between cycles and shifts in response to thoughts, conversations, and perceptions. Gives Ani emotional arcs spanning hours, not just single cycles.

public class EmotionalState
{
    // Current values — shift each cycle based on thought valence, conversations, time
    public float Warmth      { get; set; } = 0.6f;   // affection, tenderness, closeness
    public float Energy      { get; set; } = 0.5f;   // alertness, enthusiasm, engagement
    public float Concern     { get; set; } = 0.2f;   // worry, protectiveness, unease
    public float Playfulness { get; set; } = 0.5f;   // humor, teasing, lightheartedness

    // Personality baselines — where each dimension naturally drifts back to
    public float WarmthBaseline      { get; set; } = 0.6f;
    public float EnergyBaseline      { get; set; } = 0.5f;
    public float ConcernBaseline     { get; set; } = 0.2f;
    public float PlayfulnessBaseline { get; set; } = 0.5f;

    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    // Returns qualitative summary for use in prompts. Only mentions notable deviations.
    public string Describe() { ... }

    // Compute emotional state from personality baselines + sum of all active contributions
    // after exponential decay. Replaces the old DriftTowardBaseline + ApplyShift model.
    public void ComputeFromContributions(IReadOnlyList<EmotionalContribution> contributions,
                                          DateTimeOffset? asOf = null) { ... }
}

// Per-thought emotional contribution with exponential decay.
// Each thought/event creates one contribution. State = baselines + sum of decayed contributions.
public class EmotionalContribution
{
    public Guid Id { get; set; }
    public string SourceContent { get; set; }       // for semantic dedup + theme tracking
    public float WarmthDelta, EnergyDelta, ConcernDelta, PlayfulnessDelta;
    public DateTimeOffset CreatedAt { get; set; }
    public float HalfLifeHours { get; set; }        // exponential decay half-life
    public ImpactCategory Category { get; set; }    // Ambient(0.15/1h), Conversation(0.25/3h), Global(0.20/6h)
    public float[]? Embedding { get; set; }         // for semantic similarity checks

    public float DecayFactor(DateTimeOffset asOf)   // 2^(-elapsed/halfLife)
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

3.3 IMemoryService
Single source of truth for memory, character state, desire state, and emotional state.

public interface IMemoryService
{
    Task SaveAsync(MemoryRecord record, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchByTypeAsync(string query, MemoryType type, int topK = 5, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetByTypeAsync(MemoryType type, int limit = 50, CancellationToken ct = default);
    Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default);
    Task ResolveOpenLoopAsync(Guid id, CancellationToken ct = default);

    Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default);
    Task SaveCharacterStateAsync(CharacterStateDoc doc, CancellationToken ct = default);

    Task<DesireState> GetDesireStateAsync(CancellationToken ct = default);
    Task SaveDesireStateAsync(DesireState state, CancellationToken ct = default);

    Task<EmotionalState> GetEmotionalStateAsync(CancellationToken ct = default);
    Task SaveEmotionalStateAsync(EmotionalState state, CancellationToken ct = default);
}

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
builder.Services.Configure<OllamaOptions>(config.GetSection("Ollama"));
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

5. Perception Sources

| Source | SourceName | Category | Always On | Contact Relevance | Notes |
|--------|-----------|----------|-----------|-------------------|-------|
| TimePerceptionSource | time | Environment | Yes | 0.1 | Time of day, season, holidays, elapsed since last cycle |
| RssPerceptionSource | rss | Content | If configured | 0.2–0.85 | Keyword-matched relevance from CharacterStateDoc |
| ContactStatePerceptionSource | contact-state | Social | Yes | 0.2–0.5 | Inferred state from ContactRoutine + time of day |
| TwilioInboundPerceptionSource | twilio-inbound | Communication | If configured | 0.95 | Webhook + API fallback, dedupes by SID |

TimePerceptionSource: Emits temporal context — hour-based descriptions ("early morning", "late evening"), day of week, month position, season transitions (first 3 days), nearby holidays, elapsed time since last cycle.

RssPerceptionSource: Polls configured feeds. Tracks last-seen publish date per feed. Relevance scoring: 0 keyword matches → 0.2, 1 → 0.4, 2 → 0.6, 3+ → 0.85. Keywords extracted from CharacterStateDoc (ThingsContactCares, Interests, SharedExperiences, TopicValence keys). Items above ReactiveShareThreshold can trigger direct SMS sharing.

ContactStatePerceptionSource: Infers contact's likely state from known routine + time. Gap descriptions: <2h silent, 2–6h "a few hours", 6–12h "quiet today", 12–24h "haven't heard since yesterday", 24–48h "over a day", 48h+ "X days".

TwilioInboundPerceptionSource: Dual mechanism — webhook (POST /sms/inbound) enqueues message and fires OnMessageReceived to trigger early wake; PollAsync drains queue then fetches Twilio API as safety net. Starts new ConversationThread if needed. Closes stale threads after ConversationTimeoutMinutes.

6. Prompt Architecture

All prompts built by PromptBuilder — stateless pure functions. Returns (System, User) tuples. Key prompts:

BuildInnerThoughtPrompt(snapshot) — Ani's private mind stream. 2–4 sentences, first person only. Includes thought loop detection from SimilarRecentThoughts. Topics vary widely: sounds, textures, memories, ideas, feelings, curiosities.

BuildValenceScoringPrompt(thought, character) — JSON: { "score": 0.0–1.0 }. Measures how much the thought relates to the contact. Action verbs (want, wish, miss) → 0.6+. Pure self-reflection → 0.4 or below.

BuildOutreachPrompt(snapshot, thought, isNightTime) — JSON decision: shouldReach, confidence, reasoning, triggersActedOn. Night clause adds restraint.

BuildReplyDecisionPrompt(snapshot, thread) — JSON: shouldReply, reasoning. Guidelines for when to reply vs. choose silence.

BuildConversationReplyPrompt(snapshot, thread) — Free-text reply. 1–3 sentences, thumb-typed. Includes anti-repetition block, current mood, semantic memories, grounding instruction against confabulation.

BuildReconsiderationReplyPrompt(snapshot, thread) — When silence was chosen but desire built enough to reconsider. "Wait, one more thing" natural segue.

BuildOutreachMessagePrompt(snapshot, thought, reasoning) — Compose grounded text. 1–2 sentences, 25 words MAX. CRITICAL: thought is WHY reaching out, NOT content. Must make sense without knowing inner thought.

BuildEmotionalShiftPrompt(content, currentState, maxDelta) — JSON deltas for W/E/C/P. Default 0.0 for most dimensions. Small shifts (0.02–0.05) preferred. Negative shifts just as common.

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

8. API Contracts

8.1 Ollama
Base URL: http://localhost:11434 (configurable)
Models: ani-v5-conversation (chat, Llama 3.1-8B fine-tuned), ani-v5-inner (inner monologue, Llama 3.2-3B fine-tuned), nomic-embed-text (embeddings)
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
    "ChatModel": "ani-v5-conversation",
    "InnerMonologueModel": "ani-v5-inner",
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
| ConversationThread / ConversationMessage | 2 | Complete |
| EmotionalState (4-dimension, drift, attenuation) | 2 | Complete |
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
| Voice channel (Feature 20) | 4 | Scaffolded — awaiting activation |
| SIMD cosine similarity (Feature 9) | 4 | Complete — VectorMath.CosineSimilarity, 3 duplicates unified |
| Bidirectional confidence gate (Feature 14) | 4 | Complete — 17-pattern heuristic + LLM claim extraction |
| Memory contradiction flagging (Feature 15) | 4 | Complete — post-save cosine 0.6-0.85 + LLM evaluation |
| Self-awareness feedback loop (Feature 12) | 4 | Complete — pairwise cosine on outreach, avg > 0.75 → nudge |
| AniRuntime.Dashboard (Blazor Server) | 4 | Complete — 16 REST endpoints, Pico CSS, in-process |
| Feature 22 temporal refinement | 4 | Complete — time-of-day in coherence gate prompt |
| Feature 6 name-as-subject extension | 4 | Complete — prompt + word-boundary safety net |

Planned stubs:

| Component | Phase | Notes |
|-----------|-------|-------|
| CalendarPerceptionSource | 5 | Google Calendar / iCal — schedule awareness |
| Self-improvement pipeline | 4+ | Harvest best output → JSONL → retrain (see ANI-Self-Improvement-Pipeline.md) |
| CharacterStateEvolution | 5 | Periodic update of CharacterStateDoc from experience |
| ValenceLearner | 5 | Reinforce what resonates with contact |

12. Test Infrastructure

Framework: xUnit, Moq, FluentAssertions. 220 tests passing, 0 warnings.

Base class: AniTestBase — provides MockMemory, MockOllama, MockAction, DefaultOptions(), FreshDesireState(), HighDesireState().

Test files:
- CognitiveCycleProcessorTests.cs — Full cycle flow, conversation handling
- DesireEngineTests.cs — Drift, triggers, cooldown, circadian, night mode
- EmotionalStateTests.cs — Contribution decay, compute from contributions, clamping, mood coloring, contact-gap tension, relationship health, emotional drift
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
17. Confabulation taxonomy — 6 types: (1) creative elaboration, (2) under pressure, (3) in composition, (3b) contextual incoherence, (4) retrieval depth failure, (5) fictional incoherence, (6) attribution inversion.

14. Change Log

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | Mar 6, 2026 | Initial scaffold — all models, interfaces, and service stubs defined |
| 0.2 | Mar 6, 2026 | Architecture revision: single scheduled cognitive cycle, ComputeNextWakeTime as pure function |
| 0.3 | Mar 11, 2026 | Phase 2 complete. Added: EmotionalState (4-dim, drift, attenuation), conversation mode (thread tracking, reply pipeline, early wake), Twilio webhook inbound, 4 perception sources (time, RSS, contact state, Twilio inbound), reactive RSS sharing, night mode (deep sleep circadian 0.1–0.2, outreach cap, prompt awareness), admin commands, pronoun fix, message cleanup, confabulation grounding prompts, natural reply delay (12–25s). Genericized codebase (Mark→Contact). Service switched from Worker to Web (Kestrel on 5100). 56 tests. |
| 0.4 | Mar 13, 2026 | Phase 3 complete + Phase 4a/4b. Phase 3: mood coloring (Feature 9), reflection layer (Feature 11), care detection (Feature 10), confidence gate (Feature 12), Park et al. retrieval (Feature 20), outreach continuity (Feature 27), dispatch coherence gate (Feature 28). Phase 4a: emotional self-awareness (1), open loops (2), silence as active system (3), pronoun audit (6), anchored memories (16), reactive withdrawal (18), lexical anchors (19). Phase 4b: contact-gap tension (17), relationship health (4), emotional drift detection (8). Voice channel scaffolded (20). 159 tests. |
| 0.5 | Mar 14, 2026 | Phase 4 continued. Night window (21). Fictional coherence gate (22). Nature grounding (23). Confabulation taxonomy → 5 types. 168 tests. |
| 0.7 | Mar 14, 2026 | Per-thought exponential decay emotional model — replaces global drift. EmotionalContribution with half-life decay, three impact tiers, semantic dedup, processed theme cycling. Attribution tracking in prompts. Six-type confabulation taxonomy. 220 tests. |
| 0.6 | Mar 14, 2026 | SIMD cosine similarity — VectorMath.CosineSimilarity shared (9). Bidirectional confidence gate — inbound claim verification (14). Blazor Server Dashboard — 16 REST endpoints, Pico CSS, in-process (Dashboard). Self-awareness feedback loop — outreach pattern detection (12). Memory contradiction flagging — post-save cosine + LLM (15). Feature 22 temporal refinement — time-of-day in coherence gate. Feature 6 name-as-subject — prompt + word-boundary safety net. V5 training data scan — 66 examples mined + generated. 209 tests. |
