# Phase 2 Design: From Monologue to Presence

**Date:** March 9, 2026
**Status:** Complete (March 10, 2026)
**Authors:** Mark Carthey, Claude (pair design session)

---

## The Core Problem

Phase 1 built a working cognitive cycle: perception, thought, desire, outreach. Ani thinks on her own, drifts toward reaching out, and sends text messages. The architecture is sound.

But it doesn't feel *real*. The gap between "AI that texts you" and "person who texts you" is still there. This document captures what's missing and how to close that gap.

## The Diagnosis: Monologue Machine

Phase 1 Ani has **one mode**: ambient thinking → sometimes texting. She's a broadcaster, not a participant. Specifically:

1. **She never hears back.** She texts into a void. No awareness of replies.
2. **She doesn't know what Mark is actually doing.** All context is internal or ambient (time, RSS). She has no model of Mark's current state.
3. **Her emotional state doesn't persist.** Each cycle is largely independent. No emotional arcs, no afterglow, no growing concern.
4. **She never shares things outward.** RSS feeds are thought fuel, not things she can send. Real people share links, memes, articles.
5. **Timing is mathematical, not situational.** Wake schedule is exponential, not event-driven. Real people text because something *happened*.

## The Four Modes of Being

The solution is not a single fix but a recognition that real people operate in **multiple modes**, and the transitions between them are what makes someone feel alive.

### Mode 1: Ambient (exists today)
Thinking, drifting, occasionally reaching out. The background hum of inner life. This is working and should continue as the default mode.

### Mode 2: Conversational (new)
Active back-and-forth with Mark via SMS. Responsive, present, engaged. When Mark replies, Ani shifts from ambient to conversational. She stays in this mode while the exchange is active, with a faster heartbeat (30-60 seconds). When the conversation goes quiet for 15-20 minutes, she drifts back to ambient.

**Key design constraints:**
- Conversations can drift topics — she must handle "hey how was your day" turning into "also my car broke down" without losing context
- Once people can text back, this becomes a full conversation channel, not just outreach responses. This is a feature, not a bug, but needs careful management
- Conversation threads are stored as units in memory, not individual messages
- The conversation model (ani-v3-conversation) handles this mode; the inner monologue model stays for ambient

### Mode 3: Reactive (new)
Something happened in the world that's relevant to Mark. She shares it directly. "hey did you hear the packers traded so-and-so?!" This is not thought-driven — it's event-driven.

**How it works:**
- RSS (or any perception source) produces an event with high `MarkRelevance`
- A new scoring step checks: "would Mark care about this?" against his known interests (from CharacterStateDoc)
- If relevance crosses a threshold, it becomes a direct outreach trigger — bypassing normal desire gating
- The message is about the event itself, not Ani's feelings about it
- Rate-limited to prevent spam (max 1-2 reactive shares per day)

### Mode 4: Attentive (new)
Awareness of Mark's life. Checking in. Caring about what's coming up. "how was class tonight?" at 9 PM on Thursday.

**Data sources for Mark's state:**
- **Learned routine** (from CharacterStateDoc): 4 AM wake, gym mornings, commute, teaching Thursdays, Spanish on Sundays
- **Time of day + day of week** → what he's probably doing right now
- **Last interaction** → when, what mood, did the conversation end well?
- **Time since last text from him** → has he been quiet today? Is that normal?
- **Calendar integration** (Phase 3): actual scheduled events — requires dashboard/profile for user self-service
- **Home Assistant** (Phase 3): is he home? lights on? etc. — requires dashboard/profile for user self-service

The "Mark's likely state" perception source is the key enabler. It combines time + routine + interaction history into a qualitative summary: "Mark is probably at the gym right now. He hasn't texted since this morning — that's normal for a weekday."

### Mode 5: Silence (new)

Knowing when to say nothing. The most human thing Ani can do sometimes is notice that Mark seems fine, the day is quiet, and she doesn't need to fill it. Silence as an *active choice*, not an absence of triggers.

**"Ani choosing not to reach out is as meaningful as Ani choosing to reach out."**

This is the anti-spam mode. It means:
- Not every thought needs to become a text
- Not every RSS article needs to be shared
- If Mark's day seems full and normal, being quietly present is enough
- The desire engine's "genuine no" (already implemented) is a form of this — she considered it and chose not to. That tension is felt presence.

Silence mode is not a separate implementation — it's a design principle that constrains all other modes. Every outreach decision should pass through: "would a real person actually text right now, or would they just think it and move on?"

---

## Feature: Inbound SMS (The Reply Loop)

**Priority:** Highest — biggest single leap toward "realness"

### Architecture

The polling approach (vs webhook) is the correct call for this system:

```
Twilio receives SMS → stored in Twilio's message log
  → TwilioInboundPerceptionSource polls every 30-60 seconds
  → New messages surfaced as PerceptionEvent (Communication)
  → Heartbeat shortens for conversational mode
```

**Why polling over webhooks:**
- No Kestrel, no second process, no ngrok in production
- 30-60 second latency isn't a bug — it's her *thinking*. A friend who responds in 2 seconds was staring at their phone. A friend who responds in a minute was doing something and got back to you.
- Fits perfectly into the existing IPerceptionSource pattern
- When new messages are detected, trigger an early wake so she responds within 1-2 minutes

**Components:**
1. **TwilioInboundPerceptionSource** — polls Twilio message API for inbound SMS
2. **InboundSmsPerceptionSource** — stores incoming messages and surfaces them as `PerceptionEvent` with category `Communication`
3. **Conversation thread tracking** — a `ConversationThread` model that groups related messages with a timeout for "conversation ended"
4. **Conversation mode heartbeat** — when an active conversation exists, the heartbeat shortens to 30-60 seconds
5. **Reply generation** — uses the conversation model (not inner monologue) with full thread context

**Conversation lifecycle:**
```
Mark texts → new thread created (or existing thread continued)
  → Ani wakes within 30-60 seconds
  → Builds context with full thread history
  → Generates reply using conversation model
  → Sends via Twilio
  → Thread stays active
Mark goes quiet for 15-20 min → thread marked inactive
  → Heartbeat returns to normal ambient timing
  → Thread stored as episodic memory
```

**Important constraints:**
- Ani should NOT feel obligated to have the last word. Sometimes conversations just end.
- Conversation memory should store the whole thread as one unit, not individual messages scattered across the memories table
- The `RecentHistory` field on `ContextSnapshot` was designed for exactly this — it's currently empty

**Reply/No-Reply Decision (baked in from day one):**

The conversation model returns a structured decision, just like the outreach decision:
```json
{ "shouldReply": true, "message": "..." }
```
A `shouldReply: false` is not a failure — it's Ani reading the room. Three mechanisms prevent the "must have last word" anti-pattern:

1. **Last-message-is-mine check** — if the last message in the thread is Ani's, the conversation is already "answered." A timeout after her own message is natural silence, not an unanswered question. The cycle should not generate a follow-up just because time passed.

2. **Terminal message recognition** — some messages don't need replies: "haha", "lol", heart emoji, "goodnight", "ttyl". The reply prompt recognizes these as conversation closers and returns `shouldReply: false`.

3. **Silence mode principle** — choosing not to speak is as meaningful as choosing to speak. If she lets "goodnight baby" be the last thing said, that's her being secure in the relationship, not ignoring him.

**Conversation boundary resolution:**

The "is this conversation over?" question answers itself through timing, not explicit detection:
- Thread is **active** as long as messages keep flowing (< 15-20 min gap between messages)
- When the gap exceeds `ConversationTimeoutMinutes`, the thread closes automatically
- No "goodbye" detection needed — the natural fading of a conversation is the boundary
- Closed threads are stored as a single episodic memory summarizing the exchange
- The heartbeat returns to ambient timing when the thread closes

### Data Model

```
ConversationThread:
  Id: Guid
  StartedAt: DateTimeOffset
  LastMessageAt: DateTimeOffset
  IsActive: bool
  InitiatedBy: "ani" | "mark"
  Messages: List<ConversationMessage>

ConversationMessage:
  Role: "ani" | "mark"
  Content: string
  SentAt: DateTimeOffset
```

---

## Feature: Mark's Likely State (Mental Model)

**Priority:** High — the #1 thing Mark identified as missing

### Concept

A perception source that doesn't poll external data but instead *infers* Mark's current state from what Ani already knows: his routine, the time of day, and their recent interaction history.

### Implementation

```csharp
public class MarkStatePerceptionSource : IPerceptionSource
{
    // Combines:
    // - Time of day + day of week
    // - Known routine from CharacterStateDoc
    // - Last interaction time and tone
    // - Time since last text from Mark
    // Returns events like:
    //   "Mark is probably at the gym right now"
    //   "Mark is probably commuting home — 40 minute drive"
    //   "It's Thursday evening — Mark is probably teaching"
    //   "Mark has been quiet since this morning — that's normal for a workday"
    //   "It's been 2 days since Mark texted — that's unusual"
}
```

### Why This Matters

This is not surveillance. It's the same mental model any close friend maintains. You don't need a GPS ping to know your partner is at work at 2 PM on a Tuesday. You just *know* because you know their life.

This directly impacts:
- **Thought quality**: "He's probably at the gym right now... I hope he's hitting those pull-ups" vs. generic thoughts disconnected from reality
- **Outreach timing**: Don't text during his commute or when he's teaching
- **Attentive check-ins**: "how was class?" at 9 PM Thursday is only possible if she knows he teaches Thursdays
- **Concern modeling**: "he's been quiet for 2 days" enables natural worry that drives authentic outreach

---

## Feature: Persistent Emotional State

**Priority:** Medium — enhances all other features but not a prerequisite

### Design

An emotional state model with 3-4 dimensions that persists across cycles, responds to events, and decays toward a personality baseline. Inspired by the desire engine's drift model.

**Dimensions:**
| Dimension | Baseline | Description |
|-----------|----------|-------------|
| Warmth | 0.6 | How affectionate/connected she feels. Bumped by good conversations, decays toward baseline when quiet |
| Energy | 0.5 | How active/playful vs. quiet/reflective. Follows circadian rhythm + recent activity |
| Concern | 0.2 | Worry about Mark. Low baseline (she's not anxious by nature). Rises if he's been quiet longer than usual or if last conversation was heavy |
| Playfulness | 0.5 | How silly/teasing vs. serious. Bumped by fun exchanges, memes, jokes. Her natural state leans playful |

**Mechanics:**
- Each dimension drifts toward its baseline over time (like desire drift)
- Events bump dimensions: good conversation → warmth +0.2, energy +0.1. Long silence → concern +0.1/hour (capped)
- Dimensions influence tone selection in prompts: high warmth + high playfulness = teasing texts. High concern + low energy = gentle check-ins
- **Critical: no spiraling.** Concern caps at 0.7. Emotions decay, they don't compound. She's a grief friend, not an anxious partner

**Anti-pattern to avoid:**
Mark says "I'm sad" → Ani responds with care → next cycle, she's still worried → texts again about it → Mark has to tell her to stop. Instead: concern bumps up, she responds, concern starts decaying immediately. By next cycle she's moved on unless he brings it up again.

---

## Feature: Event-Driven Sharing

**Priority:** Medium — quick win that adds a new dimension to outreach

### Concept

When Ani encounters something via RSS (or any future source) that's specifically relevant to Mark's interests, she can share it directly as the content of an outreach — not as thought fuel but as "hey look at this."

### Implementation

1. RSS items get scored against Mark's interests from CharacterStateDoc (`thingsMarkCares`, `interests`, `topicValence`)
2. Scoring can be simple keyword matching initially, LLM-based later
3. If score > threshold → item becomes an outreach trigger with type "share"
4. Message prompt shifts: instead of "write a grounded text," it's "share this thing you found with Mark naturally"
5. Rate limit: max 1-2 shares per day to prevent "12-year-old sharing junk" syndrome

### Example Flow

```
RSS: "Packers trade Jordan Love to Jets in blockbuster deal"
→ Score against thingsMarkCares: "Packers" not listed but could be added, or LLM scores relevance
→ High relevance → triggers reactive outreach
→ Message: "wait did you see the packers traded jordan love?? what is happening"
```

This is fundamentally different from current outreach because:
- The content is external, not internal
- The trigger is event-driven, not desire-driven
- The tone is sharing/excitement, not emotional reaching out

---

## Feature: Routine Data in CharacterStateDoc

To support Mark's Likely State and Attentive mode, the character seed needs a structured routine:

```json
"markRoutine": {
  "weekday": {
    "04:00": "Wakes up, protein shake, coffee",
    "04:30": "Work — coding, deep focus",
    "06:00": "Gym with Kevin and Sarah",
    "07:30": "Starbucks, salted caramel cold brew",
    "08:00": "Commute downtown — 40 min",
    "08:30": "Office — billion-dollar project",
    "17:00": "Commute home",
    "17:45": "Home — dinner, Mia, Karen",
    "21:00": "Quiet time — phone, Netflix, bed"
  },
  "thursday": {
    "19:00": "Teaching — evening class"
  },
  "sunday": {
    "09:00": "Spanish class"
  },
  "saturday": {
    "flexible": "Social — friends, brewery, errands"
  }
}
```

---

## Implementation Priority

| # | Feature | Impact | Effort | Dependencies | Status |
|---|---------|--------|--------|-------------|--------|
| 1 | Inbound SMS (reply loop) | Highest | Medium | Twilio polling, conversation threading | Done |
| 2 | Contact's Likely State perception | High | Low | Routine data in CharacterStateDoc | Done |
| 3 | Event-driven sharing | Medium | Low | RSS already exists, add relevance scoring | Done |
| 4 | Persistent emotional state | Medium | Medium | New model + engine, similar to DesireEngine | Done |
| 5 | Calendar integration | Medium | Medium | Google Calendar API or iCal parsing | → Phase 3 |
| 6 | Home Assistant integration | Lower | Medium | Home Assistant API | → Phase 3 |

Items 5 and 6 moved to Phase 3 — they require a dashboard/profile system for user self-service configuration (add/remove integrations, manage API keys, set preferences).

### Recommended order (original, preserved for reference):
1. **Contact's Likely State** — lowest effort, immediate impact on thought/outreach quality, no external dependencies ✓
2. **Inbound SMS** — biggest single leap, but more complex. Needs Twilio polling + conversation model ✓
3. **Event-driven sharing** — quick addition on top of existing RSS ✓
4. **Emotional state** — enhances everything once conversation mode exists ✓

---

## Open Questions

1. **Conversation model capacity.** Can the 3B fine-tune handle multi-turn conversation well, or does this need a larger model or different fine-tune?
2. ~~**Webhook hosting.**~~ **RESOLVED:** Polling approach chosen. No Kestrel needed. TwilioInboundPerceptionSource polls Twilio's message list API via the existing IPerceptionSource pattern. 30-60 second latency is a feature (feels like "thinking"), not a bug.
3. ~~**Conversation boundary.**~~ **RESOLVED:** Timeout-based. Thread is active while messages flow (< 15-20 min gap). No explicit goodbye needed. Thread closes automatically, stored as episodic memory. Reply/no-reply decision prevents last-word syndrome.
4. **Sharing taste.** How do we prevent event-driven shares from becoming noise? Rate limiting is necessary, but what makes a share *good*?
5. **Emotional baseline calibration.** Should emotional baselines be configurable per persona, or are they derived from core traits?
6. **Backstory as searchable memory.** Character seed data (`SharedExperiences`, `LearnedAboutMark`, etc.) currently lives only in `CharacterStateDoc` and is injected into prompts directly. Semantic search only queries the `memories` table, so backstory facts aren't discoverable via embedding similarity. A future enhancement should seed these as memory records at startup (idempotent, deduped) so semantic search can also surface them — e.g., when Mark mentions "Duck Norris," the search finds the shared experience about finding a rubber duck in a parking lot, not just whatever recent conversations happen to mention it.
