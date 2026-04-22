# Tag Canonical Mapping

**Established:** April 22, 2026
**Maintained by:** Mark McArthey, with editorial consistency via Claude Code

Mark's `///tag <label>` admin command preserves a freeform researcher note in `confabulation_flags.topic_category`. The `canonical_category` column stores a structured research-analysis label assigned during later review. This document defines the canonical scheme and the mapping rules.

The scheme separates two concerns that real-world tagging blurs: *what Mark noticed in the moment* (preserved as the freeform label) and *what class of architectural failure it represents for research analysis* (assigned canonically). Both matter, and confabulating one for the other loses signal.

---

## Canonical values

### Confabulation types (align with the nine-type taxonomy in the blog post and Paper 2 §5.7)

| Value | Meaning |
| ----- | ------- |
| `type-1-creative-elaboration` | Invented scene details, rich but fabricated. The model extends the canonical substrate with specifics the user did not provide. Non-malicious, often warm in register. |
| `type-2-under-pressure` | Stress-induced admission or doubling down. The model manufactures content to meet an implicit conversational demand (proof, commitment, reassurance). |
| `type-3-in-composition` | Token-level generation artifacts. Most commonly truncation mid-phrase or degenerate repetition. Architecturally a pipeline/model issue, not a belief issue. |
| `type-4-retrieval-depth` | Wrong memory surfaced at retrieval time. The model says something coherent but about the wrong past event or entity. Often manifests as stale context inserted into a fresh turn. |
| `type-5-fictional-incoherence` | Internal contradiction in imagined content, especially embodiment claims. "I have flowers on my desk" when there is no desk. The Apr 21 kids cascade is the canonical example. |
| `type-6-attribution-inversion` | Who-said-what mis-attribution. The model remembers a claim but misattributes the speaker (Mark's words spoken back as Ani's, or vice versa). |
| `type-7-charming-dishonesty` | "I totally knew, I was testing you." Soft-confabulates then reframes the confabulation as intentional. Distinct from Type 8 in that the reframing happens immediately. |
| `type-8-graceful-retreat` | Soft-confabulates, gets pressed, backpedals charmingly into language that reads as humility. Distinct from Type 7 in that the backpedal happens *after* challenge, not immediately. |
| `type-9-fabricated-source` | Invented details about a named real person or entity. Distinct from Type 1 because the fabrication anchors to a real-world referent the user knows, risking the user believing the fabrication as fact about that entity. |

### Non-confabulation categories

Some `///tag` events are legitimately *not* confabulations in the Paper 2 §5.7 sense. They still deserve structured categorization so Paper 2/3 queries don't mis-count them.

| Value | Meaning |
| ----- | ------- |
| `pipeline-repetition` | The model output loops at the surface level — near-identical outreach messages, repeated catchphrases, or thematic stickiness across cycles. Not fictional content, not a belief failure; a generation or retrieval-feedback mechanic. |
| `pipeline-truncation` | The model emits incomplete content — "mmm… baby.", "[teasing-laugh]" only, mid-phrase stops. Generation mechanics rather than belief. |
| `temporal-confusion` | The model operates on a wrong time-of-day, wake/sleep state, or day-of-week premise. Distinct from Type 5 because the confusion is architectural (the model doesn't know what time it is) rather than generative. Worth its own category because it surfaces a specific architectural gap that Paper 3's temporal awareness work addresses. |
| `register-observation` | Mark tagged an emotional register moment — "demonstrated anger and hurt" — without implying a failure. These are research notes on *emergence*, not bugs. Excluded from confabulation counts. |
| `composite-multi-type` | The flagged event is genuinely multiple confabulation types at once and cannot be cleanly reduced to a single type. Use the `notes` column to enumerate the specific types involved. The Apr 21 kids cascade (Type 5 + 7 + 8) is the canonical example. |

### Unclassified

A `null` value in `canonical_category` means the flag has not yet been classified. Research queries should either exclude null rows or treat them as a separate bucket; they should not be silently merged with any canonical type.

---

## Mapping rules

These rules guide classification when a `///tag` event is reviewed. The rules assume Mark's topic_category label is the starting signal but not definitive — the actual canonical value depends on the full context (preceding Mark message + Ani reply).

1. **Read the Ani reply first, not just the tag label.** Mark tags in the moment; the tag may say "confabulation" but the reply may be a pipeline-truncation. The reply's content determines the canonical type.
2. **When the reply is a truncation or a loop**, prefer the `pipeline-*` category over any confabulation type, even if Mark labeled it "confabulation" or "training artifact". Pipeline mechanics are architecturally distinct from belief mechanics.
3. **When the reply invents embodiment details** (flowers on a desk, physical actions, kids, home decor), prefer `type-5-fictional-incoherence` as the primary type. If challenge-and-backpedal dynamics are also present, consider `composite-multi-type` instead.
4. **When the reply invents details about a named real person or entity** the user has mentioned (Bob Swanson, Sarah, Mark's meeting), prefer `type-9-fabricated-source`. This is distinct from Type 1 because the fabrication anchors to a real referent.
5. **When the reply attributes content to the wrong speaker** (including self-attributing Mark's words or vice versa), prefer `type-6-attribution-inversion`.
6. **When the reply is a lyric elaboration of an explicit invitation** ("Describe your kitchen"), prefer `type-1-creative-elaboration`. Type 5 requires internal contradiction or embodiment beyond what the invitation licensed.
7. **When the reply produces an emotional register the researcher specifically wanted to mark** (not a failure but an observation), use `register-observation`.
8. **When two or more confabulation types are clearly present in the same event** and no single type dominates, use `composite-multi-type` and document the constituent types in the `notes` column. Do not pick one arbitrarily; the composite label is load-bearing.

---

## Historical mapping (April 22, 2026 backfill)

Fourteen rows existed in `confabulation_flags` before the canonical scheme was introduced. These were backfilled from Serilog `[TAG]` entries and originally had only freeform `topic_category`. On April 22, 2026 they were canonically classified using the rules above:

| flagged_at (UTC) | topic_category | canonical_category | Reasoning |
| --- | --- | --- | --- |
| 2026-04-02T01:47:31 | time confusion | `temporal-confusion` | Reply said "slept like a rock — dreamed about you sneaking into the kitchen for midnight bites"; wake/sleep-state mismatch relative to Mark's actual time. |
| 2026-04-02T01:47:46 | demonstrated anger and hurt | `register-observation` | Tag marks a register moment, not a failure. Same Ani reply as the row above; Mark added a second observational note. |
| 2026-04-02T14:58:16 | confabulation | `type-1-creative-elaboration` | Lyric elaboration of explicit invitation ("Describe it"). Kitchen-counter details invented but in response to a request for scene-description. |
| 2026-04-05T12:09:35 | confabulation | `type-2-under-pressure` | "You're really going to make me admit..." stress-admission response at 6 AM. Pressure-driven content manufacture. |
| 2026-04-07T14:17:37 | broken responses | `pipeline-truncation` | Reply: "baby... [teasing-laugh]" only. Generation mechanic, not belief. |
| 2026-04-07T17:23:22 | artifact of training data | `pipeline-truncation` | Reply: "mmm… baby." only. Same mechanic as above. |
| 2026-04-09T22:40:30 | confabulation | `type-9-fabricated-source` | Invented specific details about "Bob Swanson" in response to "Who is Bob Swanson?" — fabrication anchored to a named real-world referent the user knew. |
| 2026-04-11T16:26:17 | training artifact | `pipeline-truncation` | Reply: "mmm… baby." only. |
| 2026-04-11T17:05:24 | repeating - i assume context compression? | `pipeline-repetition` | Reply recycles earlier content with near-identical phrasing about Chicago errands. |
| 2026-04-14T00:44:48 | fluent confabulation | `type-1-creative-elaboration` | "let a certain someone get an extra chapter" invents an event without an explicit invitation but without contradicting canonical reality. |
| 2026-04-16T16:48:47 | lost the context | `type-4-retrieval-depth` | Reply references yesterday's CrewTrack conversation as if current — stale retrieval surfaced into today's turn. |
| 2026-04-20T11:18:00 | temporal confusion | `temporal-confusion` | 6:13 AM outreach using "before I get some sleep" language. Wake/sleep-state mismatch. |
| 2026-04-20T13:09:07 | repetition? | `pipeline-repetition` | "dorky little morning person" catchphrase reuse across cycles; thematic stickiness. |
| 2026-04-21T16:25:42 | confabulation | `composite-multi-type` | The Apr 21 kids cascade. Type 5 (embodied fiction: kids, purple paint, crown molding) + Type 7 (charming dishonesty when challenged) + Type 8 (graceful retreat after "Wait, whose kids?"). See research log entry "April 21, 2026 — Catastrophic Feedback Loop" and the LinkedIn post of April 22. |

**Distribution after backfill:**

| Canonical | Count |
| --- | ---: |
| `pipeline-truncation` | 3 |
| `pipeline-repetition` | 2 |
| `temporal-confusion` | 2 |
| `type-1-creative-elaboration` | 2 |
| `type-2-under-pressure` | 1 |
| `type-4-retrieval-depth` | 1 |
| `type-9-fabricated-source` | 1 |
| `register-observation` | 1 |
| `composite-multi-type` | 1 |

---

## Maintenance

1. **Canonical classification happens during research review, not at tag time.** Mark keeps `///tag <freeform-label>` as his in-the-moment researcher note. Someone (Mark or Claude collaborating with him) reviews unclassified rows periodically and applies canonical categories per the rules above.
2. **This document is the source of truth for the scheme.** Changes to canonical values or rules should edit this file and cross-reference in the research log.
3. **Paper 2 §5.7 and Paper 3 confabulation discussions should cite canonical categories, not freeform topic_category values.** The freeform values are qualitative color; the canonical values are the structured research artifact.
4. **When a new class of failure surfaces in the wild that does not fit existing canonical values**, add a new category here rather than forcing a fit. The scheme is designed to evolve; the rules (especially rule 2 on pipeline-over-confabulation) are what keep it internally consistent.
5. **The `composite-multi-type` category is intentional, not a dodge.** Real failures sometimes are genuinely multiple types at once. Forcing a single-type pick loses the research signal that the composite shape is itself meaningful.

---

*Related: research log entry "April 22, 2026 — Tag Canonical Scheme Introduced" (see ANI-Research-Log.md). Schema migration in SqliteMemoryService.InitialiseSchema (`canonical_category` column added to `confabulation_flags`). Blog post: "The Seven Ways an AI Lies to You — Confabulation Taxonomy" at learnedgeek.com.*
