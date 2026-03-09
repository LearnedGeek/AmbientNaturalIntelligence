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

## Task 3 — Inbound SMS: Models & Persistence

**Why third:** The data layer must exist before the perception source can
store conversations or the cycle processor can detect conversation mode.

### What to build

**Models** (in `AniRuntime.Core/Models/`):

```csharp
public class ConversationThread
{
    public Guid                      Id            { get; set; } = Guid.NewGuid();
    public DateTimeOffset            StartedAt     { get; set; }
    public DateTimeOffset            LastMessageAt { get; set; }
    public bool                      IsActive      { get; set; } = true;
    public string                    InitiatedBy   { get; set; } = "mark"; // "ani" | "mark"
    public List<ConversationMessage> Messages      { get; set; } = new();
}

public class ConversationMessage
{
    public string         Role    { get; set; } = "mark"; // "ani" | "mark"
    public string         Content { get; set; } = string.Empty;
    public DateTimeOffset SentAt  { get; set; } = DateTimeOffset.UtcNow;
}
```

**Interface** (in `AniRuntime.Core/Interfaces/`):

```csharp
public interface IConversationService
{
    Task<ConversationThread?> GetActiveThreadAsync(CancellationToken ct = default);
    Task SaveThreadAsync(ConversationThread thread, CancellationToken ct = default);
    Task AddMessageAsync(Guid threadId, ConversationMessage message, CancellationToken ct = default);
    Task CloseThreadAsync(Guid threadId, CancellationToken ct = default);
}
```

**Implementation** — `SqliteConversationService` in `AniRuntime.Memory`.
Stores threads and messages in two SQLite tables: `conversation_threads` and
`conversation_messages`. Thread closure saves the full exchange as an episodic
memory record.

### Config additions (AniOptions)

```csharp
// Conversation mode
public double ConversationHeartbeatSeconds  { get; set; } = 45.0;
public double ConversationTimeoutMinutes    { get; set; } = 15.0;
```

### Done when

- `IConversationService` implemented with SQLite backing
- Thread CRUD tested (create, add message, close, get active)
- Closed threads produce an episodic memory summary

---

## Task 4 — TwilioInboundPerceptionSource

**Why fourth:** This is the ear. Without it, Ani doesn't know Mark replied.

### What to build

A new `IPerceptionSource` that polls Twilio's message list API for inbound SMS
sent to the Ani phone number since the last poll.

```
Twilio REST API: GET /2010-04-01/Accounts/{sid}/Messages.json
  ?To={AniNumber}&DateSent>={lastPollTime}&Direction=inbound
```

**Key design points:**
- Uses `IHttpClientFactory` (same pattern as `RssPerceptionSource`)
- Tracks `_lastPollTime` to avoid re-processing old messages
- Each new inbound message becomes a `PerceptionEvent` with category
  `Communication` and high `MarkRelevance` (0.9)
- Creates or extends a `ConversationThread` via `IConversationService`
- Signals an early wake (see Task 5)

### Done when

- Source polls Twilio successfully and surfaces new messages
- Messages are recorded in the conversation thread
- Perception events appear in the cognitive cycle context

---

## Task 5 — Early Wake Mechanism

**Why fifth:** Without early wake, Mark texts and waits up to 45 minutes for
Ani to notice. The heartbeat must be interruptible.

### What to change

**AniHeartbeatService** — replace `Task.Delay(delay, stoppingToken)` with a
delay that can be cancelled early:

```csharp
// New field
private CancellationTokenSource? _wakeCts;

// Public method for perception sources to call
public void RequestEarlyWake()
{
    _wakeCts?.Cancel();
}

// In ExecuteAsync loop
_wakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
try { await Task.Delay(delay, _wakeCts.Token); }
catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
{
    _log.LogInformation("Early wake triggered — conversation mode");
}
```

**Conversation heartbeat** — when an active conversation thread exists,
`ComputeNextWakeTime` should return `ConversationHeartbeatSeconds` instead of
the normal exponential delay.

### Done when

- External code can trigger an early wake
- Active conversation thread shortens the heartbeat to ~45 seconds
- Heartbeat returns to normal ambient timing when thread closes

---

## Task 6 — Conversation-Aware Cognitive Cycle

**Why sixth:** The cycle processor needs to detect conversation mode and route
to reply generation instead of (or in addition to) ambient thought.

### What to change in CognitiveCycleProcessor

Add a conversation branch after perception polling:

```
1. Poll perceptions (existing)
2. Check: is there an active conversation thread with unread messages?
   YES → Conversation mode:
     a. Build context with full thread history
     b. Generate reply decision (shouldReply + message) via conversation model
     c. If shouldReply: send via Twilio, add to thread
     d. If !shouldReply: do nothing (she read it, chose silence)
     e. Check thread timeout — close if expired
   NO → Ambient mode (existing flow)
3. Persist perceptions (existing)
```

**Reply/No-Reply decision** — structured JSON like outreach:
```json
{ "shouldReply": true, "message": "hey baby, how was your day?" }
```

**Three no-reply conditions (baked in):**
1. Last message in thread is Ani's — conversation is already "answered"
2. Terminal message detected (lol, haha, goodnight, emoji-only)
3. Model decides no reply needed (genuine silence)

### Done when

- Cycle detects active conversation and generates contextual replies
- Reply/no-reply decision works correctly
- Thread timeout closes inactive conversations
- Closed threads are stored as episodic memory

---

## Task 7 — Conversation Reply Prompts

**Why seventh:** The conversation model needs different prompts than ambient
inner thought or outreach initiation.

### What to build in PromptBuilder

**`BuildConversationReplyPrompt(snapshot, thread)`** — system prompt positions
Ani as responding to Mark in an active conversation. Includes:
- Full thread history as chat messages
- Character state for personality consistency
- Current perceptions for grounding
- Relevant memories from semantic search

**`BuildReplyDecisionPrompt(snapshot, thread)`** — JSON-mode prompt that
determines whether to reply. Returns `{ shouldReply, reasoning }`.

Key prompt constraint: "You do NOT need to have the last word. If the
conversation feels complete, say so. Silence is okay."

### Done when

- Reply prompts generate natural, contextual responses
- Decision prompt correctly identifies terminal messages
- Prompts use the conversation model (not inner monologue model)

---

## Task 8 — Wire Up & Integration Test

### What to do

1. Register all new services in `Program.cs`
2. Add `Twilio` section config for inbound polling
3. End-to-end manual test:
   - Ani sends outreach SMS
   - Mark replies
   - Ani detects reply within 60 seconds
   - Ani responds contextually
   - Conversation threads and closes naturally
   - Thread stored as episodic memory
4. Run full test suite — 0 errors, 0 warnings

### Done when

- Full conversation loop works end-to-end
- All existing tests still pass
- New tests cover conversation lifecycle

---

## Completion Criteria

| Task | Done when |
|---|---|
| ✅ 1. Mark's Likely State | Inner thoughts reference Mark's schedule |
| ✅ 2. Perception persistence | Perceptions saved with embeddings, findable via search |
| 3. Conversation models | Thread CRUD works, closure produces episodic memory |
| 4. Twilio inbound | Inbound SMS detected and surfaced as perception events |
| 5. Early wake | Heartbeat interrupts on message arrival, shortens in conversation mode |
| 6. Conversation cycle | Reply/no-reply decision works, ambient/conversation mode switching |
| 7. Reply prompts | Natural contextual replies, terminal message detection |
| 8. Integration | Full conversation loop end-to-end, all tests pass |

Once all eight are done, Ani can hear Mark and respond. She's no longer a
broadcaster — she's a participant. The conversation boundary resolves naturally
through timing, and she knows when to let silence be the last word.
