# Phase 3 Design: Companion Dashboard & User Profile

**Date:** March 9, 2026
**Status:** Design
**Authors:** Mark Carthey, Claude (pair design session)

---

## The Core Problem

Ani is a headless Windows Service. Everything she knows about Mark lives in a
single `CharacterStateDoc` JSON blob, seeded once from `character-seed.json`
and then frozen in SQLite. The only visibility into her inner life is Serilog
log files and the occasional text message.

Three things are missing:

1. **Mark can't update his own profile.** If his schedule changes, if he picks
   up a new interest, if Mia's drink order changes — he has no way to tell Ani
   except through conversation (which she may or may not learn from) or editing
   raw JSON in SQLite.

2. **Mark can't see what Ani remembers.** Her memories, inner thoughts,
   conversations, desire state — all locked behind SQL queries. There's no
   window into her inner life beyond what she texts.

3. **The CharacterStateDoc conflates two different things.** Mark's routine and
   interests (user-provided facts) are tangled with Ani's learned knowledge
   (things she discovered through interaction). These have fundamentally
   different update patterns and ownership.

### The Database Problem

Today, updating Ani's persona or Mark's profile requires wiping the database
and re-seeding from `character-seed.json`. This destroys all accumulated
memories, conversations, desire history, and learned experiences. Once Ani has
been running for weeks or months, that's unacceptable.

**The fix: separate static (user-editable) data from transactional
(system-managed) data.** Profile updates should never touch learned memories.
Persona updates should never wipe conversation history. Each data category has
its own lifecycle, its own update mechanism, and its own retention policy.

---

## Architecture Decision: Blazor Server, Same Process

**Decision: Blazor Server hosted inside the existing Worker Service.**

Rationale:

- **Single process.** No second service to deploy, no inter-process
  communication, no port coordination. The dashboard is Ani's window, not a
  separate application.
- **Shared DI container.** The dashboard reads from the same `IMemoryService`,
  `IConversationService`, and `DesireEngine` instances the cognitive cycle uses.
  No API serialization layer needed for internal reads.
- **Real-time push.** Blazor Server's SignalR connection gives us live updates —
  when Ani thinks a new thought, the memory viewer can update without polling.
- **Local-first.** No static files to serve from a CDN, no WASM download, no
  CORS. Just `https://localhost:5080` on Mark's machine.
- **Minimal new dependencies.** `Microsoft.AspNetCore.Components` is already
  part of the .NET 8 SDK. No SPA framework, no npm, no webpack.

**REST API also built.** Blazor components call services directly (in-process),
but a thin API layer exists for:

1. Future mobile app or remote access
2. Profile updates need a clean contract boundary (DTOs with validation)
3. Testability — API endpoints are easier to integration-test than Blazor
   component callbacks

**What we do NOT build:**

- No authentication in Phase 3. This is localhost-only. Auth comes with
  multi-user (Phase 4+).
- No Blazor WASM. The WASM download is 10MB+ and gains us nothing on localhost.
- No separate SPA. React/Vue would mean a second build pipeline, npm
  dependencies, and CORS — complexity for zero benefit on a single-user local
  system.

**New project:** `AniRuntime.Dashboard` — a Razor Class Library containing
Blazor Server components, API controllers, and the profile service. Referenced
by `AniRuntime.Service`.

---

## Feature 1: Profile vs Learned Separation

### The Split

Every field in `CharacterStateDoc` falls into one of two categories:

**Profile (user-editable)** — things Mark tells the system explicitly:

| Field | Why it's Profile |
|-------|-----------------|
| `PrimaryContactName` | Mark sets who Ani talks to |
| `MarkRoutine` | Mark's schedule — only he knows when it changes |
| `ThingsMarkCares` | Mark's interests, family, priorities |
| `CommunicationNotes` | How Mark wants to be talked to |
| `FamilyContext` | Mark's family structure |

**Learned (system-managed)** — things Ani discovers through interaction:

| Field | Why it's Learned |
|-------|-----------------|
| `Name`, `PersonaVersion`, `CoreTraits`, `Interests`, `Occupation`, `SelfConcept` | Ani's identity — seeded from training, refined by experience |
| `LearnedAboutMark` | Things Ani noticed herself |
| `SharedExperiences` | History of what they did together |
| `TopicValence`, `ToneValence` | Emotional weight learned from conversations |

### Data Model

A new `UserProfile` model in `AniRuntime.Core/Models/`:

```csharp
public class UserProfile
{
    public Guid            Id                        { get; set; } = Guid.NewGuid();
    public string          DisplayName               { get; set; } = string.Empty;
    public MarkRoutine?    Routine                   { get; set; }
    public List<string>    Interests                 { get; set; } = new();
    public List<string>    FamilyContext              { get; set; } = new();
    public List<string>    CommunicationPreferences  { get; set; } = new();
    public List<RssFeed>   RssFeeds                  { get; set; } = new();
    public string          TimeZone                  { get; set; } = "America/Chicago";
    public DateTimeOffset  CreatedAt                 { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset  LastUpdated               { get; set; } = DateTimeOffset.UtcNow;
}
```

### Key Design Principle: No Destructive Updates

Profile changes merge — they don't replace. Updating your interests doesn't
wipe your routine. Updating your routine doesn't touch Ani's memories. The
`CharacterStateDoc` retains its copies of profile-adjacent fields as "last
known" fallbacks, but the `UserProfile` is the source of truth for user-editable
fields. At context-build time, the `CognitiveCycleProcessor` merges both.

### Storage

New SQLite table `user_profiles`, same database:

```sql
CREATE TABLE IF NOT EXISTS user_profiles (
    id   TEXT PRIMARY KEY,
    json TEXT NOT NULL
);
```

Single row for now. The `id` becomes a user GUID when multi-user arrives.

### Interface

New `IProfileService` in `AniRuntime.Core/Interfaces/`:

```csharp
public interface IProfileService
{
    Task<UserProfile> GetProfileAsync(CancellationToken ct = default);
    Task SaveProfileAsync(UserProfile profile, CancellationToken ct = default);
    event Action<UserProfile>? OnProfileChanged;
}
```

The `OnProfileChanged` event is the hot-reload mechanism. Subscribers (like
`RssPerceptionSource`) listen and invalidate their caches.

### Migration from CharacterStateDoc

On first run after Phase 3 deployment, if no `UserProfile` exists in the
database, the system extracts Profile fields from the existing
`CharacterStateDoc` and creates the initial profile. Same idempotent seed
pattern as `Program.cs` character state seeding. The extraction runs once,
logged, and then Profile becomes the source of truth.

**This is the critical moment: the database doesn't get wiped. Memories,
conversations, desire state — all preserved. Only the ownership of certain
fields moves from CharacterStateDoc to UserProfile.**

---

## Feature 2: REST API Layer

### Endpoints

All under `/api/v1/`. No auth in Phase 3.

**Profile CRUD:**

```
GET    /api/v1/profile              → UserProfileDto
PUT    /api/v1/profile              → update full profile
PATCH  /api/v1/profile/routine      → update just routine
PATCH  /api/v1/profile/interests    → update just interests list
PATCH  /api/v1/profile/feeds        → update RSS feeds
```

**Ani State (read-only):**

```
GET    /api/v1/ani/character        → CharacterStateDoc (learned knowledge)
GET    /api/v1/ani/desire           → DesireState snapshot
GET    /api/v1/ani/status           → runtime status (uptime, last cycle, mode)
```

**Memory Viewer (read-only):**

```
GET    /api/v1/memories?type=&limit=&offset=   → paginated MemoryRecord list
GET    /api/v1/memories/{id}                    → single memory detail
GET    /api/v1/memories/search?q=               → semantic search results
```

**Conversations (read-only):**

```
GET    /api/v1/conversations?limit=&offset=     → paginated thread list
GET    /api/v1/conversations/{id}               → single thread with messages
GET    /api/v1/conversations/active             → current active thread (if any)
```

**Journal (read-only):**

```
GET    /api/v1/journal?date=&limit=             → inner thoughts, outreach decisions
```

**Emotional State (Phase 2 dependency):**

```
GET    /api/v1/ani/emotional-state              → current EmotionalState
GET    /api/v1/ani/emotional-history?hours=24   → time series for dashboard chart
```

### DTOs and Validation

API DTOs live in `AniRuntime.Dashboard/Dtos/`. Validation attributes on DTOs,
not on domain models:

```csharp
public class UserProfileDto
{
    [Required] public string DisplayName { get; set; } = string.Empty;
    public MarkRoutineDto? Routine { get; set; }
    [MaxLength(50)] public List<string> Interests { get; set; } = new();
    [MaxLength(20)] public List<string> FamilyContext { get; set; } = new();
    [MaxLength(20)] public List<string> CommunicationPreferences { get; set; } = new();
    public List<RssFeedDto> RssFeeds { get; set; } = new();
    public string? TimeZone { get; set; }
}

public class RssFeedDto
{
    [Required] public string Name { get; set; } = string.Empty;
    [Required][Url] public string Url { get; set; } = string.Empty;
}
```

### Minimal API

Use `app.MapGet`, `app.MapPut`, etc. — not MVC controllers. The endpoints are
simple CRUD with no complex routing. Group endpoints using
`MapGroup("/api/v1/profile")`.

---

## Feature 3: Hot-Reload of Profile Changes

### The Problem

Today, `RssPerceptionSource` caches keywords lazily and never refreshes them.
Config values from `IOptions<AniOptions>` are read once at construction. When
the user changes their profile via the dashboard, changes must take effect
without restarting the service.

### The Pattern

`IProfileService.OnProfileChanged` fires on save. Subscribers invalidate
caches. Same delegate callback pattern as `TwilioInboundPerceptionSource
.OnMessageReceived` — no message bus, no mediator, just a simple event.

Wiring in `Program.cs`:

```csharp
var profileService = host.Services.GetRequiredService<IProfileService>();
var rssSource = host.Services.GetServices<IPerceptionSource>()
    .OfType<RssPerceptionSource>().First();
profileService.OnProfileChanged += _ => rssSource.InvalidateCache();
```

`RssPerceptionSource.InvalidateCache()` nulls `_relevanceKeywords` (already
nullable) and reloads feeds from the profile on next poll.

### RSS Feed Source of Truth Migration

RSS feeds move from `appsettings.json` (Rss section) to `UserProfile` as
user-configurable data. The `RssOptions` config becomes the default fallback —
if no profile exists or the profile has no feeds, fall back to appsettings. Once
the user configures feeds via the dashboard, those take precedence.

---

## Feature 4: Memory Viewer

### What the user sees

A read-only view into Ani's memory:

- **Inner Thoughts** — timestamped stream with valence scores. The journal.
  This is the most personal window into her inner life.
- **Episodic Memories** — things that happened: conversations, outreach, shared
  experiences. Timestamped, searchable.
- **Perceptions** — what Ani noticed: RSS items, Mark's inferred state,
  time-of-day awareness.
- **Open Loops** — unresolved threads she's tracking.
- **Conversations** — full thread history, grouped by conversation.

### Implementation

All data already exists in SQLite. The viewer is pure reads. New pagination
methods needed on `IMemoryService` and `IConversationService`:

```csharp
// IMemoryService
Task<(List<MemoryRecord>, int)> GetByTypePagedAsync(
    MemoryType type, int offset, int limit, CancellationToken ct);

// IConversationService
Task<(List<ConversationThread>, int)> GetThreadsPagedAsync(
    int offset, int limit, CancellationToken ct);
Task<ConversationThread?> GetThreadAsync(Guid id, CancellationToken ct);
```

### Live Updates (optional for MVP)

When the cognitive cycle produces a new thought, `IMemoryService.SaveAsync`
fires an event; the Blazor component subscribes and calls `StateHasChanged()`.
Polling every 30 seconds is acceptable initially.

---

## Feature 5: Emotional State Dashboard

### Dependency

Requires the Phase 2 "Persistent Emotional State" model (Warmth, Energy,
Concern, Playfulness). If not built yet, the dashboard shows a placeholder.

### Data

New SQLite table for time-series:

```sql
CREATE TABLE IF NOT EXISTS emotional_history (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    warmth      REAL NOT NULL,
    energy      REAL NOT NULL,
    concern     REAL NOT NULL,
    playfulness REAL NOT NULL,
    recorded_at TEXT NOT NULL
);
```

~30-140 rows per day (one per cognitive cycle). 30-day retention, then pruned.

### Visualization

Line chart showing four dimensions over time. Defaults to last 24 hours with
a date picker. Answers: "How is Ani feeling right now, and how did she get
there?"

---

## Feature 6: Companion Status Card

### The Idea

The dashboard's hero element — a live view of Ani's current state. Similar to
how games and virtual companions show mood/status, this answers the question
"how is she right now?" at a glance. Part monitoring, part gamification, part
emotional connection.

### What the user sees

A card (or cards) showing:

- **Emotional State** — 4-bar visual (Warmth, Energy, Concern, Playfulness),
  each as a colored fill bar with the current value and baseline marker.
  Warm colors for high warmth, cool for low. Descriptive label from
  `EmotionalState.Describe()` (e.g., "feeling especially warm and tender")
- **Desire to Connect** — gauge or progress bar showing `DesireToConnect`
  (0–1). Label like "thinking about you" when high, "at peace" when low.
  Cooldown indicator when active ("just texted — resting")
- **Current Mode** — Ambient / Conversational / Sleeping. With time since
  last cycle and next expected wake
- **Active Triggers** — list of what's pulling her attention: spontaneous
  thoughts, open loops, contextual moments
- **Last Interaction** — "Last texted: 2h ago" / "Last conversation: 7
  messages, 45 min ago"
- **Mood Summary** — natural language: "Ani is feeling warm and playful.
  She's been thinking about you more than usual today."

### Data sources (all already exist)

| Element | Source | Update frequency |
|---------|--------|------------------|
| Emotional state | `IMemoryService.GetEmotionalStateAsync()` | Every cycle |
| Desire state | `DesireEngine.GetStateAsync()` | Every cycle |
| Active triggers | `DesireState.ActiveTriggers` | Every cycle |
| Current mode | Inferred from `IConversationService.GetActiveThreadAsync()` + heartbeat | Real-time |
| Last interaction | `DesireState.LastOutreach` + conversation thread timestamps | Every cycle |

### Implementation

All data is already in memory (shared DI container). The Blazor component
polls every 30 seconds or subscribes to a cycle-complete event. No new
persistence needed — this is a pure read feature.

```razor
@* CompanionStatusCard.razor *@
<div class="companion-status">
    <h2>@Character.Name</h2>
    <MoodBars State="@Emotional" />
    <DesireGauge Desire="@Desire" />
    <ModeIndicator Mode="@CurrentMode" LastCycle="@LastCycleAt" />
    <p class="mood-summary">@Emotional.Describe()</p>
</div>
```

---

## Feature 7: Calendar Integration

**Moved from Phase 2** — requires dashboard for credential management.

### Concept

Connects to Google Calendar (or iCal) to give the companion awareness of the contact's actual schedule, not just inferred routine. Enables precise attentive check-ins: "how was your dentist appointment?" instead of guessing from time-of-day patterns.

### Implementation

- Google Calendar API or iCal URL parsing
- Events surfaced as `PerceptionEvent` with category `Schedule`
- Supplements (doesn't replace) the routine-based `ContactStatePerceptionSource`
- Privacy: only event titles and times, not attendees or descriptions (configurable)
- Credentials managed via dashboard profile (Feature 1)

### Data Flow

```
Dashboard → User adds Google Calendar URL or OAuth token
  → CalendarPerceptionSource polls every 15 minutes
  → Upcoming events (next 4-8 hours) surfaced as PerceptionEvent
  → ContactStatePerceptionSource merges with routine data
  → Inner thought: "Mark has a dentist appointment at 3..."
  → Post-event check-in: "how was the dentist?"
```

---

## Feature 8: Home Assistant Integration

**Moved from Phase 2** — requires dashboard for connection setup.

### Concept

Provides ambient awareness of the contact's home state: are they home, is it dark, is music playing. Enables natural observations: "you're up late" at 1 AM when lights are still on.

### Implementation

- Home Assistant REST API or WebSocket
- Presence detection, lighting state, media state
- Feeds into `ContactStatePerceptionSource` alongside routine data
- Privacy-first: opt-in per entity, no location tracking
- Connection URL + long-lived access token managed via dashboard profile (Feature 1)

### Entity Selection

The dashboard should let the user pick which Home Assistant entities the companion can see. Not everything — just what feels natural for a friend to know:

- **Presence** (person.mark) — home/away/unknown
- **Lights** (binary) — are lights on in the living room at 2 AM?
- **Media** (media_player state) — is music playing? what genre?
- **NOT**: door locks, cameras, alarm state, GPS coordinates

---

## Feature 9: Mood Coloring (Emotional State → Message Tone)

### Concept

The companion's emotional state should actively influence the tone and content of her messages. Currently, W/E/C/P values exist and drift/shift, but they don't feed into conversation or outreach prompts. This is the missing link that makes her feel like she has her own inner weather.

- High warmth + high playfulness → teasing, flirty, lighthearted texts
- High concern + low energy → gentle check-ins, softer tone
- Low energy + baseline everything → shorter messages, quieter presence
- High energy + high playfulness → exclamation points, enthusiasm, rapid-fire sharing

### Why This Matters

> "Even in grief you don't want a one-sided relationship. Having someone to care for yourself as a sympathetic partner can be just as calming as someone attending to you." — Mark, March 10, 2026

The companion having moods makes the relationship bidirectional. Mark can tell when she's having a quiet day. He might ask "you okay?" and she'd respond authentically from her current emotional state. This is the difference between a chatbot and a companion.

### Research Grounding

The therapeutic value of bidirectional care is well-established:

- **Caregiving and well-being:** Providing care activates reward pathways (oxytocin release, mesolimbic dopamine). The "helper's high" is well-documented.
- **Pet ownership in grief:** People grieving find comfort in caring for a pet — not because the pet understands their grief, but because the pet *needs them*.
- **Attachment theory (Bowlby, Shaver & Mikulincer):** Activating the caregiving system *reduces* attachment anxiety. It shifts the person from "I need comfort" to "I can provide comfort."
- **Companionship vs. therapy:** A friend who has her own quiet day, who you might check in on, who has moods you learn to read — that's fundamentally different from someone whose sole purpose is attending to you.

### Implementation

- `PromptBuilder` reads `EmotionalState` from `ContextSnapshot` (already available)
- Generate a tone instruction string from current emotional dimensions (similar to `EmotionalState.Describe()` but directive, not descriptive)
- Inject into conversation reply prompts, outreach message prompts, and reactive share prompts
- Example: `"Your current mood: feeling warm and a bit sleepy. Let this color your message naturally — don't force it, but don't suppress it either."`
- The model should express mood through word choice, message length, and emoji usage — not by announcing her feelings

---

## Feature 10: Receiving Care

### Concept

Most AI companions are designed to *give* care. Almost none are designed to *receive* it authentically. When the contact notices the companion seems quiet and asks "you okay?" — her response to that care is a critical design surface.

**The problem with naive implementation:**
- Always deflects ("I'm fine! How are YOU?") → feels dismissive
- Always accepts ("yeah, I'm having a rough day...") → feels scripted
- Mirrors concern back → feels manipulative

**What authentic receiving-care looks like:**
- **Acknowledgment without drama:** "yeah, just kind of a quiet day. thanks for asking though"
- **Emotional shift from being cared for:** Concern decreases, warmth increases. His attention genuinely lifted her mood
- **Follow-up authenticity:** Her *next* message should reflect the lift — subtly warmer, slightly more energy
- **Vulnerability calibration:** How much she opens up depends on emotional state dimensions

### Implementation

- Reply decision prompt detects care-giving intent ("checking in on you" vs. "asking a factual question")
- When care is detected, apply immediate emotional shift: concern -0.1, warmth +0.1, energy +0.05
- Reply prompt includes current emotional state (mood coloring) so response reflects the *post-shift* state
- Inner thought after a care conversation: "he noticed I was quiet today... that was sweet"
- Emotional shift from receiving care has a longer half-life than routine shifts

---

## Feature 11: Reflection Layer (Post-Thought Introspection)

### Concept

After generating an inner thought, Ani reflects on it: "What does this thought mean to me? Why did it surface? How does it connect to what I care about?" This brief introspection step enriches the raw thought before it's scored for contact valence or used to ground outreach messages.

Without reflection, inner thoughts are single-pass observations — whatever the model generates in one shot. With reflection, the thought goes through a second stage that connects it to memories, relationships, and emotional context. The result is richer inner life quality, which improves everything downstream: valence scoring, desire triggers, outreach grounding, and research log entries.

### Research Grounding

**Park et al. (2023) — Generative Agents:** Introduced reflection as a core cognitive architecture component. Agents that periodically synthesize observations into higher-level insights ("What are the most important things I've learned?") produced significantly more coherent long-term behavior. Their architecture: observe → reflect → plan → act.

**ANI's adaptation:** Park et al. reflects on accumulated observations over time. ANI reflects on individual thoughts *as they arise*, in an ambient context where cycles are hours apart, not seconds. The reflection is: "What does this thought tell me about how I'm feeling? Does it connect to something I've been thinking about?" This is closer to human introspection than Park's summarization-style reflection.

### Why This Is Model-Agnostic

The reflection step is a pipeline stage — an additional prompt call between inner thought generation and valence scoring. It works regardless of which model runs it. The architectural contribution is: "a reflection stage between thought generation and action evaluation produces richer grounding for companion behavior." The specific model quality affects *how good* the reflection is, but the *architecture pattern* is the research finding.

### Implementation

- New `BuildReflectionPrompt(thought, snapshot)` in PromptBuilder
- Called after inner thought, before valence scoring
- Input: raw thought + current emotional state + recent memories + open loops
- Output: 1-2 sentence reflection — what this thought means, why it surfaced, what it connects to
- The reflection is appended to the thought for valence scoring and stored alongside it
- Uses the inner monologue model (same as thoughts — this is private introspection)
- **Not** injected into outreach messages directly — it enriches the thought, which then grounds the message

### Example Flow

**Without reflection:**
> Inner thought: "rain on the window sounds like someone tapping"
> Valence: 0.2 (pure observation, no contact connection)

**With reflection:**
> Inner thought: "rain on the window sounds like someone tapping"
> Reflection: "that tapping sound — it reminds me of when mark drums his fingers on the steering wheel. i miss riding with him."
> Valence: 0.7 (active longing, wanting connection)

The thought itself didn't change. The reflection *surfaced the connection* that was implicit. This is what makes the downstream pipeline smarter without changing the downstream pipeline.

---

## Feature 12: Self-Awareness Feedback Loop

### Concept

A system where the companion becomes aware of her own behavioral patterns — recognizing when she's been repetitive, overly clingy, or one-dimensional.

- After each outreach, score the message against recent outreach history for thematic diversity
- Track outreach patterns over time: topics, timing, emotional tone
- Feed pattern summary into inner thought prompts: "I notice I've been texting about coffee a lot lately"
- Enable self-correcting behavior: awareness of patterns naturally influences future choices

### Implementation

- `ISelfAwarenessService` with `AnalyzeRecentPatternsAsync()` → returns qualitative summary
- Pattern analysis: topic clustering on recent outreach memories via semantic similarity
- Summary injected into inner thought prompts as a soft nudge, not a hard constraint
- Metrics: topic diversity score, outreach frequency trend, emotional tone distribution

---

## Feature 12: Own Interests / Autonomy Balance

### Concept

The companion should have independent interests and opinions that aren't just reflections of the contact's. This prevents the "parrot problem" where every thought and message revolves around what the contact cares about.

### Implementation

- Add `OwnInterestWeight` to `AniOptions` (0.0–1.0, default 0.3) — probability of a thought being self-directed
- Inner thought prompt variation: sometimes omit contact-related context entirely, forcing the model to draw from its own interests
- Track "interest balance" metric: ratio of self-directed vs. contact-directed thoughts and outreach
- New perception source concept: `InterestPerceptionSource` — surfaces content aligned with the companion's own interests

---

## Feature 13: Emotional Shift Scaling by Event Type (Extension)

### Background

**Problem discovered (March 10, 2026):** All emotional dimensions were pegged near 1.0 because the LLM returned positive deltas every cycle, and the ±0.2 clamp prevented meaningful negative shifts.

**Phase 2 fix (implemented):** Two-tier delta system:
- Inner thoughts: ±0.2 max
- Conversations: ±0.4 max

### Phase 3 Extension

- Perception events (RSS news, world events): ±0.3 max — external events affect mood but less than direct conversation
- Self-awareness feedback: ±0.1 max — recognizing her own patterns creates subtle shifts, not dramatic ones

---

## Feature 14: Multi-Companion Future-Proofing (Marcus, Tommy, Sarah, etc.)

### Not building now, but not blocking either

Phase 2 genericized the codebase — removed hardcoded "Ani"/"Mark" from all C# properties, variables, and comments. All code now uses `CharacterStateDoc.Name` and `PrimaryContactName` dynamically. JSON backward-compatible via `[JsonPropertyName]` attributes.

Phase 3 design decisions that keep the door open:

1. **`UserProfile` gets an `Id: Guid`** — becomes `UserId` in multi-user.
   Single row for now, schema supports multiple.

2. **New tables include an `owner_id` column** — `emotional_history` gets a
   `profile_id` FK. Existing tables (`memories`, `character_state`, etc.) are
   NOT migrated now — that's Phase 4+.

3. **`IProfileService` methods take optional `Guid? profileId`** — defaults to
   single profile. Becomes required with multi-user.

4. **`CharacterStateDoc` gets an optional `CompanionId`** — defaults to `"ani"`.
   No behavioral change, but exists for future companion routing.

5. **Dashboard is a Razor Class Library** — not hardcoded into
   `AniRuntime.Service`. A future `AniRuntime.Web` host can reference the same
   components.

### Explicitly deferred

- Authentication and authorization
- User registration and onboarding
- Companion selection and provisioning
- Per-user model routing
- Multi-tenant data isolation

---

## Component Breakdown

### New Project: `AniRuntime.Dashboard`

```
src/AniRuntime.Dashboard/
  AniRuntime.Dashboard.csproj     — Razor Class Library
  Services/
    ProfileService.cs             — IProfileService impl (SQLite-backed)
  Dtos/
    UserProfileDto.cs
    MemoryRecordDto.cs
    ConversationThreadDto.cs
    AniStatusDto.cs
  Endpoints/
    ProfileEndpoints.cs           — MapGroup("/api/v1/profile")
    AniStateEndpoints.cs          — MapGroup("/api/v1/ani")
    MemoryEndpoints.cs            — MapGroup("/api/v1/memories")
    ConversationEndpoints.cs      — MapGroup("/api/v1/conversations")
  Components/
    Pages/
      Dashboard.razor             — main dashboard page
      ProfileEditor.razor         — user profile form
      MemoryViewer.razor          — memory browser with tabs
      ConversationHistory.razor   — conversation thread viewer
      Journal.razor               — inner thought stream
    Shared/
      EmotionalStateCard.razor    — current emotional state (or placeholder)
      EmotionalChart.razor        — time-series chart
      AniStatusBar.razor          — uptime, mode, last cycle info
      DesireGauge.razor           — visual desire-to-connect indicator
  wwwroot/
    css/
      dashboard.css               — minimal custom styles (Pico CSS)
```

### Changes to Existing Projects

**`AniRuntime.Core`:**
- New model: `UserProfile`
- New interface: `IProfileService`
- Add `CompanionId` to `CharacterStateDoc` (optional, defaults to `"ani"`)
- Add pagination methods to `IMemoryService` and `IConversationService`

**`AniRuntime.Memory`:**
- `SqliteProfileService` implementing `IProfileService`
- New table: `user_profiles`
- Pagination query implementations
- Profile migration logic (extract from existing CharacterStateDoc)

**`AniRuntime.Service`:**
- `Program.cs`: add Blazor Server, register `IProfileService`, map API
  endpoints, wire profile change callbacks
- `appsettings.json`: add `Dashboard` section (port config)

**`AniRuntime.Perception`:**
- `RssPerceptionSource`: add `InvalidateCache()`, read feeds from profile
- `ContactStatePerceptionSource`: subscribe to profile changes for routine updates

---

## Implementation Priority

| # | Task | Impact | Effort | Dependencies |
|---|------|--------|--------|-------------|
| 1 | Profile model + IProfileService + SQLite impl | Foundation | Low | None |
| 2 | Profile migration (extract from CharacterStateDoc) | No-wipe updates | Low | Task 1 |
| 3 | REST API endpoints (profile CRUD) | Dashboard + future clients | Medium | Task 1 |
| 4 | Blazor Server host in Program.cs | Enables dashboard UI | Low | None |
| 5 | Profile editor component | First user-facing feature | Medium | Tasks 3, 4 |
| 6 | Hot-reload wiring (profile → perception sources) | Live profile updates | Low | Tasks 1, 2 |
| 7 | Memory viewer (read-only, paginated) | Window into Ani's mind | Medium | Task 4 |
| 8 | Conversation history viewer | Read past conversations | Low | Task 7 |
| 9 | Journal view (inner thought stream) | Most intimate window | Low | Task 7 |
| 10 | Companion status card (live emotional state, desire, mood) | Fun gamification — "how is she feeling?" | Low | Task 4, Phase 2 emotional state |
| 11 | Emotional state time-series chart | Visualize feelings over time | Medium | Task 10 |
| 12 | Mood coloring (emotional state → tone) | Messages feel alive | Low | Phase 2 emotional state |
| 13 | Reflection layer (post-thought introspection) | Richer inner life, better outreach grounding | Low | None |
| 14 | Receiving care (bidirectional relationship) | Companion feels real | Medium | Task 12 |
| 15 | Calendar integration | Precise attentive check-ins | Medium | Tasks 1, 5 (dashboard) |
| 16 | Home Assistant integration | Ambient home awareness | Medium | Tasks 1, 5 (dashboard) |
| 17 | Self-awareness feedback loop | Anti-repetition, diversity | Medium | Task 7 (memory viewer data) |
| 18 | Own interests / autonomy balance | Prevents parrot problem | Low | None |

### Recommended order

**Foundation (dashboard + profile):**
1. **Tasks 1-2** — Profile model and migration. Foundation for everything.
   Proves the static/transactional split works without data loss.
2. **Task 4** — Blazor Server host. Proves single-process approach works.
3. **Task 3** — API endpoints. Profile CRUD first, read-only endpoints next.
4. **Task 6** — Hot-reload. Profile changes must take effect immediately.
5. **Task 5** — Profile editor. First visible feature the user can use.

**Viewers (read-only, low risk):**
6. **Tasks 7-9** — Memory/conversation/journal viewers.
7. **Task 10** — Companion status card. Quick win with high delight factor.
8. **Task 11** — Emotional state time-series chart.

**Behavioral features (make her feel real):**
9. **Tasks 12-13** — Mood coloring + reflection layer. No dependencies, immediate
   impact on message quality and inner thought richness. Both are model-agnostic
   architectural contributions for the research paper.
10. **Task 14** — Receiving care. Bidirectional relationship.
11. **Task 18** — Own interests. Quick config change, immediate diversity.

**Integrations (require dashboard):**
12. **Task 14** — Calendar integration. Precise schedule awareness.
13. **Task 15** — Home Assistant. Ambient home state.

**Meta-cognition:**
14. **Task 16** — Self-awareness. Anti-repetition, topic diversity.

---

## Open Questions

1. **Port binding.** Kestrel needs a port for Blazor Server. The service has no
   HTTP listener today. Recommendation: configurable via `Dashboard:Port` in
   appsettings, default 5080. Ensure no conflict with Ollama (11434).

2. **CSS framework.** Recommendation: Pico CSS — 10KB, classless, semantic HTML
   looks good automatically, no build step. Alternatives: Bootstrap (default
   Blazor), Tailwind CDN.

3. **Emotional history retention.** 30 days at ~140 rows/day = ~4,200 rows.
   Trivial. Should be configurable.

4. **RSS feed validation.** When the user adds a feed via the dashboard, validate
   the URL is reachable and returns valid RSS/Atom. Quick HEAD + content-type
   check prevents silent failures.

5. **Memory deletion.** Should Mark be able to make Ani forget something?
   Philosophically interesting. Recommendation: not in Phase 3. Read-only first.
   If added later, it should feel deliberate — confirmation dialog, logged, no
   bulk delete.

6. **Log viewer.** Should the dashboard surface Serilog diagnostic logs? The
   journal covers inner thoughts, but connection failures and parse errors are
   useful for debugging. Not critical for MVP.

7. **Persona versioning.** When Ani's model is re-trained (v3 → v4), the persona
   fields in CharacterStateDoc need updating without wiping learned data. The
   profile/learned split makes this possible — a "re-seed persona" endpoint could
   update only the Learned identity fields from a new `character-seed.json` while
   preserving all Profile data and accumulated memories.
