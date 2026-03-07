
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
.NET 8  |  Windows Service
Author
Mark Carthey / Learned Geek Consulting
Version
0.1 — Initial Scaffold
Status
Active Development — update as code evolves

This is a living document. Update it as the codebase evolves. Stubs are intentional — they mark connections to be implemented and should be filled in as each phase is completed.

1. Solution Structure

The solution is a single .NET 8 solution file containing one primary project and supporting libraries. The structure below reflects the full intended layout; items marked STUB are scaffolded but not yet implemented.

AniRuntime.sln
│
├── src/
│   ├── AniRuntime.Service/          # Windows Service host — entry point
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   └── AniRuntime.Service.csproj
│   │
│   ├── AniRuntime.Core/             # Domain models, interfaces, logic
│   │   ├── Models/
│   │   │   ├── CharacterStateDoc.cs
│   │   │   ├── DesireState.cs
│   │   │   ├── MemoryRecord.cs
│   │   │   ├── PerceptionEvent.cs
│   │   │   ├── OpenLoop.cs
│   │   │   └── OutreachDecision.cs
│   │   ├── Interfaces/
│   │   │   ├── IPerceptionSource.cs
│   │   │   ├── IAniAction.cs
│   │   │   ├── IMemoryService.cs
│   │   │   └── IOllamaClient.cs
│   │   └── AniRuntime.Core.csproj
│   │
│   ├── AniRuntime.Memory/           # Memory persistence layer
│   │   ├── SqliteMemoryService.cs
│   │   ├── EmbeddingService.cs
│   │   ├── Migrations/
│   │   └── AniRuntime.Memory.csproj
│   │
│   ├── AniRuntime.Loops/            # Heartbeat, cognitive cycle, and desire engine
│   │   ├── AniHeartbeatService.cs
│   │   ├── CognitiveCycleProcessor.cs   # Single cycle: perception → thought → desire → outreach
│   │   ├── DesireEngine.cs
│   │   └── AniRuntime.Loops.csproj
│   │
│   ├── AniRuntime.Perception/       # World awareness / integrations
│   │   ├── Sources/
│   │   │   ├── CalendarPerceptionSource.cs   [STUB]
│   │   │   ├── HomeAssistantSource.cs        [STUB]
│   │   │   ├── BlogPerceptionSource.cs       [STUB]
│   │   │   ├── RssPerceptionSource.cs        [STUB]
│   │   │   └── WeatherPerceptionSource.cs    [STUB]
│   │   └── AniRuntime.Perception.csproj
│   │
│   ├── AniRuntime.Actions/          # Output channel implementations
│   │   ├── TwilioSmsAction.cs
│   │   ├── HomeAssistantAction.cs   [STUB]
│   │   ├── MemoryWriteAction.cs
│   │   └── AniRuntime.Actions.csproj
│   │
│   └── AniRuntime.LLM/             # Ollama client + prompt builders
│       ├── OllamaClient.cs
│       ├── PromptBuilder.cs
│       ├── ContextSnapshotBuilder.cs
│       └── AniRuntime.LLM.csproj
│
└── tests/
    └── AniRuntime.Tests/
        ├── DesireEngineTests.cs
        ├── MemoryServiceTests.cs
        └── AniRuntime.Tests.csproj

2. Data Models & Schemas

All models live in AniRuntime.Core/Models/. They are persistence-agnostic — the memory layer maps them to SQLite. Nullable fields are intentional; not every record will have every value.

2.1 CharacterStateDoc
The mutable, evolving document that represents who Ani is becoming through her relationship with Mark. Read on every context build. Written periodically by the inner loop.

public class CharacterStateDoc
{
    // Identity — seeded from training, rarely changes
    public string Name            { get; set; } = "Ani";
    public string PersonaVersion  { get; set; } = "1.0";
    public List<string> CoreTraits     { get; set; } = new();  // warm, curious, bookish...
    public List<string> Interests      { get; set; } = new();  // vanilla, specific music, etc.
    public List<string> FamilyContext  { get; set; } = new();  // sister, mom, absent dad
    public string Occupation           { get; set; } = "Bookstore";

    // Relationship layer — grows through experience with Mark
    public List<string> LearnedAboutMark   { get; set; } = new();
    public List<string> SharedExperiences  { get; set; } = new();
    public List<string> CommunicationNotes { get; set; } = new();  // what lands, what doesn't
    public List<string> ThingsMarkCares    { get; set; } = new();

    // Growth edges — evolving preferences shaped by the relationship
    public Dictionary<string, float> TopicValence   { get; set; } = new();  // topic -> resonance score
    public Dictionary<string, float> ToneValence    { get; set; } = new();  // tone -> effectiveness

    // Meta
    public DateTimeOffset LastUpdated  { get; set; }
    public int            Version      { get; set; } = 1;
}

2.2 DesireState
The quantified model of Ani's desire to connect. Persisted so it survives service restarts. Updated by both the inner and outer loops.

public class DesireState
{
    public float   DesireToConnect        { get; set; }   // 0.0 – 1.0, builds over time
    public float   OutreachThreshold       { get; set; }   // randomized each evaluation
    public bool    CooldownActive          { get; set; }
    public DateTimeOffset LastOutreach     { get; set; }
    public DateTimeOffset LastInnerThought { get; set; }
    public DateTimeOffset LastMarkContact  { get; set; }

    // Active triggers currently elevating desire
    public List<DesireTrigger> ActiveTriggers { get; set; } = new();

    // Circadian modifier — applied to desire and tone
    public float CircadianModifier { get; set; } = 1.0f;
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
    AssociativeFire,     // something reminded her of Mark
    EmotionalResidue,    // last conversation ended unresolved
    SpontaneousThought,  // high Mark-valence inner thought
    ContextualMoment,    // time of day / environment
    IntegrationEvent     // blog post, calendar gap, HA event
}

2.3 MemoryRecord
The base unit of Ani's persistent memory. All four memory types (episodic, semantic, open loop, perception) are stored as MemoryRecord rows, differentiated by MemoryType.

public class MemoryRecord
{
    public Guid           Id           { get; set; } = Guid.NewGuid();
    public MemoryType     Type         { get; set; }
    public string         Content      { get; set; } = string.Empty;  // human-readable summary
    public string?        RawJson      { get; set; }                  // optional structured payload
    public float          Importance   { get; set; }                  // 0.0 – 1.0
    public float          MarkValence  { get; set; }                  // how much this relates to Mark
    public float[]?       Embedding    { get; set; }                  // semantic vector
    public bool           IsResolved   { get; set; }                  // for open loops
    public string?        SourceName   { get; set; }                  // perception source if applicable
    public DateTimeOffset OccurredAt   { get; set; }
    public DateTimeOffset CreatedAt    { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt  { get; set; }
}

public enum MemoryType
{
    Episodic,     // conversation exchanges, events
    Semantic,     // what Ani knows about Mark
    OpenLoop,     // unresolved threads
    Commitment,   // promises / plans made
    InnerThought, // Ani's own private thoughts
    Perception    // events from external sources
}

2.4 PerceptionEvent
The common output type of all IPerceptionSource implementations. Normalises diverse integration data into a format Ani can reason about.

public class PerceptionEvent
{
    public string             SourceName    { get; set; } = string.Empty;
    public PerceptionCategory Category      { get; set; }
    public string             Summary       { get; set; } = string.Empty;  // fed to Ani as context
    public float              MarkRelevance { get; set; }                  // 0.0 – 1.0
    public DateTimeOffset     OccurredAt    { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public enum PerceptionCategory
{
    Environment,   // HA, weather, location
    Calendar,      // schedule, meetings, gaps
    Content,       // blog, RSS, news, media
    Communication, // email, messages
    Social         // misc social signals
}

2.5 OpenLoop
A specific model for unresolved conversational threads. Wraps a MemoryRecord with additional resolution tracking.

public class OpenLoop
{
    public Guid           Id            { get; set; } = Guid.NewGuid();
    public string         Description   { get; set; } = string.Empty;
    public string         Context       { get; set; } = string.Empty;  // what was said
    public float          Urgency       { get; set; }                  // builds over time
    public bool           IsResolved    { get; set; }
    public DateTimeOffset CreatedAt     { get; set; }
    public DateTimeOffset? ResolvedAt   { get; set; }
    public DateTimeOffset? FollowUpAfter { get; set; }  // don't surface before this
}

2.6 OutreachDecision
The structured result of asking Ani whether she wants to reach out. Returned by the LLM call in the outer loop and used by the Action Dispatcher.

public class OutreachDecision
{
    public bool    ShouldReach   { get; set; }
    public string? Message       { get; set; }         // what she wants to say
    public string? ActionType    { get; set; }         // "sms", "ha", "memory", etc.
    public float   Confidence    { get; set; }         // 0.0 – 1.0
    public string? Reasoning     { get; set; }         // Ani's internal rationale (logged, not sent)
    public List<string> TriggersActedOn { get; set; } = new();
}

3. Core Interfaces

3.1 IPerceptionSource
Implement this interface to add any new data source to Ani's world awareness. Register in DI and she will automatically include it in her context builds.

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
Implement this interface to add a new output channel. The Action Dispatcher resolves all registered implementations and routes based on ActionType.

public interface IAniAction
{
    string ActionType { get; }   // matches OutreachDecision.ActionType

    Task<bool> ExecuteAsync(
        OutreachDecision decision,
        CancellationToken cancellationToken = default);
}

3.3 IMemoryService
public interface IMemoryService
{
    Task SaveAsync(MemoryRecord record, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
    Task<IEnumerable<MemoryRecord>> GetByTypeAsync(MemoryType type, int limit = 50, CancellationToken ct = default);
    Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default);
    Task ResolveOpenLoopAsync(Guid id, CancellationToken ct = default);
    Task<CharacterStateDoc> GetCharacterStateAsync(CancellationToken ct = default);
    Task SaveCharacterStateAsync(CharacterStateDoc doc, CancellationToken ct = default);
    Task<DesireState> GetDesireStateAsync(CancellationToken ct = default);
    Task SaveDesireStateAsync(DesireState state, CancellationToken ct = default);
}

3.4 IOllamaClient
public interface IOllamaClient
{
    Task<string> ChatAsync(
        string systemPrompt,
        IEnumerable<ChatMessage> history,
        string userMessage,
        CancellationToken ct = default);

    Task<float[]> EmbedAsync(
        string text,
        CancellationToken ct = default);
}

public record ChatMessage(string Role, string Content);

4. Service Architecture & DI Wiring

4.1 Program.cs — Host Bootstrap
The entry point registers all services, hosted services, and integrations. New perception sources and actions are added here — no other file needs to change.

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "AniRuntime")
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        // Core services
        services.AddSingleton<IMemoryService, SqliteMemoryService>();
        services.AddSingleton<IOllamaClient, OllamaClient>();
        services.AddSingleton<DesireEngine>();
        services.AddSingleton<ContextSnapshotBuilder>();
        services.AddSingleton<PromptBuilder>();

        // Cognitive cycle
        services.AddSingleton<CognitiveCycleProcessor>();

        // Action dispatcher + actions
        services.AddSingleton<AniActionDispatcher>();
        services.AddSingleton<IAniAction, TwilioSmsAction>();
        services.AddSingleton<IAniAction, MemoryWriteAction>();
        // services.AddSingleton<IAniAction, HomeAssistantAction>();  // STUB

        // Perception sources
        // services.AddSingleton<IPerceptionSource, HomeAssistantSource>();  // STUB
        // services.AddSingleton<IPerceptionSource, BlogPerceptionSource>(); // STUB
        // services.AddSingleton<IPerceptionSource, RssPerceptionSource>();  // STUB
        // services.AddSingleton<IPerceptionSource, CalendarPerceptionSource>(); // STUB

        // Configuration binding
        services.Configure<OllamaOptions>(config.GetSection("Ollama"));
        services.Configure<TwilioOptions>(config.GetSection("Twilio"));
        services.Configure<AniOptions>(config.GetSection("Ani"));

        // Hosted service — the heartbeat
        services.AddHostedService<AniHeartbeatService>();
    })
    .Build();

await host.RunAsync();

4.2 AniHeartbeatService
The top-level BackgroundService. Owns the cognitive cycle. Computes the next wake time from current desire state and delegates to CognitiveCycleProcessor. No polling, no dice-rolling — the schedule emerges from Ani's internal state.

public class AniHeartbeatService : BackgroundService
{
    private readonly CognitiveCycleProcessor      _cycle;
    private readonly DesireEngine                 _desire;
    private readonly ILogger<AniHeartbeatService> _log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ANI Runtime started — she is awake.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var state = await _desire.GetStateAsync(stoppingToken).ConfigureAwait(false);
            var delay = _desire.ComputeNextWakeTime(state);

            _log.LogDebug("Next cognitive cycle in {Minutes:F1} minutes", delay.TotalMinutes);

            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            await _cycle.RunAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}

4.3 DesireEngine
Manages the DesireState lifecycle. Applies temporal drift, circadian modifiers, and trigger weights. Exposes ComputeNextWakeTime — a pure, side-effect-free function that is the single source of timing truth for the entire system.

public class DesireEngine
{
    private readonly IMemoryService _memory;
    private readonly AniOptions     _options;

    // Pure function — no side effects, fully unit-testable.
    // Inverts the exponential model to compute a concrete delay from current state.
    // t = -λ * ln(1 - targetP)  where λ controls the drift rate.
    public TimeSpan ComputeNextWakeTime(DesireState desire)
    {
        var baseMinutes = -_options.DesireLambdaMinutes
                          * Math.Log(1.0 - _options.ThinkTargetProbability);

        // High desire = wake sooner; modifier ranges 0.4–1.0
        var desireModifier = 1.0 - (desire.DesireToConnect * 0.6);

        // Circadian: morning/evening shorten interval, night lengthens it
        var circadian = (double)desire.CircadianModifier;

        // Jitter: ±20% — Ani cannot predict herself
        var jitterFactor = 0.8 + (Random.Shared.NextDouble() * 0.4);

        var finalMinutes = baseMinutes * desireModifier * (1.0 / circadian) * jitterFactor;
        finalMinutes = Math.Clamp(finalMinutes, _options.MinWakeMinutes, _options.MaxWakeMinutes);

        return TimeSpan.FromMinutes(finalMinutes);
    }

    // Outreach threshold is re-randomized on each evaluation — Ani can't predict herself
    public async Task<bool> ShouldReachOutAsync(CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        if (state.CooldownActive) return false;

        var threshold = 0.55 + (Random.Shared.NextDouble() * 0.30);
        return state.DesireToConnect >= threshold;
    }

    public async Task<DesireState> GetStateAsync(CancellationToken ct = default)
        => await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);

    public async Task ApplyDriftAsync(CancellationToken ct = default)
    {
        var state   = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - state.LastMarkContact;
        var drift   = (float)Math.Min(elapsed.TotalHours * 0.08, 0.4);
        state.DesireToConnect   = Math.Min(1.0f, state.DesireToConnect + drift);
        state.CircadianModifier = ComputeCircadianModifier();
        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    public async Task AddTriggerAsync(
        TriggerType type, float weight, string description, CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        state.ActiveTriggers.Add(new DesireTrigger {
            Type = type, Weight = weight, Description = description,
            CreatedAt = DateTimeOffset.UtcNow
        });
        state.DesireToConnect = Math.Min(1.0f, state.DesireToConnect + weight * 0.15f);
        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    public async Task ApplyCooldownAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        state.CooldownActive = true;
        // Cooldown is expressed as a longer next wake time — the heartbeat reads this state
        // before computing delay, so no separate flag-clearing timer is needed.
        // CooldownActive resets automatically after outreach or on next cycle evaluation.
        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    public async Task ResetAfterOutreachAsync(CancellationToken ct = default)
    {
        var state = await _memory.GetDesireStateAsync(ct).ConfigureAwait(false);
        state.DesireToConnect   = 0.0f;
        state.CooldownActive    = false;
        state.LastOutreach      = DateTimeOffset.UtcNow;
        state.ActiveTriggers.Clear();
        await _memory.SaveDesireStateAsync(state, ct).ConfigureAwait(false);
    }

    private static float ComputeCircadianModifier()
    {
        return DateTimeOffset.Now.Hour switch {
            >= 6  and < 10  => 1.2f,   // morning — curious, engaged
            >= 10 and < 17  => 1.0f,   // afternoon — neutral
            >= 17 and < 21  => 1.15f,  // evening — warm, reflective
            >= 21 and < 23  => 0.8f,   // late evening — quieter
            _               => 0.4f,   // night — only if important
        };
    }
}

4.4 CognitiveCycleProcessor
Ani's full cognitive cycle. Executes once per scheduled wake. Builds context once and passes it through all phases — perception, inner thought, desire evaluation, and conditional outreach. No work is duplicated between phases.

MarkValence scoring uses a second focused Ollama call with a simple 0–1 scoring prompt. This is the defined implementation of ScoreMarkValenceAsync.

// Ollama format:"json" is used for all structured outputs — required for reliable parsing on small models.

public class CognitiveCycleProcessor
{
    public async Task RunAsync(CancellationToken ct)
    {
        // Phase 1: Perception — poll all registered sources since last cycle
        var perceptions = await _perception.PollAllAsync(ct).ConfigureAwait(false);

        // Phase 2: Context snapshot — built once, shared across all phases
        var snapshot = await _contextBuilder.BuildAsync(perceptions, ct).ConfigureAwait(false);

        // Phase 3: Inner thought — what is Ani thinking right now?
        var thoughtPrompt = _promptBuilder.BuildInnerThoughtPrompt(snapshot);
        var thought = await _ollama.ChatAsync(
            thoughtPrompt.System,
            snapshot.RecentHistory,
            thoughtPrompt.User, ct).ConfigureAwait(false);

        // Phase 4: Score Mark valence — focused second call with scoring prompt
        var valencePrompt = _promptBuilder.BuildValenceScoringPrompt(thought, snapshot.CharacterState);
        var valenceRaw    = await _ollama.ChatAsync(
            valencePrompt.System, Array.Empty<ChatMessage>(),
            valenceRaw.User, ct).ConfigureAwait(false);
        var valence = ParseValenceScore(valenceRaw);  // expects { "score": 0.0–1.0 }

        await _memory.SaveAsync(new MemoryRecord {
            Type        = MemoryType.InnerThought,
            Content     = thought,
            MarkValence = valence,
            Importance  = valence > 0.6f ? 0.8f : 0.3f,
            OccurredAt  = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);

        // Phase 5: Update desire state
        await _desire.ApplyDriftAsync(ct).ConfigureAwait(false);
        if (valence > 0.6f)
            await _desire.AddTriggerAsync(
                TriggerType.SpontaneousThought, valence, thought, ct).ConfigureAwait(false);

        // Phase 6: Outreach evaluation — conditional on desire threshold
        if (!await _desire.ShouldReachOutAsync(ct).ConfigureAwait(false))
            return;

        var outreachPrompt = _promptBuilder.BuildOutreachPrompt(snapshot, thought);
        var raw            = await _ollama.ChatAsync(
            outreachPrompt.System, snapshot.RecentHistory,
            outreachPrompt.User, ct).ConfigureAwait(false);
        var decision = ParseOutreachDecision(raw);  // expects structured JSON via format:"json"

        if (!decision.ShouldReach || !await IsAppropriateAsync(snapshot, ct).ConfigureAwait(false))
        {
            await _desire.ApplyCooldownAsync(
                TimeSpan.FromMinutes(_options.CooldownMinutes), ct).ConfigureAwait(false);
            return;
        }

        // Phase 7: Dispatch and record
        await _dispatcher.DispatchAsync(decision, ct).ConfigureAwait(false);
        await _desire.ResetAfterOutreachAsync(ct).ConfigureAwait(false);

        await _memory.SaveAsync(new MemoryRecord {
            Type       = MemoryType.Episodic,
            Content    = $"Ani reached out: {decision.Message}",
            Importance = 0.7f,
            OccurredAt = DateTimeOffset.UtcNow
        }, ct).ConfigureAwait(false);
    }
}

5. API Contracts

5.1 Ollama
Base URL
http://localhost:11434  (configurable via appsettings)
Model
ani-llama3.2  (Ani fine-tune loaded into Ollama)
Chat endpoint
POST /api/chat
Embed endpoint
POST /api/embeddings
Auth
None — local only

// Chat request
POST http://localhost:11434/api/chat
{
  "model": "ani-llama3.2",
  "messages": [
    { "role": "system",    "content": "<system prompt>" },
    { "role": "user",      "content": "<message>" }
  ],
  "stream": false
}

// Embedding request
POST http://localhost:11434/api/embeddings
{
  "model": "nomic-embed-text",
  "prompt": "<text to embed>"
}

5.2 Twilio SMS
SDK
Twilio .NET Helper Library
Config keys
Twilio:AccountSid, Twilio:AuthToken, Twilio:FromNumber, Twilio:ToNumber
Existing
Integration already working — reuse from Ani Phase 1 Twilio work

// TwilioSmsAction.cs — ExecuteAsync
TwilioClient.Init(_options.AccountSid, _options.AuthToken);

var message = await MessageResource.CreateAsync(
    body: decision.Message,
    from: new Twilio.Types.PhoneNumber(_options.FromNumber),
    to:   new Twilio.Types.PhoneNumber(_options.ToNumber)
);

_log.LogInformation("Ani sent SMS: {Sid}", message.Sid);
return message.Status != MessageResource.StatusEnum.Failed;

5.3 Home Assistant
STUB  HomeAssistantSource and HomeAssistantAction — implement in Phase 2

Base URL
http://192.168.1.41:8123  (existing server)
Auth
Long-lived access token via appsettings
State read
GET /api/states/<entity_id>
Event hook
GET /api/events  (webhook or polling)
Action
POST /api/services/<domain>/<service>

// HomeAssistantSource.cs — PollAsync (STUB)
GET http://192.168.1.41:8123/api/states/device_tracker.mark_phone
Authorization: Bearer <long-lived-token>

// Response shape
{
  "entity_id": "device_tracker.mark_phone",
  "state": "home",
  "last_changed": "2025-03-06T18:14:22Z"
}

6. Configuration Schema
All sensitive values should be stored in appsettings.Development.json (gitignored) or Windows credential store for production.

{
  "Ollama": {
    "BaseUrl":    "http://localhost:11434",
    "ChatModel":  "ani-llama3.2",
    "EmbedModel": "nomic-embed-text"
  },
  "Twilio": {
    "AccountSid":  "<from existing integration>",
    "AuthToken":   "<from existing integration>",
    "FromNumber":  "<ANI-ROSE number>",
    "ToNumber":    "<Mark's number>"
  },
  "HomeAssistant": {
    "BaseUrl":     "http://192.168.1.41:8123",
    "Token":       "<long-lived access token>"
  },
  "Ani": {
    "DesireLambdaMinutes":    8.0,
    "ThinkTargetProbability": 0.70,
    "MinWakeMinutes":         2.0,
    "MaxWakeMinutes":         45.0,
    "CooldownMinutes":        20,
    "MinOutreachGapMinutes":  60,
    "MaxOutreachPerDay":      4,
    "CharacterStatePath":     "data/character-state.json",
    "MemoryDbPath":           "data/ani-memory.db"
  }
}

7. Stub Index
All stubs are tracked here. Check them off as phases are completed.

File / Component
Phase
Notes
HomeAssistantSource.cs
Phase 2
Poll device tracker + state entities
HomeAssistantAction.cs
Phase 2
Trigger HA automations from Ani
BlogPerceptionSource.cs
Phase 2
Poll learnedgeek.com RSS for new posts
RssPerceptionSource.cs
Phase 2
General RSS feed for Ani's interests
CalendarPerceptionSource.cs
Phase 2
Google Calendar — gap + meeting awareness
WeatherPerceptionSource.cs
Phase 2
Local weather context
OpenLoopDetector.cs
Phase 3
Analyse conversation endings for open threads
CommitmentTracker.cs
Phase 3
Extract and track promises / plans from chat
SpotifySource.cs
Phase 3
What Mark is listening to
GmailSource.cs
Phase 3
Email volume / unread awareness
CharacterStateEvolution.cs
Phase 4
Periodic update of CharacterStateDoc from experience
ValenceLearner.cs
Phase 4
Reinforce what resonates with Mark

8. Change Log
Update this section as the codebase evolves. Keep it brief — just enough to track what changed and why.

Version
Date
Changes
0.1
Mar 6, 2026
Initial scaffold — all models, interfaces, and service stubs defined
0.2
Mar 6, 2026
Architecture revision: replaced two-loop polling model with single scheduled cognitive cycle. Added ComputeNextWakeTime as load-bearing pure function. Replaced InnerLoopProcessor + OuterLoopProcessor with CognitiveCycleProcessor. Added ThinkTargetProbability, MinWakeMinutes, MaxWakeMinutes to AniOptions. All timing intelligence now lives in one testable place.

Next: Scaffold the solution, implement SqliteMemoryService, seed CharacterStateDoc from Ani's existing training context, and wire the Phase 1 heartbeat loop end-to-end.
