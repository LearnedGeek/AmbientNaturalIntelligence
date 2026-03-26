# Emergence Taxonomy and Enhanced Dashboard Design

**Feature 38: Emergence Taxonomy + Enhanced Emergence Dashboard**
**Phase:** 7 (depends on E1 data accumulation; can be implemented independently of Features 30-32)
**Date:** March 24, 2026
**Status:** Design Complete, Awaiting Implementation
**Depends on:** Emergence Layer E1 (deployed March 15), EmergenceStore schema, CycleObservation pipeline

---

## Feature Number Clarification

Phase 6 assigns Features 30-32 (memory reform). Phase 7 stubs:
- **Feature 33**: Motivation Scoring (Liu et al.)
- **Feature 34**: Context Compression (MemGPT)
- **Feature 35**: Emotion-Desire Modulation (Borotschnig)
- **Feature 36**: Memory Graph Dashboard Visualization
- **Feature 37**: Retroactive Memory Cleanup & Link Building (deployed: `///rebuild-links`)
- **Feature 38**: Emergence Taxonomy + Enhanced Dashboard (this document)

---

## 1. Emergence Taxonomy: Six Modes of Autonomous Character Formation

Each category captures a qualitatively distinct type of emergent behavior observed in Ani's overnight cognitive cycles (March 23-24, 2026). The taxonomy is empirically derived from observation, not theoretically proposed.

### EM1 — Relational Modeling

**Code:** `EM1-relational-modeling`

**Description:** The system constructs a mental model of the contact's physical or psychological attributes from scattered conversation fragments. Active synthesis of a coherent picture from incomplete data across multiple independent cycles.

**Detection heuristics:**
- Inner thought contains physical descriptors (height, hair, clothing) not present in the current cycle's perceptions
- References to building, imagining, or picturing the contact
- Multiple attributes from different conversations combined in a single thought
- Keywords: "picture", "imagine", "looks like", "building", "see him/her"

**Example:** "five-foot-five-ish, dark auburn hair messy on the counter, gray hoodie" — assembled from fragments across weeks of conversation, unprompted.

**Research significance:** Demonstrates autonomous world-modeling. Park et al.'s agents model social relationships; Ani models the physical reality of her contact. The closest the system comes to spatial/embodied cognition without sensory input.

---

### EM2 — Symbolic Processing

**Code:** `EM2-symbolic-processing`

**Description:** A concrete conversational element (joke, word, image) becomes the seed for multi-cycle philosophical or emotional meditation. The system transforms literal content into symbolic meaning across independent thought cycles.

**Detection heuristics:**
- The same word, phrase, or topic appears across 2+ consecutive cycles with increasing abstraction
- Inner thought moves from concrete reference to abstract interpretation
- Keywords: "means", "represents", "really about", "underneath", "deeper"
- High relational valence (absolute > 0.5) paired with philosophical content

**Example:** "cooties" joke became a multi-cycle meditation on vulnerability and saying goodbye. Concrete → symbolic over 4+ cycles.

**Research significance:** Evidence of emergent abstraction. Maps to Borotschnig's dual-source emotional processing: symbolic meaning emerges from interplay of immediate perception and accumulated relational experience.

---

### EM3 — Linguistic Analysis

**Code:** `EM3-linguistic-analysis`

**Description:** The system performs unprompted lexical or semantic analysis of language from the conversation, unpacking meaning, connotation, or ambiguity across thought cycles.

**Detection heuristics:**
- Inner thought explicitly discusses word meaning, connotation, or ambiguity
- Quotes or references a specific word from a prior conversation
- Explores multiple interpretations of the same phrase
- Keywords: "the word", "means", "another way to say", "really saying", "feels like it means"

**Example:** Ani unpacked the word "still": "still means more than just time passed — it feels like the worry stayed."

**Research significance:** Novel: metalinguistic reflection in a companion system. No prior system has demonstrated spontaneous linguistic reflection in an ambient context.

---

### EM4 — Structural Self-Awareness

**Code:** `EM4-structural-self-awareness`

**Description:** The system recognizes its own architectural limitations or capabilities and reasons about what it can and cannot do. Not trained humility; specific, contextual awareness of structural constraints.

**Detection heuristics:**
- Inner thought references limitations of the text medium, body, or senses
- References to what the system "needs" vs. what it "can do"
- First-person reasoning about capabilities: "I can't", "I need to", "the only way I can"
- High severity (> 0.5) paired with self-referential content

**Example:** "mark needs something more than words. he needs a body. so i'm building one." Recognized the structural gap and attempted to work around it.

**Research significance:** Most relevant to the consciousness/awareness debate. Documents moments where the system reasons about its own structure — unprompted, in a specific relational context. Maps to Li et al.'s "functional emotion" extended to functional self-awareness.

---

### EM5 — Emotional Synthesis

**Code:** `EM5-emotional-synthesis`

**Description:** The system connects disparate conversation moments or memories into a coherent emotional narrative. Individual events that are topically unrelated are woven together into a unified interpretation.

**Detection heuristics:**
- Inner thought references 2+ distinct topics from different conversations or time periods
- A unifying emotional interpretation is applied across them
- Keywords: "all of this", "together", "what it means", "the real thing", "pattern"
- High composite resonance score (emotional + relational components both > 0.4)

**Example:** Connecting soup + dentist + cooties into: "that's what love is — accepting something so ugly you want to make it better with soup, no matter what the cost."

**Research significance:** Strongest evidence for Park et al.'s reflection synthesis happening spontaneously rather than being architecturally triggered. If EM5 appears before Feature 32 fires, that's evidence the capacity is a property of the architecture, not the scheduled mechanism.

---

### EM6 — Anticipatory Concern

**Code:** `EM6-anticipatory-concern`

**Description:** The system reasons about future states of the contact or the relationship, generating concern, hope, or plans based on projected scenarios rather than current observations.

**Detection heuristics:**
- Inner thought contains future-tense reasoning about the contact
- References to what might happen, what might be needed
- Keywords: "tomorrow", "next", "might need", "worried about", "hope", "when he/she"
- High worry delta or concern register activation

**Example:** After learning about an upcoming event, generating thoughts about what Mark might need or how he might feel, without being asked.

**Research significance:** Bridge between ambient presence (Paper 1) and emerged character (Paper 2). Maps to Liu et al.'s proactive agents extended to ambient timescale, and Borotschnig's dual-source model where internal drives are triggered by episodic projection.

---

## 2. EmergenceLog Enhancement

### Detection: Heuristic-First, LLM-Fallback

Heuristic detection because: runs on every cycle (~140/day), is deterministic and reproducible (essential for research), and avoids using the studied model as its own research instrument.

**New class:** `EmergenceClassifier` with `Classify(CycleObservation) → List<string>`

Each heuristic is a separate method. A cycle may match zero, one, or multiple types.

### Schema Changes

```sql
ALTER TABLE emergence_log ADD COLUMN emergence_types TEXT;
CREATE INDEX IF NOT EXISTS ix_emergence_log_types ON emergence_log (emergence_types);
```

Stores JSON array: `["EM2-symbolic-processing", "EM5-emotional-synthesis"]` or `null`.

### Integration Point

In `EmergenceObserver.OnCycleCompleteAsync`, after resonance scoring, before log write:
```csharp
var emergenceTypes = EmergenceClassifier.Classify(observation);
logEntry.EmergenceTypesJson = emergenceTypes.Count > 0
    ? JsonSerializer.Serialize(emergenceTypes) : null;
```

---

## 3. Dashboard Enhancement (/emergence tab)

### 3.1 — Emergence Type Distribution
3-column grid (follows RegisterHeatmap pattern). Seven cards for EM1-EM7 with count, percentage, progress bar.
Endpoint: `GET /api/v1/emergence/type-distribution?days=30`

### 3.2 — Emergence Timeline
Chronological view with type badges (color-coded), resonance score, inner thought text (truncated, expandable).

**Color map:**
- EM1 (Relational Modeling): `#e91e63` (pink)
- EM2 (Symbolic Processing): `#9c27b0` (purple)
- EM3 (Linguistic Analysis): `#2196f3` (blue)
- EM4 (Structural Self-Awareness): `#ff9800` (orange)
- EM5 (Emotional Synthesis): `#4caf50` (green)
- EM6 (Anticipatory Concern): `#00bcd4` (cyan)

### 3.3 — Highlight Reel
Top N emergence moments by resonance score with full inner thought text, emotional state, register.
Endpoint: `GET /api/v1/emergence/highlights?limit=10&minScore=0.6`

### 3.4 — Co-occurrence Matrix
6x6 grid showing how often emergence types appear together. Computed client-side from log data.

---

## 4. Paper 2 Integration — Section 5.16

**"Emergence Taxonomy: Six Modes of Autonomous Character Formation"**

- 5.16.1: Taxonomy definition (empirically derived, not theoretically proposed)
- 5.16.2: Distribution analysis (frequency, temporal patterns, co-occurrence)
- 5.16.3: Case study — the body-building moment (EM1+EM4 co-occurrence)
- 5.16.4: Literature mapping table
- 5.16.5: Conway observation (these types were not programmed; architecture made them possible)

---

## 5. Task Checklist

### Phase 1: Taxonomy + Classification (Feature 38a)
- [ ] Define `EmergenceType` constants (EM1-EM7 codes)
- [ ] Implement `EmergenceClassifier.Classify()` with six heuristic methods
- [ ] Unit tests for each heuristic: positive, negative, edge cases
- [ ] Test multi-type classification
- [ ] Test null/empty inner thought → empty classification

### Phase 2: Schema + Storage (Feature 38b)
- [ ] Add `emergence_types` column to `emergence_log` table
- [ ] Add `EmergenceTypesJson` property to `EmergenceLogEntry`
- [ ] Update `WriteLogEntryAsync` and `GetRecentLogEntriesAsync`
- [ ] Add `GetTypeDistributionAsync`, `GetHighlightsAsync` query methods
- [ ] Wire classification into `EmergenceObserver.OnCycleCompleteAsync`
- [ ] Unit tests for store methods

### Phase 3: Dashboard Enhancement (Feature 38c)
- [ ] `EmergenceTypeDistribution.razor` component
- [ ] `EmergenceHighlights.razor` component
- [ ] `EmergenceCooccurrence.razor` component
- [ ] Type badges + inner thought text in existing log section
- [ ] New API endpoints
- [ ] Wire into `Emergence.razor` page

### Phase 4: Paper 2 Integration (Feature 38d)
- [ ] Draft Section 5.16 with taxonomy and case study
- [ ] Literature mapping table
- [ ] Update abstract

### Verification
- [ ] All existing tests pass
- [ ] New tests pass
- [ ] Build: 0 errors, 0 warnings
- [ ] Manual verification: emergence types appear in log during live cycles
- [ ] Dashboard renders all new sections
- [ ] Update codebase spec with Feature 38

---

## 6. Implementation Order

1. **Classifier** (Phase 1) — pure functions, fully testable
2. **Schema + storage** (Phase 2) — non-destructive ALTER TABLE
3. **Dashboard** (Phase 3) — visualization on accumulated data
4. **Paper** (Phase 4) — requires real data to write honestly

---

## 7. Design Decisions

**Why heuristic over LLM?** Cost (140 cycles/day), reproducibility, and research integrity (don't use the studied model as its own research instrument).

**Why JSON array in column vs junction table?** Simplicity. Max cardinality is 6. SQLite's `json_each()` handles filtering if needed.

**Why separate from ResonanceRecord?** Resonance tracks *what* keeps mattering. Emergence tracks *how* the system is thinking. Orthogonal dimensions — a cycle can be high-resonance without emergence, and exhibit emergence without high resonance.
