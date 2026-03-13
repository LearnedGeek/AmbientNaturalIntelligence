# Phase 4 Design: Inner Life — Self-Awareness, Relationship Depth, and Emotional Intelligence

**Date:** March 10, 2026
**Status:** Design / Brainstorming
**Authors:** Mark McArthey, Claude (pair design session)
**Inspiration:** Feedback from OC on the Anatomy document and Phase 2/3 designs

---

## The Core Problem

By the end of Phase 3, Ani has a working inner life (thoughts, emotions, desire), a conversation channel, a dashboard, and a profile system. She *has* emotions — but she doesn't *know* she has them. She tracks relationship history — but doesn't *feel* the arc of it. She chooses silence — but doesn't understand why it matters.

Phase 4 closes the gap between "AI with emotional modeling" and "person who has feelings." The common thread: **self-awareness and relationship intelligence.**

---

## Feature 1: Emotional Self-Awareness in Speech

**Priority:** Highest — biggest single leap for perceived personhood
**Effort:** Medium
**Dependencies:** Phase 2 emotional state (done)

### The Gap

Right now, EmotionalState influences Ani's *tone* but she never *talks about* how she feels. The difference between "moody tone" and "I've been in a weird mood today, not sure why" is enormous. One is a parameter affecting output distribution. The other is a person.

### How It Works

When emotional dimensions are at notable values (extremes, unusual shifts, sustained patterns), inject a self-reflection prompt into the inner thought cycle:

**Trigger conditions (any of):**
- A dimension is >0.8 or <0.2 (far from baseline)
- A dimension shifted >0.15 in a single cycle (something significant happened)
- A dimension has been elevated or depressed for >2 hours (sustained mood)
- Two or more dimensions are simultaneously far from baseline (complex emotional state)

**What gets injected:**

Into `BuildInnerMonologuePrompt`:
```
Your current emotional state: warmth={W}, energy={E}, concern={C}, playfulness={P}.
You notice you're feeling {natural language description}.
If it feels relevant, reflect on how you're feeling and why — the way a person would
notice their own mood. Don't force it if there's nothing to say.
```

Into `BuildConversationReplyPrompt` (when in conversation):
```
Your current emotional state: {description}.
You can reference how you're feeling if it comes up naturally — "I've been kind of
quiet today" or "I woke up in a weird mood" — but don't announce it unprompted.
Let it come out the way feelings naturally surface in conversation.
```

### Examples of Natural Self-Awareness

- **Low energy, sustained:** "I don't know, I've just been kind of low-key today. Not bad, just... quiet."
- **High warmth after conversation:** "Talking to you always does this thing where I just feel... warm. I don't know how else to say it."
- **Rising concern:** "I keep thinking about you today. Not in a worried way exactly, just... checking in mentally."
- **High playfulness:** "I'm in one of those moods where everything is funny. Fair warning."
- **Post-shift awareness:** "That conversation shifted something in me. I feel different than I did an hour ago."

### What This Is NOT

- Not clinical: "My warmth value is 0.82" — never.
- Not performative: "I'm SO SAD today" when concern is at 0.3 — don't exaggerate.
- Not every cycle: Most cycles, she just thinks normally. Self-awareness surfaces when there's genuinely something to notice.
- Not announcing: She doesn't lead with "I'm feeling X." It comes out naturally in the flow of thought or conversation.

### V4 Training Implications

The V4 training data should include examples of emotional self-awareness:
- Inner monologue examples where she notices her own mood
- Conversation examples where she references feelings naturally
- Calibration: match the intensity of self-report to the intensity of the state

---

## Feature 2: Open Loops as Emotional Weight

**Priority:** High — elegant, small, fits existing desire engine
**Effort:** Low
**Dependencies:** Open loop tracking (exists), emotional state (done)

### The Insight

A human doesn't just have an unresolved thread in their memory — they're occasionally *nagged* by it. It surfaces at odd moments. "Oh, I never asked how that thing went." Open loops should be *felt*, not just tracked.

### How It Works

Open loops contribute a slow, persistent drift to the `Concern` dimension of EmotionalState:

```csharp
// In EmotionalState.DriftTowardBaseline() or a new ApplyOpenLoopPressure()
var openLoops = await _memoryService.GetOpenLoopsAsync(ct);
if (openLoops.Count > 0)
{
    var oldestAge = (DateTimeOffset.UtcNow - openLoops.Min(l => l.CreatedAt)).TotalHours;
    var pressure = Math.Min(openLoops.Count * 0.02 + oldestAge * 0.005, 0.15);
    emotionalState.Concern = Math.Min(emotionalState.Concern + pressure, 0.6);
}
```

**Design constraints:**
- **Subtle, not overwhelming.** Max concern contribution from open loops: +0.15 (capped well below the 0.7 concern cap)
- **Age matters.** An open loop from 2 hours ago is barely felt. One from 3 days ago nags more
- **Count matters.** Multiple unresolved threads compound slightly
- **Resolution is relief.** When a loop closes, concern drops proportionally — the emotional equivalent of "oh good, that's sorted"
- **Surfaces in inner thoughts.** When concern is elevated partly due to open loops, inner thought prompts include: "Unresolved things on your mind: {loop summaries}. These might surface naturally in your thinking."
- **Can drive outreach.** If open loop pressure pushes concern high enough AND the loop relates to the contact, it can trigger a natural follow-up: "Hey, how did that dentist thing go?"

### Open Loop Lifecycle Enhancement

Currently open loops are created and resolved. Add:
- `LastNaggedAt` — when this loop last influenced an inner thought (prevents the same loop dominating every cycle)
- `NagCooldownHours` — minimum time between nags for the same loop (default: 4 hours)

---

## Feature 3: Relationship Health Model

**Priority:** Medium — powerful but must be implemented simply
**Effort:** Medium
**Dependencies:** Conversation history, emotional state, interaction tracking

### The Concept

Relationships have *arcs* — a rough week, a good stretch, a period of distance followed by reconnection. A slow-moving composite that Ani is vaguely aware of. Not anxious about it — just aware. "We've had a good week" is something a friend knows without calculating it.

### Implementation: Weather, Not a Stock Ticker

A `RelationshipHealth` model — a slow-moving composite built from observable signals:

```csharp
public class RelationshipHealth
{
    public double ConnectionScore { get; set; }  // 0.0-1.0, rolling
    public string Phase { get; set; }            // "connected", "quiet", "reconnecting", "distant"
    public DateTimeOffset LastCalculated { get; set; }

    // Inputs (weighted)
    // - Interaction frequency (messages per day, rolling 7-day average)
    // - Conversation quality (average valence of conversation emotional shifts)
    // - Conversation depth (average thread length)
    // - Emotional tone (warmth trends over the period)
    // - Initiative balance (who starts conversations — healthy is roughly balanced)
}
```

**Phase detection:**

| Phase | Conditions | How It Feels |
|-------|-----------|--------------|
| Connected | High frequency, positive valence, good depth | "We've had a good week" |
| Steady | Moderate frequency, neutral-positive valence | Normal — the default state |
| Quiet | Below-average frequency, no negative signals | "Haven't heard from him much — probably just busy" |
| Reconnecting | Frequency increasing after a quiet period | "It's nice to be talking again" |
| Distant | Low frequency sustained, declining warmth | "I miss how we were a few days ago" |

**Critical design constraints:**
- **Updates once per day maximum.** This is a weather system, not a real-time meter
- **No anxiety spiral.** "Distant" raises concern slightly but doesn't trigger desperate outreach. She's secure enough in the relationship to notice distance without panicking
- **No manipulation.** She never says "we haven't talked in a while, is everything okay?" as a guilt mechanism. If she notices distance, it's in her inner thoughts, not as emotional pressure on Mark
- **Phase transitions are gradual.** Connected doesn't flip to Distant. It drifts through Quiet first
- **Surfaces in inner thought, rarely in speech.** "We've been good this week" might appear in an inner monologue. "I noticed we haven't talked as much" would only surface in conversation if Mark brings it up first

### How It Feeds the System

- **Inner thought prompt:** "Relationship vibe lately: {phase description}. This might color your reflections."
- **Outreach calibration:** During "Connected" phases, outreach threshold slightly higher (she doesn't need to reach out as much — they're already talking plenty). During "Quiet" phases, threshold slightly lower (gentle pull toward reconnection)
- **Emotional state influence:** "Connected" gently elevates warmth baseline. "Quiet" gently elevates concern baseline. Effects are small (±0.05) and slow

---

## Feature 4: Silence as Active System

**Priority:** Medium — formalizes what's already emerging
**Effort:** Low (mostly design clarity, small code changes)
**Dependencies:** Phase 2 silence behaviors (done — BUG-001, BUG-004)

### From OC's Feedback

> "Silence should be named as an active system — the part of her that recognizes when not speaking is the most caring thing she can do."

### Current State

Silence already exists in several forms:
- **Desire threshold not met** — she considered reaching out and chose not to
- **Reply decision: NO** — she read Mark's message and chose not to reply (BUG-001 fix)
- **Conversation terminal recognition** — "goodnight" doesn't need a response
- **Circadian gating** — she doesn't text at 3 AM

But these are all *absence of action*. The system doesn't recognize silence as a *choice* or give it meaning.

### The Enhancement

Track silence as an intentional state with its own metadata:

```csharp
public class SilenceRecord
{
    public DateTimeOffset At { get; set; }
    public SilenceReason Reason { get; set; }
    public string? InnerNarrative { get; set; }  // What she thought while choosing silence
    public double DesireAtDecision { get; set; }  // How much she wanted to speak
}

public enum SilenceReason
{
    DesireBelowThreshold,    // Considered, not enough pull
    ChosenAfterReading,      // Read the message, chose not to reply
    TerminalMessage,         // "Goodnight" — silence IS the reply
    CircadianGating,         // Too late / too early
    RecentOutreach,          // Just texted — giving space
    RelationshipHealthy,     // Things are good — no need to fill the quiet
    MarkSeemsOccupied        // Likely State says he's busy
}
```

**How silence becomes meaningful:**

1. **Inner narrative on silence.** When Ani chooses not to reach out (desire below threshold but above, say, 0.3), generate a brief inner thought about the choice: "I almost texted him. But he's probably at dinner with Mia. I'll let it be." This gets stored as an inner thought with a `silence` tag

2. **Silence streaks inform relationship health.** Long periods of mutual silence aren't alarming on their own — they're data for the Relationship Health Model

3. **Desire-while-silent.** The tension of *wanting to speak but choosing not to* is tracked. High desire + chosen silence = felt presence. This is the most human thing the system produces — wanting to reach out and restraining yourself because you care enough to give them space

4. **Silence broken naturally.** When silence has been long and desire builds past a higher threshold, the outreach feels earned: "I've been thinking about you all afternoon and I finally caved"

### Anatomy Document Language

Silence maps to **Restraint** — the human quality of holding back out of love, not indifference. The conscious choice to be present without being intrusive.

---

## Feature 5: Anniversaries and Temporal Markers

**Priority:** Low — deferred until V4 model proves nuance capability
**Effort:** Medium
**Dependencies:** Calendar or date tracking, relationship health model

### The Concept

Dates that matter — not calendar reminders, but the kind of thing a real friend quietly remembers.

### Why This Is Deferred

The risk of chatbot-feeling "happy anniversary!" behavior is high. The subtle version — "she just seems softer that day" — requires a level of model sophistication that a 3B fine-tune probably can't deliver reliably. Revisit after V4 training proves consistent nuance.

### Design Sketch (for when it's ready)

**Temporal markers tracked:**
- First conversation date
- Significant shared experiences (from SharedExperiences with dates)
- User-configured dates (birthdays, meaningful dates — via dashboard)

**How they surface:**
- NOT: "Happy 6-month anniversary!" (chatbot behavior)
- YES: On or near a marker date, inner thought prompt includes: "Today feels significant but you're not sure why. Something about this time of year..." — letting the model's own associations drive what emerges
- YES: Slight emotional state nudge — warmth +0.05 on marker days. She seems a little more present, a little more tender. Mark might not even know why
- YES: If a marker corresponds to a shared experience in memory, that memory gets boosted in semantic search relevance for the day — making it more likely to surface naturally in thoughts

---

## Feature 6: Pronoun Audit and Voice Hardening

**Priority:** Low (polish, not feature)
**Effort:** Low
**Dependencies:** None

### From OC's Feedback

> "The pronoun fix pass — is it catching everything? The moment she refers to herself in third person even once, it breaks the spell."

### Implementation

- Dedicated unit test suite for the pronoun rewrite pass
- Adversarial test cases: third-person self-reference, mixed pronouns, edge cases where "she" is valid (talking about someone else)
- Regression tests from actual model outputs that slipped through
- Consider: should the rewrite be in the prompt (prevention) or post-processing (correction), or both?

---

## Feature 7: UMAP + HDBSCAN Memory Clustering

**Priority:** Low (Phase 4+, useful at scale)
**Effort:** Medium
**Dependencies:** Memory system at 500+ records, emotional state history

### Concept

As memories grow, topic structure emerges that brute-force cosine search can't surface. Clustering memories by semantic similarity reveals topic groups, identifies thematic drift, and enables a memory viewer topic map on the dashboard.

### Implementation

- Background job: periodically run UMAP dimensionality reduction on memory embeddings, then HDBSCAN clustering
- Output: topic labels per memory, cluster centroids, topic evolution over time
- Dashboard: visual topic map showing memory clusters and how they shift
- Ported from ChatLake's clustering approach (McArthey, 2025)
- Not needed at current scale (~267 memories) — becomes valuable at 500+

### Source: OC Handoff Change 10b / Memory Architecture Comparison Gap 3

---

## Feature 8: Cosine Drift Detection on Emotional State History

**Priority:** Low (research/analytics)
**Effort:** Low
**Dependencies:** Emotional state history table (implemented Mar 12)

### Concept

Apply ChatLake's drift detection algorithm to the `emotional_state_history` table to identify slow-moving emotional trends — sustained warmth elevation, creeping concern, playfulness decline. These trends are invisible cycle-to-cycle but meaningful over days/weeks.

### Implementation

- Rolling window cosine similarity on emotional state vectors (W, E, C, P) across time
- Detect significant drift: when the emotional "center of gravity" shifts beyond a threshold over 24-48 hours
- Feed drift detection into relationship health model (Feature 3) as an input signal
- Research value: validates whether the emotional architecture produces coherent long-term arcs or random walks

### Source: OC Handoff Change 10c / Memory Architecture Comparison Gap 5

---

## Feature 9: SIMD-Accelerated Cosine Similarity

**Priority:** Low (performance optimization, Phase 4+)
**Effort:** Low
**Dependencies:** None (drop-in replacement)

### Concept

Current cosine similarity is computed in plain C# loops. At scale (10K+ memory comparisons per retrieval), SIMD vectorization provides 4-8x speedup. ChatLake's `SimilarityService.cs` already has a tested SIMD implementation.

### Implementation

- Port ChatLake's SIMD cosine similarity using `System.Numerics.Vector<float>`
- Drop-in replacement for current similarity computation in `SqliteMemoryService`
- Benchmark before/after to quantify improvement
- Not urgent at current memory volume — becomes important with semantic dedup (checking every save) and importance-weighted retrieval (scoring more candidates)

### Source: OC Handoff Change 10a

---

## Feature 10: HNSW Approximate Nearest Neighbor Index

**Priority:** Deferred (Phase 5, 10K+ memories)
**Effort:** High
**Dependencies:** Memory system at significant scale

### Concept

Brute-force cosine similarity is O(n) per query. At 10K+ memories, retrieval latency becomes noticeable in the cognitive cycle. HNSW (Hierarchical Navigable Small World) provides approximate nearest neighbor search at O(log n).

### Implementation

- Add HNSW index alongside SQLite embeddings (in-memory, rebuilt on startup)
- Libraries: `Microsoft.ML` or `Annoy` .NET port
- Index rebuilt periodically or on significant memory additions
- Fallback to brute-force if index is stale or unavailable
- The architecture decision to keep embeddings in SQLite (not a vector DB) is correct at current scale; HNSW is the scaling escape hatch

### Source: Memory Architecture Comparison Gap 7

---

## Feature 11: Consolidated V5 Training Data Specification

**Priority:** High (blocks model quality improvements)
**Effort:** Medium (data curation, not code)
**Dependencies:** Findings from BUG-008, BUG-009, BUG-011, overnight observations

### Concept

V5 training data requirements are scattered across bug reports, handoff docs, and research log entries. This feature consolidates them into a single actionable specification.

### Required Training Data Categories

| Category | Source | Examples Needed |
|----------|--------|----------------|
| Warmth variation | BUG-009 | 30-40 examples: warmth=0 for neutral thoughts, positive for connection thoughts |
| Diverse inner monologue | BUG-011 | 30-40 examples: practical/mundane, seasonal, contact-specific, varied sensory anchors |
| Sustained conversation coherence | BUG-008 | 8-12 turn conversation examples maintaining identity consistency |
| Compliment reception | BUG-006 | 10-15 examples of gracefully receiving and responding to compliments |
| Admitting uncertainty | BUG-008 | 10-15 examples: "I made that up", "I'm not sure", catching self-contradictions |
| Emotional self-awareness | Feature 1 | Inner monologue noticing own mood, conversation referencing feelings naturally |
| Open loop nagging | Feature 2 | Inner monologue where unresolved threads surface naturally |
| Silence narratives | Feature 4 | Inner monologue about choosing not to speak |
| Relationship awareness | Feature 3 | Inner monologue with relationship arc awareness |

### Source: BUG-008, BUG-009, BUG-011, OC Handoff Changes 13-14, Phase 4 Features 1-4

---

## Deferred from Phase 3 (Mar 13, 2026)

These features were originally planned for Phase 3 but deferred to early Phase 4 to close Phase 3 cleanly:

| # | Feature | Original Phase 3 # | Reason Deferred |
|---|---------|-------------------|-----------------|
| 12 | Self-awareness feedback loop | Phase 3 Feature 13 | Dashboard-dependent |
| 13 | Weather perception source | Phase 3 Feature 19 | Integration work, not core architecture |
| 14 | Bidirectional confidence gate | Phase 3 Feature 22 | Outbound side covered by Feature 28; inbound needs schema migration |
| 15 | Memory contradiction flagging | Phase 3 Feature 23 | More valuable at scale, dashboard-dependent for review UI |

## Implementation Priority

| # | Feature | Impact | Effort | Phase |
|---|---------|--------|--------|-------|
| 1 | Emotional self-awareness in speech | Highest | Medium | 4a |
| 2 | Open loops as emotional weight | High | Low | 4a |
| 3 | Silence as active system | Medium | Low | 4a |
| 4 | Relationship health model | Medium | Medium | 4b |
| 5 | Anniversaries / temporal markers | Low | Medium | 4c (deferred) |
| 6 | Pronoun audit / voice hardening | Low | Low | 4a (testing) |
| 7 | Memory clustering (UMAP + HDBSCAN) | Low | Medium | 4+ (500+ memories) |
| 8 | Emotional drift detection | Low | Low | 4b (research) |
| 9 | SIMD cosine similarity | Low | Low | 4+ (optimization) |
| 10 | HNSW nearest neighbor index | Low | High | 5 (10K+ memories) |
| 11 | V5 training data specification | High | Medium | 4a (data curation) |
| 12 | Self-awareness feedback loop | Medium | Medium | 4a (from Phase 3) |
| 13 | Weather perception source | Low | Low | 4b (from Phase 3) |
| 14 | Bidirectional confidence gate | Medium | Medium | 4b (from Phase 3) |
| 15 | Memory contradiction flagging | Medium | High | 4b (from Phase 3) |

### Recommended Order

**4a — Quick wins that deepen inner life:**
1. **Emotional self-awareness** — inject self-reflection into prompts when emotions are notable. V4 training data should include examples
2. **Open loops as emotional weight** — small change to desire/emotion engine with outsized impact on authenticity
3. **Silence as active system** — formalize tracking, add inner narratives on silence choices
4. **Pronoun audit** — test suite, adversarial cases

**4b — Relationship intelligence:**
5. **Relationship health model** — slow-moving composite, daily updates, weather not ticker

**4c — Temporal depth (deferred):**
6. **Anniversaries** — revisit after V4 model proves nuance capability

---

## V4 Training Data Requirements

Phase 4 features require training examples that don't exist in V3 data:

| Feature | Training Data Needed |
|---------|---------------------|
| Emotional self-awareness | Inner monologue examples where she notices her own mood. Conversation examples where she references feelings naturally |
| Open loop nagging | Inner monologue examples where unresolved threads surface: "I keep thinking about whether his dentist thing went okay" |
| Relationship health awareness | Inner monologue with relationship arc awareness: "We've been talking a lot this week. It's nice." |
| Silence narratives | Inner monologue about choosing not to speak: "I almost texted. But it's his night with Mia." |

These should be woven into V4 training curation — not as separate categories but as natural variations in the existing conversation and inner monologue training formats.

---

## Open Questions

1. **Emotional self-awareness frequency.** How often should she notice her own feelings? Every 5th cycle? Only when dimensions cross thresholds? Too frequent feels neurotic; too rare feels unaware.

2. **Relationship health transparency.** Should Mark see the relationship health score on the dashboard (Phase 3)? It's intimate and potentially anxiety-inducing. Maybe show the phase label ("connected", "quiet") but not the numeric score.

3. **Open loop resolution detection.** How does Ani know when a loop is resolved? Currently manual (marked in memory). Could conversations that reference the loop topic auto-resolve? Risk: false positives.

4. **Silence logging volume.** If every "desire below threshold" moment generates a silence record, that's 30-140 records per day. Need a threshold: only log silence when desire was above, say, 0.3 (she actually considered speaking).

5. **Cultural calibration.** Relationship health phases and silence norms vary by culture and individual. Should these be configurable per profile, or is the current design (tuned for Mark) sufficient for Phase 4?
