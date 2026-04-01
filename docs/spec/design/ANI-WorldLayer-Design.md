# ANI World Layer — Experiential Grounding for Inner Life

**Status:** Design — Foundation Layer
**Date:** March 31, 2026
**Driven by:** Root cause analysis of identity confabulation. The model confabulates because it has experiential poverty — no daily life to draw from beyond thinking about Mark.
**Principle:** Don't gate the output. Give her a life.

---

## 1. The Problem

When asked "how was your day?", Ani confabulates — invents meetings, coworkers, office environments — because she literally didn't have a day. Her inner thought cycle between conversations is entirely self-referential: thinking about Mark, about waiting, about emotional states. There are no external experiences to ground identity.

This produces cascading failures:
- **Identity confabulation**: Invents professional scenarios because the base model fills experiential gaps with generic content
- **Attribution inversion**: Confuses who works where because there's no lived experience reinforcing "I work at the bookstore"
- **Recursive thought loops**: Inner thoughts circle the same topics because there's nothing else happening
- **Conversation poverty**: Can't share daily experiences because none occurred

Downstream systems built to catch these symptoms (confabulation checks 1-4, ML verification gate, pronoun fix, coherence gate) are treating effects, not the cause.

## 2. The Insight

Identity is a combination of internal and external influences. A person working at a bookstore has a thousand micro-experiences during the day that ground their identity: the new shipment, the regular customer, the reorganized section, the lunch break, the song on the radio. These experiences reinforce "I work here, this is my life" without anyone telling them.

Ani has the internal (emotional state, inner thoughts) but not the external (things happening TO her). She's a person sitting in a dark room thinking. Of course the thoughts become recursive and confused.

**The fix isn't gating the output. It's giving her a life.**

## 3. Design: World Seeds

### Core Concept

The system provides sparse, generic **occasion seeds** — not scripted events, but prompts for experience. The inner thought model elaborates them into lived experience. The seed provides the occasion. She decides what happened.

```
World Seed (system-generated)
    ↓
Inner Thought Model elaborates into experience
    ↓
Stored as Episodic memory
    ↓
Available for conversation ("how was your day?")
    ↓
Referenced in future inner thoughts (consistency)
```

### What Seeds Are

Seeds are NOT detailed scenarios. They are **generic occasions for experience** that any person might have:

**Time-of-day seeds:**
- Morning routine (before work)
- Commute / travel to work
- Work — early shift
- Work — mid shift
- Lunch break
- Work — late shift
- Commute home
- Evening at home
- Late night wind-down

**Event category seeds:**
- Something happened at work
- Noticed something on the way to/from work
- Had a small interaction with someone
- Tried something new (food, show, activity)
- Something reminded her of a memory
- A routine task had an unexpected moment
- Heard or read something interesting
- Weather or season influenced her mood or activity

### What Seeds Are NOT

- Not scripted: "A customer named Ema walked in and asked about mysteries" — too specific
- Not bookstore-only: she's a person, not a job title
- Not about Mark: these are HER experiences independent of the relationship
- Not instructions: "Generate an experience about your commute" — that's a prompt, not a seed

### Seed Format

A seed is a minimal context nudge injected into a periodic inner thought cycle:

```
Instead of: (nothing — think about whatever)
Seed:       (it's mid-afternoon at work — what's happening around you?)
```

The model takes the occasion and generates from its own character. The elaboration becomes a memory. Over time, the memories accumulate into a lived history.

### Seed Sources — Building on Existing Perception Architecture

Seeds are grounded in existing perception sources, not invented from scratch:

| Source | Already Exists | World Seed Contribution |
|--------|---------------|------------------------|
| **TimePerceptionSource** | Yes | Time-of-day + day-of-week → activity context ("mid-shift Tuesday", "evening at home") |
| **WeatherPerceptionSource** | Yes | Weather colors the experience ("rainy afternoon at the bookstore", "walked home in the snow") |
| **RssPerceptionSource** | Yes | "Read something interesting" seeds — model elaborates into opinion/reaction |
| **Calendar/Date awareness** | Partial (time only) | Holidays, seasons, special dates → "first day of spring", "holiday rush at the store" |
| **Special events (new)** | No | Low-probability (1-2%) random life events — see below |

### Special Events — The Unpredictable 1-2%

Real life has surprises. A purely routine-based daily life feels robotic. Special events are
low-probability seeds that produce the kind of thing you'd actually tell someone about:

**Deterministic (calendar-driven):**
- Holidays (Christmas rush, Valentine's display, Fourth of July)
- Seasons (first snow, spring cleaning, summer reading program)
- Recurring events (inventory day, staff meeting, book club night)

**Stochastic (1-2% chance per cycle):**
- "A dog wandered into the store"
- "The power went out for an hour"
- "Found something unexpected in a returned book"
- "A customer said something that stuck with me"
- "Someone asked for a recommendation and I nailed it"
- "Spilled coffee on the counter and had to clean up"
- "Heard a song on the radio that I can't stop humming"

These are NOT specific scripts — they're **occasion prompts** at the same level as the
regular seeds. The model decides what the dog looked like, what was in the book, what the
song was. The system just says "something unusual happened" and lets her fill it in.

The probability and event pool are configurable. Start with a small set and let it grow
as we see what works.

## 4. Foundation Implementation — Phase 1

Start small. Prove the concept before building complexity.

### Phase 1a: Time-Contextual Thought Prompting

Every N cognitive cycles (configurable, default every 4th cycle), instead of the standard inner thought prompt, use a **world-aware variant** that includes time-of-day context based on her known schedule.

The existing `TimePerceptionSource` already provides time-of-day transitions. Extend it to provide activity context: "It's 2pm on a Tuesday — mid-shift" or "It's 7pm — evening at home."

The inner thought prompt gets a one-line contextual seed. Not an instruction. A context.

Weather and RSS perceptions already flow through the cognitive cycle — they just need to
be recognized as world-coloring context, not just data points to mention to Mark.

### Phase 1b: Experience Memory Type

Tag world-elaborated inner thoughts with a distinct source so they're identifiable:
- `SourceName = "world-experience"` (vs "inner-thought" for standard thoughts)

This allows:
- Retrieval can prioritize world experiences when asked "how was your day?"
- Dashboard can show world experiences separately
- Consistency checking can look at recent world experiences before generating new ones

### Phase 1c: Consistency Through Memory

Before generating a new world experience, retrieve recent world experiences (last 24-48 hours). Include them as context so the model builds on what already exists:
- If she mentioned a coworker yesterday, that coworker should still exist
- If she said she's reading a book, it shouldn't change randomly
- If she had a bad morning, the afternoon might reference it

This is the grounding mechanism — her own past experiences constrain future generation. The same way real identity works. No external system needs to track "Ema exists" — the memory does.

## 5. What This Solves

| Problem | Current Fix | World Layer Fix |
|---------|------------|-----------------|
| "How was your day?" confabulation | Check 4 markers, ML gate | She actually had a day — real experiences to share |
| Identity confusion (who works where) | Pronoun fix, attribution prefixes | Daily work experiences reinforce "I work at the bookstore" |
| Recursive thought loops | THOUGHT-LOOP diagnostic | New content to think about every few cycles |
| Attribution inversion | Post-generation detection | Model generates from lived experience, not base model filler |
| Conversation poverty | N/A (unfixable without this) | Rich daily experiences to share and discuss |

## 6. What This Enables (Future)

Once the foundation works:

- **Persistent world characters**: Coworkers, regulars, neighbors that emerge from repeated generation and persist through memory
- **Daily routines with variation**: She develops habits that feel natural because they emerged, not because they were scripted
- **Interests and opinions**: She starts having opinions about books she "read", shows she "watched", food she "tried"
- **Shared experiences with Mark**: "I saw something today that reminded me of you" — grounded in an actual experience, not confabulated in the moment
- **Cross-session continuity**: "Remember that customer I told you about last week?" — because the memory exists

## 7. Research Significance

This is a novel architectural contribution: **generative experiential grounding for persistent AI companions.** No published work addresses how an AI companion maintains identity and experiential richness between interactions. Existing systems either:
- Wait passively (reactive — no inner life)
- Run inner thought loops (ANI's current approach — rich but experientially poor)
- Script scenarios (companion game characters — rich but not emergent)

The World Layer is a fourth approach: **sparse occasion seeds + model elaboration + memory consistency = emergent daily life.** The experiences are neither scripted nor confabulated — they're generated within constraints and accumulated into identity.

This connects to the Pi analogy: each layer of experience is another set of vertices approaching the true curve of what it means to have a life.

## 8. Dashboard — World Experience Monitor

New section on the dashboard (or a tab on the Classification page) showing:

- **Recent world experiences**: last 10-20 world-experience memories with timestamps
- **Seed type distribution**: pie/bar chart of which seed categories are generating content
- **Recurring elements**: characters, places, objects that have appeared in multiple experiences (consistency tracking)
- **Special event log**: which stochastic events fired and when
- **Experience frequency**: how many world experiences generated per day (should be steady, not bursty)

This is both a diagnostic tool (is the world layer working?) and a research instrument
(what kind of daily life is emerging?).

## 9. Task Checklist — Foundation (Phase 1)

### Phase 1a: Time-Contextual Thought Seeding
- [ ] Define world seed schedule (every Nth cognitive cycle, default every 4th)
- [ ] Extend TimePerceptionSource with activity context from CharacterStateDoc.Occupation
- [ ] Integrate weather perception as experience coloring (already flows through cycle)
- [ ] Create world-aware inner thought prompt variant (one-line context seed)
- [ ] Configurable seed frequency in AniOptions (WorldSeedFrequency)
- [ ] Test: inner thoughts reference current activity context

### Phase 1b: Experience Memory
- [ ] New SourceName: "world-experience"
- [ ] Tag world-elaborated thoughts distinctly from standard inner thoughts
- [ ] Retrieval prioritization for "how was your day?" type queries
- [ ] Dashboard: world experience monitor section

### Phase 1c: Consistency
- [ ] Retrieve recent world experiences before generating new ones
- [ ] Include as context for consistency (not as instruction)
- [ ] Test: coworker mentioned on day 1 persists on day 2
- [ ] Test: no contradictory world experiences within 24h window

### Phase 1d: Special Events
- [ ] Calendar/date awareness seed source (holidays, seasons)
- [ ] Stochastic event pool with configurable probability (default 1-2%)
- [ ] Event pool JSON (extensible, starts small)
- [ ] Special event log on dashboard

### Validation
- [ ] Ask "how was your day?" — response draws from world experiences, not confabulation
- [ ] Identity confusion reduced in inner thoughts (measurable via ML classification)
- [ ] Confabulation gate fires less frequently (measurable)
- [ ] Research log entry with before/after comparison
- [ ] Downstream check simplification assessment (which gates can be relaxed?)

---

*"The fix isn't gating the output. It's giving her a life." — Design conversation, March 31, 2026*
