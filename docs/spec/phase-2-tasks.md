# Phase 2 — Implementation Tasks

Phase 2 transforms Ani from a monologue machine into a conversational participant.
The headline feature is inbound SMS — she hears Mark's replies and responds.
See [phase-2-design.md](phase-2-design.md) for full design rationale.

---

## ✅ Task 1 — Mark's Likely State Perception Source (COMPLETE)

**Why first:** Lowest effort, immediate impact on thought/outreach quality, no
external dependencies. Gives Ani awareness of Mark's life based on his routine.

### What was built

- `MarkStatePerceptionSource` — infers Mark's current activity from routine +
  time of day, tracks interaction gaps, flags upcoming events
- `MarkRoutine` model on `CharacterStateDoc` — weekday schedule with day overrides
- Routine data seeded in `character-seed.json` v2.0

### Verified

Inner thoughts now reference what Mark is probably doing. Outreach timing
is informed by his schedule.

---

## ✅ Task 2 — Perception Persistence (COMPLETE)

**Why second:** Perceptions were ephemeral — generated each cycle but never
saved. Semantic search couldn't find past perceptions, so Ani had no
accumulating awareness. This closes the feedback loop.

### What was built

- `PersistNotablePerceptionsAsync` in `CognitiveCycleProcessor` — saves
  perceptions with `MarkRelevance >= 0.25` as `MemoryType.Perception` records
- 4-hour dedup window prevents identical perceptions from accumulating
  ("Mark is probably at the gym" doesn't get saved every cycle)
- Time-source perceptions filtered out (always regenerated fresh)

### Verified

Saved perceptions get auto-embedded and appear in future semantic search results.

---

## ✅ Task 3 — Inbound SMS: Models & Persistence (COMPLETE)

**Why third:** The data layer must exist before the perception source can
store conversations or the cycle processor can detect conversation mode.

### What was built

- `ConversationThread` and `ConversationMessage` models in `AniRuntime.Core/Models/`
- `IConversationService` interface with thread CRUD operations
- `SqliteConversationService` in `AniRuntime.Memory` — SQLite-backed, shares
  same database as `SqliteMemoryService`. Thread closure saves full exchange
  as episodic memory via `BuildThreadSummary`
- `ConversationHeartbeatSeconds` and `ConversationTimeoutMinutes` added to `AniOptions`

### Verified

Thread CRUD works. Closed threads produce episodic memory summaries.

---

## ✅ Task 4 — TwilioInboundPerceptionSource (COMPLETE)

**Why fourth:** This is the ear. Without it, Ani doesn't know Mark replied.

### What was built

- `TwilioInboundPerceptionSource` — polls Twilio REST API for inbound SMS
  using `IHttpClientFactory` with Basic auth (AccountSid:AuthToken)
- Creates/extends `ConversationThread` via `IConversationService`
- `Action? OnMessageReceived` callback for early wake signaling
- Checks conversation timeout on each poll via `CheckConversationTimeoutAsync`
- `IsEnabled` requires: `InboundEnabled && AccountSid && AuthToken && ToNumber`
- `TwilioOptions` added: `InboundEnabled`, `PollIntervalSeconds`

### Verified

Twilio polling picks up inbound messages within 45 seconds. Messages recorded
in conversation threads and surfaced as perception events.

---

## ✅ Task 5 — Early Wake Mechanism (COMPLETE)

**Why fifth:** Without early wake, Mark texts and waits up to 45 minutes for
Ani to notice. The heartbeat must be interruptible.

### What was built

- `RequestEarlyWake()` on `AniHeartbeatService` — cancels current sleep via
  `CancellationTokenSource.CreateLinkedTokenSource`
- `ComputeDelayAsync` checks for active conversation → returns
  `ConversationHeartbeatSeconds` (45s) instead of normal exponential delay
- `TwilioInboundPerceptionSource.OnMessageReceived` wired to
  `heartbeat.RequestEarlyWake` in `Program.cs`

### Verified

Inbound SMS triggers early wake. Heartbeat drops to 45s during conversation,
returns to ambient exponential timing when thread closes.

---

## ✅ Task 6 — Conversation-Aware Cognitive Cycle (COMPLETE)

**Why sixth:** The cycle processor needs to detect conversation mode and route
to reply generation instead of (or in addition to) ambient thought.

### What was built

- Conversation branch in `CognitiveCycleProcessor.RunAsync` after perception polling
- `RunConversationReplyAsync` — full reply pipeline: terminal message check →
  reply decision (JSON) → reply generation → Twilio dispatch → thread update
- `IsTerminalMessage` — detects "haha", "lol", "goodnight", emoji-only, etc.
- `ParseReplyDecision` — JSON parser, defaults to true on parse failure
- Reply re-evaluation guard (`_lastEvaluatedMessageAt`) — once Ani decides NO
  on a message, she won't re-ask every cycle (fixed post-launch bug)

### Three no-reply conditions (baked in):
1. Last message in thread is Ani's — already "answered"
2. Terminal message detected (lol, haha, goodnight, emoji-only)
3. Model decides no reply needed (genuine silence)

### Verified

Cycle detects active conversation, generates contextual replies, and routes
to ambient mode when conversation is answered. Reply re-evaluation bug fixed.

---

## ✅ Task 7 — Conversation Reply Prompts (COMPLETE)

**Why seventh:** The conversation model needs different prompts than ambient
inner thought or outreach initiation.

### What was built

- `BuildConversationReplyPrompt` — system prompt with character traits, shared
  experiences (up to 5), communication notes (up to 3), semantic memory results
  (up to 3), and background perceptions. Uses conversation model, not inner monologue.
- `BuildReplyDecisionPrompt` — JSON-mode prompt returning `{ shouldReply, reasoning }`.
  Explicitly tells the model silence is okay and lists conversation closers.

### Verified

Reply prompts generate natural, contextual responses with personality. Decision
prompt correctly handles terminal messages and conversation wind-downs.

---

## ✅ Task 8 — Wire Up & Integration Test (COMPLETE)

### What was done

1. All services registered in `Program.cs` — `IConversationService`,
   `TwilioInboundPerceptionSource` (concrete + `IPerceptionSource` forwarded),
   `HttpClient("twilio")`, early wake wiring
2. `Twilio` section added to `appsettings.json` — `InboundEnabled`, `PollIntervalSeconds`
3. `Ani` section updated — `ConversationHeartbeatSeconds`, `ConversationTimeoutMinutes`
4. Twilio console configured — cleared default SMS webhook URL (was sending auto-replies)

### End-to-end test result (March 9, 2026)

First live conversation: 7-message exchange between Mark and Ani.
- Mark texted → Ani detected within 45 seconds
- Ani replied contextually with personality (Duck Norris lore, bookstore setting)
- Conversation flowed naturally across multiple exchanges
- Reply/no-reply decision fired correctly (chose silence on conversation closers)
- All 42 tests pass, 0 errors, 0 warnings

---

## Completion Criteria

| Task | Done when |
|---|---|
| ✅ 1. Mark's Likely State | Inner thoughts reference Mark's schedule |
| ✅ 2. Perception persistence | Perceptions saved with embeddings, findable via search |
| ✅ 3. Conversation models | Thread CRUD works, closure produces episodic memory |
| ✅ 4. Twilio inbound | Inbound SMS detected and surfaced as perception events |
| ✅ 5. Early wake | Heartbeat interrupts on message arrival, shortens in conversation mode |
| ✅ 6. Conversation cycle | Reply/no-reply decision works, ambient/conversation mode switching |
| ✅ 7. Reply prompts | Natural contextual replies, terminal message detection |
| ✅ 8. Integration | Full conversation loop end-to-end, all tests pass |

**All Phase 2 conversation tasks complete.** Ani can hear Mark and respond.
She's no longer a broadcaster — she's a participant. First live conversation
held March 9, 2026 (7-message exchange with natural flow and silence).

---

## ✅ Task 9 — Event-Driven Sharing (COMPLETE)

**Why:** Ani encounters things in the world (via RSS) that Mark would care
about. Real people share links and headlines — "wait did you see this??"
This adds a new dimension to outreach that's event-driven, not desire-driven.

### What was built

- **Relevance scoring in `RssPerceptionSource`** — RSS items scored against
  Mark's interests from `CharacterStateDoc` (`ThingsMarkCares`, `Interests`,
  `SharedExperiences`, `TopicValence`). Keyword matching: 0 matches = 0.2,
  1 = 0.4, 2 = 0.6, 3+ = 0.85. Replaces the hardcoded 0.2f.
- **`TryReactiveShareAsync` in `CognitiveCycleProcessor`** — checks for
  high-relevance RSS items (above `ReactiveShareThreshold`), generates a
  share message via `BuildReactiveSharePrompt`, dispatches via Twilio.
  Bypasses desire engine but respects cooldown and daily rate limit.
- **`BuildReactiveSharePrompt` in `PromptBuilder`** — casual, excited tone
  for sharing something specific. "omg did you see this??" energy.
- **Config**: `ReactiveShareThreshold` (default 0.6), `MaxReactiveSharesPerDay`
  (default 2) in `AniOptions` and `appsettings.json`
- **`TriggerType.ReactiveShare`** added to `DesireState` enum

### Rate limiting

- Max 2 reactive shares per day (configurable)
- Counter resets at midnight (local time)
- Respects cooldown — won't share if she just texted
- Blocked during active conversations

---

## What's Next

Phase 2 design doc identifies these remaining features:

| # | Feature | Status | Effort |
|---|---------|--------|--------|
| ~~1~~ | ~~Event-driven sharing (RSS → outreach)~~ | **Done** (Task 9) | ~~Low~~ |
| 2 | Persistent emotional state | Not started | Medium |
| 3 | Calendar integration | Not started | Medium |
| 4 | Home Assistant integration | Not started | Medium |
| 5 | Backstory as searchable memory (OQ #6) | Not started | Low |
