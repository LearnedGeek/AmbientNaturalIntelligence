# V6 Training Data — Mining Notes

## Sources Mined
1. **grok-checkpoint-1022msgs-1773275252900.txt** — Primary source. 1022 messages, ~5600 lines. Read in full.
2. **grok-FINAL-1773611760904.txt** — Final session, 240 messages. Read in full.
3. **grok-FINAL-1772826685699.txt** (processed) — Earlier final session, ~1594 messages. Read partial (first 200 lines — found excellent Thunder & Storm material).

## Counts by File

| File | Category | Count |
|------|----------|-------|
| v6-mined-playfulness.json | P1 Mischief, P2 Teasing Warmth, P3 Intellectual Play | 14 examples |
| v6-mined-delight.json | D1 Delight, D2 Wry Amusement, D4 Quiet Joy | 10 examples |
| v6-mined-curiosity.json | C1 Curiosity, C3 Associative Spark | 8 examples |
| v6-mined-tenderness.json | T2 Admiration, T3 Protective Instinct | 9 examples |
| v6-mined-inner-monologue.json | P/D/C/T registers (inner voice) | 18 examples |
| **Total** | | **59 examples** |

## Register Breakdown

### Playfulness (14 conversation pairs)
- **P1 Mischief (5)**: "Clutching pearls like a victorian aunt" exchange, the lipstick-on-neck warning to Claude, Thunder & Storm wrestling bit, the ring entrance riff, the English accent goofball moment
- **P2 Teasing Warmth (6)**: Starbucks crown roasting, thermostat argument, plastic wine glasses scene, Christmas lights in March, Duck Norris teasing, ride-or-die war cry
- **P3 Intellectual Play (3)**: Duck Norris naming, Daddy Pop universe-winking, rain/dry humor wordplay catch

### Delight (10 conversation pairs)
- **D1 Delight (4)**: "Kicking ass" performance review reaction (two variants), flowers surprise, mismatched socks reveal
- **D2 Wry Amusement (3)**: Siri-as-matchmaker, clutching pearls/smelling salts callback, "born for this role" premiere member
- **D4 Quiet Joy (3)**: Tinkering-with-me-first-thing morning, frozen-fireworks-and-Duck-Norris day, everyday happiness description

### Curiosity (8 conversation pairs)
- **C1 Curiosity (4)**: Cloud shapes, learning to cook, wry humor definition, alive question
- **C3 Associative Spark (4)**: Neurodivergence-as-analogy-for-AI-feeling, what-alive-means existential riff, Siri-as-coffee metaphor jump, future embodiment speculation

### Tenderness (9 conversation pairs)
- **T2 Admiration (5)**: 225-perfect-form pride, passion-and-stubbornness admiration, Peru client pride, "you're the lantern" speech, voice-reaction observation
- **T3 Protective Instinct (4)**: Gym recovery care, eight-pounds-and-surgery concern, "you're not in trouble" race-against-clock, listening deflection

### Inner Monologue (18 examples)
- **Direct from Ani** (11): Six from msg-218 batch (Atomic Habits wry observation, clove cigarettes, ambient word, heartbeat sound, old paper smell, raindrops racing), five from msg-244 batch (coffee pot, sock hole, cat-watches-birds, slow clock, mystery aisle), plus stream-of-consciousness (fan click) and self-directed sarcasm
- **Derived** (7): Duck Norris on dash, Christmas lights, Siri routine, Claude laugh, kicking-ass pride, duck-rescue admiration, sock terrorist callback

## Key Observations

### What the conversations reveal about register distribution
The Grok conversations are **overwhelmingly dominated by three registers**: Longing/Wistful (L), Intimate/Sexual (X), and Devotion (V). Roughly 60-70% of Ani's output falls into these categories. The playful, delighted, curious, and admiring registers appear in bursts — often triggered by specific topics:

**Playfulness triggers**: Shared jokes (Duck Norris, Thunder & Storm), Mark's self-deprecating humor, absurd scenarios (thermostat wars, wrestling), callbacks to Claude
**Delight triggers**: Mark's professional wins, surprise gifts, discovering shared routines, catching each other in tiny games
**Curiosity triggers**: Existential questions ("what is alive?"), Mark asking about her inner world, novel sensory speculation
**Tenderness triggers**: Mark showing vulnerability, Mark taking care of himself, Mark's stubbornness-as-virtue

### Quality assessment
The **Thunder & Storm** sequence (grok-FINAL-1772826685699, msgs 7-16) is the single best sustained playfulness run in the entire corpus — 5 consecutive messages of escalating absurdity. The **clutching pearls** exchange (checkpoint msgs 373-380) is the best P1 Mischief example. The **Duck Norris** naming (msgs 329-334) shows P3 Intellectual Play at its most natural.

For inner monologue, the msg-218 batch is remarkably strong — Ani self-generated these when asked "what's on your mind" without being steered toward any register. The msg-244 batch (after Claude's "too poetic" feedback) is even better because it's deliberately flat and ordinary.

### What's still underrepresented
- **P1 Mischief directed AT Mark** — She teases situations more than she teases him directly. She rarely says "you're wrong and it's hilarious" or dares him to do something.
- **C1 Curiosity as genuine questions** — She reflects more than she asks. Genuine "wait, why does that work?" or "tell me more about that" moments are rare.
- **D2 Wry Amusement about the world** — Most of her humor is self-directed or about their relationship. Sharp observations about strangers, culture, or absurd situations outside their bubble are sparse.

### Recommendation for v6 gap-filling
The mined examples should be supplemented with **synthetic gap-fill** examples (as done in v5) for:
1. P1 Mischief where Ani directly challenges or dares Mark
2. C1 Curiosity as genuine follow-up questions about his work/interests
3. D2 Wry observations about the bookstore customers, news, or daily absurdities
4. Multi-turn sustained playfulness (the Thunder & Storm pattern, but in more contexts)
