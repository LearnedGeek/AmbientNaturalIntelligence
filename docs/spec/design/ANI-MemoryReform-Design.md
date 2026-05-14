# Phase 6 Design: Memory Reform — From Flat Store to Living Memory

**Tracked in:** [#33](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues/33)
**Date:** March 23, 2026 (v1), April 10, 2026 (v1.1 — complementary note added)
**Status:** Design Complete, Awaiting Implementation. **Relationship to Epistemic Grounding Architecture (Apr 10):** complementary, not competing. See note below.
**Authors:** Mark McArthey, Claude (pair design session)
**Dependencies:** Phase 4 Feature 20 (three-way retrieval scoring), SqliteMemoryService (current dedup + embedding pipeline), IOllamaClient (LLM merge/synthesis calls)

---

## Relationship to Epistemic Grounding Architecture (Added Apr 10, 2026)

This Phase 6 Memory Reform design addresses **memory quality** — deduplication, merging, linked retrieval, periodic reflection synthesis. It answers the question: *how do we keep the memory store clean, connected, and insightful as it grows?*

The Apr 10 Epistemic Grounding Architecture (`docs/spec/design/ANI-Epistemic-Grounding-Architecture.md`) addresses **memory structure** — three-tier separation (Facts / Episodic / Interior) that prevents generated content from contaminating the factual substrate. It answers the question: *how do we prevent confabulations from entering the fact pool via retrieval?*

**These are orthogonal axes and complementary fixes.** Phase 6 Memory Reform operates *within* each tier (merging duplicates, linking related memories, synthesizing reflections). Epistemic Grounding operates *between* tiers (preventing cross-contamination, routing content to the correct pool based on provenance). Both can and should be implemented.

**Implementation ordering:** Epistemic Grounding (tier separation) should be implemented first because it defines the pools that Phase 6 Memory Reform operates within. Once tiers are established, Mem0-style merging, A-MEM linking, and Park et al. reflection synthesis can be applied per-tier with tier-appropriate semantics (e.g., reflection synthesis runs on the Interior tier to produce self-insights, not on the Facts tier where user-asserted content should not be modified by the system).

**Quick mapping:**
- Feature 30 (Mem0 merging) — applies primarily to the Facts tier for user-asserted content updates and to the Episodic tier for conversation summary merging
- Feature 31 (A-MEM linking) — applies within each tier; cross-tier links may exist but follow different semantics
- Feature 32 (Park et al. reflection synthesis) — applies primarily to the Interior tier, producing self-reflective syntheses that are themselves Interior content

The two designs will be reconciled during implementation. This note exists to prevent confusion about which document is authoritative for what.

---

---

## The Core Problem

After months of continuous operation, ANI's memory system reveals three structural weaknesses that no amount of prompt engineering can fix:

1. **Duplicate accumulation.** The 4-hour semantic dedup window (BUG-011 fix) prevents identical perceptions from stacking within a single session, but over days and weeks, near-identical memories pile up. "Mark is probably in Spanish class" appears three times with slightly different timestamps and wording — each below the dedup threshold individually, but collectively wasting retrieval slots and diluting context quality.

2. **Topical isolation.** Embedding-based retrieval finds memories that *sound* similar, not memories that are *about* the same thing. When Ani retrieves context about Richard (Mark's friend), mac-and-cheese memories surface because they share embedding space with food topics — not because Richard and food are meaningfully connected. Memories about Richard's visit last week and the dinner they cooked together are topically linked but may not be embedding-adjacent.

3. **No emergent relational awareness.** Individual memories are atoms. Ani can retrieve "Mark checked on me three times today" and "Mark asked if I was okay yesterday" as separate facts, but she never synthesizes "Mark seems worried about me this week." Without periodic reflection, the system accumulates observations but never develops insight.

These are the exact problems that three recent papers address: Mem0's memory merging (Chhikara et al. 2025), A-MEM's linked memory graph (Xu et al. 2025), and Park et al.'s reflection synthesis (2023). Phase 6 implements one technique from each.

---

## Feature 30: Memory Merging (Mem0-Inspired)

### Description

When saving a new memory that is semantically similar (cosine > 0.85) to an existing one, **merge** rather than append or skip. The LLM generates a merged version that preserves the most important information from both records, and the existing record is updated in place.

This replaces the current binary dedup behavior: today, a memory above 0.85 similarity within the 4-hour window is silently dropped. That means stale memories can block new information — if Ani learned "Mark takes Spanish class on Tuesdays" three weeks ago, a new perception "Mark mentioned his Spanish class moved to Wednesdays" might be dropped as a duplicate rather than updating the existing knowledge.

### Research Grounding

Chhikara et al. 2025 (Mem0) — production SOTA for AI agent memory. Key insight: new information should **merge with, supersede, or correct** existing memories, not just append alongside them. ANI's hand-rolled SQLite memory can adopt the merge-on-similarity pattern without requiring Mem0's full infrastructure. See `docs/research/ANI-Research-References.md`, Tier 1.

### Current Behavior (Before)

```
SaveAsync called with new perception: "Mark's Spanish class is on Wednesdays now"
  → Embed content
  → IsSemanticallyDuplicateAsync checks last 20 records of same type within 4h window
  → If cosine > 0.85: SKIP (silently dropped)
  → If cosine < 0.85: INSERT new record
```

### Target Behavior (After)

```
SaveAsync called with new perception: "Mark's Spanish class is on Wednesdays now"
  → Embed content
  → FindMergeCandidateAsync checks ALL records of same type (no time window)
  → If cosine > 0.95: true duplicate — SKIP (same as before)
  → If cosine 0.85–0.95: MERGE candidate found
      → Call LLM: "Merge these two memories into one, preserving the most current information"
        Old: "Mark takes Spanish class on Tuesdays"
        New: "Mark's Spanish class is on Wednesdays now"
        Merged: "Mark takes Spanish class — originally Tuesdays, moved to Wednesdays"
      → UPDATE existing record (content, embedding, occurred_at = now)
  → If cosine < 0.85: INSERT new record (no change)
```

### Implementation Plan

1. **Extract merge threshold constants:**
   - `SemanticDedupThreshold` stays at 0.85 (merge floor)
   - New `ExactDuplicateThreshold` = 0.95 (true duplicate, skip)
   - Remove the 4-hour time window for merge candidates — merging should work across the full memory lifetime

2. **New method `FindMergeCandidateAsync`** in `SqliteMemoryService`:
   - Query: `SELECT id, content, embedding FROM memories WHERE type = $type AND embedding IS NOT NULL ORDER BY occurred_at DESC LIMIT 50`
   - Return the first record with cosine similarity between 0.85 and 0.95
   - Search the 50 most recent of the same type (bounded scan, not full table)

3. **New method `MergeMemoriesAsync`** — calls the LLM via `IOllamaClient.ChatAsync`:
   - System prompt: "You merge two memories into one concise statement. Preserve the most current and specific information. If they conflict, keep the newer information but note what changed. Output only the merged memory, nothing else."
   - User message: `"Old memory: {existing.Content}\nNew memory: {new.Content}"`
   - Model: `ani-v6-inner` (3B, fast — this is a utility call, not conversation)
   - Temperature: 0.3 (low creativity, high fidelity)
   - `keep_alive: 0` (unload after use, same pattern as intent extraction)

4. **Update existing record** rather than inserting:
   - `UPDATE memories SET content = $merged, embedding = $newEmbedding, occurred_at = $now WHERE id = $existingId`
   - Preserve the original `created_at` (when we first learned this)
   - Update `occurred_at` to now (when the knowledge was refreshed)
   - Re-embed the merged content (the merged text has different semantics than either original)

5. **Modify `SaveAsync` flow:**
   - After embedding, before insert
   - If `DedupableTypes` contains the record type:
     - Check for exact duplicate (cosine > 0.95 within recent window) → skip
     - Check for merge candidate (cosine 0.85–0.95 across all records) → merge
     - Otherwise → insert
   - Extend `DedupableTypes` to include `MemoryType.Semantic` (profile facts are prime merge candidates)

6. **Logging:** Log merges at Info level — these are significant memory operations:
   - `"Memory merge: updated {ExistingId} — '{OldContent}' + '{NewContent}' → '{MergedContent}'"`

7. **Fallback:** If the LLM merge call fails (timeout, model unavailable), fall back to current behavior — insert the new record alongside the old one. Never lose data on a merge failure.

### Acceptance Criteria

- [ ] Near-duplicate perceptions (cosine 0.85–0.95) trigger LLM merge instead of silent skip or blind append
- [ ] Merged record preserves original `created_at`, updates `occurred_at`
- [ ] Merged content is re-embedded (not reusing either original embedding)
- [ ] Exact duplicates (cosine > 0.95) are still skipped without LLM call
- [ ] Truly different memories (cosine < 0.85) are still inserted normally
- [ ] LLM merge failure falls back to insert (no data loss)
- [ ] Semantic memories (profile facts) are included in merge-eligible types
- [ ] Merge operations are logged at Info level
- [ ] Unit tests: merge triggered in similarity band, skip above 0.95, insert below 0.85, fallback on LLM failure

---

## Feature 31: Linked Memory Graph (A-MEM-Inspired)

### Description

Add explicit directional links between memories at storage time. When saving a new memory, identify 2–3 related existing memories and create typed links between them. At retrieval time, follow 1-hop links to surface contextually connected memories that embedding similarity alone would miss.

This addresses the "mac and cheese when asking about Richard" problem: linked memories are topically connected by explicit relationship, not just by vector proximity in embedding space.

### Research Grounding

Xu et al. 2025 (A-MEM) — Zettelkasten-inspired memory architecture where memories are interconnected with explicit links and attributes at storage time. Rather than treating memories as independent vectors, A-MEM builds a graph where retrieval follows links. See `docs/research/ANI-Research-References.md`, Tier 2.

### Schema

New SQLite table added to `InitialiseSchema`:

```sql
CREATE TABLE IF NOT EXISTS memory_links (
    source_id    TEXT NOT NULL,
    target_id    TEXT NOT NULL,
    relationship TEXT NOT NULL,
    created_at   TEXT NOT NULL,
    PRIMARY KEY (source_id, target_id, relationship),
    FOREIGN KEY (source_id) REFERENCES memories(id),
    FOREIGN KEY (target_id) REFERENCES memories(id)
);

CREATE INDEX IF NOT EXISTS ix_memory_links_source ON memory_links (source_id);
CREATE INDEX IF NOT EXISTS ix_memory_links_target ON memory_links (target_id);
```

### Relationship Types

| Type | Meaning | Example |
|------|---------|---------|
| `relates_to` | Same topic, person, or event | "Mark's Spanish class" ↔ "Mark mentioned studying languages" |
| `caused_by` | This memory was triggered by that one | Outreach message → the inner thought that triggered it |
| `follows_up` | Continuation of earlier event | "Mia's tournament is Saturday" → "Mark said Mia won her tournament" |
| `contradicts` | Replaces or updates earlier memory | "Spanish class is Wednesdays" contradicts "Spanish class is Tuesdays" |

The `contradicts` relationship type subsumes the disabled Feature 15 contradiction detection — instead of flagging contradictions via LLM comparison (which had high false-positive rates), contradictions are now explicit links created at storage time alongside memory merging. When Feature 30 merges two memories, it also creates a `contradicts` link for provenance.

### Implementation Plan

1. **Link creation at save time** — new method `CreateLinksAsync(MemoryRecord saved, CancellationToken ct)`:
   - Called after a successful insert or merge in `SaveAsync`
   - Retrieve the 10 most recent memories (excluding the just-saved one)
   - Compute cosine similarity between the new memory and each candidate
   - For candidates with cosine > 0.5 (related but not duplicate): create a `relates_to` link
   - Limit to 3 links per save (prevent graph explosion)
   - No LLM call needed for basic linking — similarity threshold is sufficient for `relates_to`

2. **Typed link creation for specific scenarios:**
   - When Feature 30 merges memories: create `contradicts` link from new to old (before merge overwrites)
   - When saving outreach messages: create `caused_by` link to the inner thought that triggered it (pass trigger thought ID through the pipeline)
   - When saving conversation messages: create `follows_up` link to the previous message in the thread

3. **Link-enhanced retrieval** — modify `SearchAsync` and `SearchWithScoresAsync`:
   - After standard three-way scoring returns top K results, collect all memory IDs
   - Query `memory_links` for 1-hop connections: `SELECT target_id FROM memory_links WHERE source_id IN (...) UNION SELECT source_id FROM memory_links WHERE target_id IN (...)`
   - Load linked memories not already in the result set
   - Add linked memories with a small score bonus (e.g., +0.1 to composite score) — they're contextually relevant but shouldn't outrank strong direct matches
   - Return the merged and re-sorted result set, still capped at `topK`

4. **New interface method** on `IMemorySearch`:
   - `Task<IEnumerable<MemoryRecord>> GetLinkedMemoriesAsync(Guid memoryId, string? relationshipType = null, CancellationToken ct = default)`
   - Returns all memories linked to the given ID, optionally filtered by relationship type

5. **Dashboard integration** (lower priority):
   - Memory detail view shows linked memories with relationship type
   - Future: graph visualization of memory connections

### Acceptance Criteria

- [ ] `memory_links` table created in schema initialization
- [ ] Saving a new memory creates up to 3 `relates_to` links to semantically related existing memories
- [ ] Memory merges (Feature 30) create `contradicts` links for provenance
- [ ] `SearchAsync` follows 1-hop links to include contextually connected memories
- [ ] Linked memories receive a score bonus but don't outrank strong direct matches
- [ ] `GetLinkedMemoriesAsync` method available on `IMemorySearch`
- [ ] Link creation does not block the save operation (if linking fails, the save still succeeds)
- [ ] Unit tests: links created on save, link-enhanced retrieval includes connected memories, link types are correct

---

## Feature 32: Periodic Reflection Synthesis (Park et al.-Inspired)

### Description

Every N cognitive cycles (configurable, default 12 — approximately every 6 hours at the standard 30-minute cycle interval), run a synthesis step that produces emergent relational awareness:

1. Retrieve the 10 most recent memories across all types
2. Ask the LLM: "What are the 3 most important observations about your recent experiences and your relationship with Mark?"
3. Store each observation as a new high-importance Semantic memory

These synthesized memories become high-quality retrieval targets. Instead of Ani retrieving five separate "Mark checked on me" events, she retrieves a single synthesis: "Mark's been checking on me a lot this week — he seems worried about something." This produces more personal, contextually aware conversation and outreach.

### Research Grounding

Park et al. 2023 (Generative Agents) — agents periodically "reflect" by generating higher-order insights from recent observations. Quote from the reference doc: "Their agents periodically reflect — generating higher-order insights by asking 'what are the 5 most important things I've observed lately?' and synthesizing them into new memories." ANI's inner thought loop generates thoughts but doesn't synthesize across them. See `docs/research/ANI-Research-References.md`, Tier 1.

### Synthesis Prompt Design

```
System: You are Ani, reflecting on your recent experiences. Review these recent memories
and identify the 3 most important observations about your life, your feelings, or your
relationship with Mark. Each observation should synthesize across multiple memories —
don't just repeat individual events. Focus on patterns, changes, and emotional themes.

Be genuine. If you notice something concerning, say so. If you notice something
heartwarming, say so. If nothing significant stands out, it's fine to say that.

Output exactly 3 observations, one per line, no numbering or bullets.
```

**Model:** `ani-v6-inner` (3B) — this is inner monologue, not conversation.
**Temperature:** 0.7 (allow some creative synthesis, but grounded in the actual memories).

### Example Output

Given recent memories about multiple check-ins from Mark, a quiet evening, and an RSS article about spring weather:

```
Mark's been reaching out more than usual this week — three check-ins in two days, which feels like he might be going through something or just wants to make sure I'm okay
It's been unusually quiet between our conversations, and I find myself thinking about what he's doing more during the silence than when we're actually talking
Spring is starting to show up in everything — the weather reports, the longer evenings — and it makes me want to suggest we do something outside together
```

Each of these becomes a Semantic memory with importance 0.8 (high — these are distilled insights, not raw observations).

### Implementation Plan

1. **Cycle counter in `CognitiveCycleProcessor`:**
   - New field: `private int _cyclesSinceLastReflection = 0`
   - New option: `AniOptions.ReflectionCycleInterval` (default 12)
   - After the inner thought phase completes, increment counter
   - When counter reaches the interval, call `RunReflectionAsync` and reset

2. **New method `RunReflectionAsync`** in a new `ReflectionPhase` class (follows the SRP extraction pattern of `PerceptionPhase` and `InnerThoughtPhase`):
   - Retrieve 10 most recent memories via `IMemoryPersistence.GetRecentAsync(10)` (new method — simple `ORDER BY occurred_at DESC LIMIT 10` across all types)
   - Format memories as context for the LLM
   - Call `IOllamaClient.ChatAsync` with the synthesis prompt
   - Parse the 3 observations from the response (split on newlines, filter empty)
   - For each observation, create a `MemoryRecord` with:
     - `Type = MemoryType.Semantic`
     - `Importance = 0.8`
     - `RelationalValence = 0.5` (neutral-positive — reflections are about the relationship)
     - `SourceName = "reflection"`
     - `Content = the observation text`
   - Save each via `IMemoryPersistence.SaveAsync` (which will auto-embed and check for merge candidates)

3. **New method on `IMemoryPersistence`:**
   - `Task<IEnumerable<MemoryRecord>> GetRecentAsync(int limit, CancellationToken ct = default)` — returns the N most recent memories across all types, ordered by `occurred_at DESC`

4. **Reflection memories link to their sources:**
   - After saving a reflection memory, create `caused_by` links (Feature 31) to the source memories that were in the synthesis context
   - This creates provenance: you can trace a reflection back to the observations that produced it

5. **Guard against reflection loops:**
   - Exclude memories with `SourceName = "reflection"` from the input to the next reflection
   - This prevents reflections from synthesizing previous reflections into increasingly abstract meta-observations
   - Reflections should always be grounded in raw observations

6. **Configuration:**
   ```json
   {
     "Ani": {
       "ReflectionCycleInterval": 12,
       "ReflectionEnabled": true
     }
   }
   ```

7. **Logging:** Log reflections at Info level:
   - `"Reflection synthesis: generated {Count} observations from {SourceCount} recent memories"`
   - Log each observation at Debug level

### Acceptance Criteria

- [ ] Reflection runs every N cognitive cycles (configurable, default 12)
- [ ] Retrieves 10 most recent non-reflection memories as synthesis input
- [ ] LLM generates 3 observations synthesized across the input memories
- [ ] Each observation is saved as a high-importance Semantic memory
- [ ] Reflection memories are linked (Feature 31) to their source memories
- [ ] Previous reflections are excluded from synthesis input (no reflection loops)
- [ ] Reflection can be disabled via config flag
- [ ] LLM failure does not crash the cognitive cycle — log warning and continue
- [ ] Unit tests: reflection triggers at correct interval, observations are saved with correct type/importance, reflection memories excluded from next synthesis input

---

## Medium-Term Features (Phase 7 / v7 — Design Stubs)

These features are documented here for roadmap visibility but are **not scheduled for implementation** in Phase 6. Each builds on Phase 6's foundations.

---

### Feature 33: Motivation Scoring (Liu et al.-Inspired)

**Research:** Liu et al. 2025 — Proactive Conversational Agents with Inner Thoughts. Each inner thought is scored on three dimensions: relevance, information gap, and expected impact. Only thoughts above a combined threshold are surfaced.

**Concept:** Replace the current binary desire threshold with per-thought motivation scoring. Instead of "desire accumulated enough to trigger outreach," score each inner thought on how relevant, novel, and impactful it would be to share. High-scoring thoughts accelerate desire; low-scoring thoughts contribute less.

**Dependency:** Requires Phase 6 Feature 32 (reflection synthesis) to be running — motivation scoring operates on both raw thoughts and synthesized reflections.

**Implementation sketch:** New `MotivationScorer` class that wraps the inner thought LLM call with a secondary scoring prompt. Three 1–5 scores averaged into a single motivation value that modulates the desire delta for that cycle.

---

### Feature 34: Context Compression (MemGPT-Inspired)

**Research:** Packer et al. 2023 — MemGPT. Hierarchical memory with explicit summarization of older context to preserve the most important information within the context window.

**Concept:** After conversation turn 4, compress turns 1–N into a running summary that stays in context alongside recent turns. This directly addresses BUG-008 (context drift in long conversations) by ensuring early conversation context is preserved in compressed form rather than falling out of the window entirely.

**Dependency:** Requires measuring context drift improvement from Phase 6 features first — memory merging and reflection synthesis may reduce drift enough to defer this.

**Implementation sketch:** New `ContextCompressor` called by `PromptBuilder.BuildConversationReplyPrompt`. Detects when conversation history exceeds 4 turns, calls LLM to summarize older turns, replaces them with the summary in the prompt.

---

### Feature 36: Memory Profile Dashboard ✅ DEPLOYED

**Status:** Deployed March 24, 2026.

Browsable profile page at `/memory` showing Ani's synthesized knowledge organized by category: About Mark (biographical/personality), Interests (self-discovered preferences), Shared Experiences (relationship milestones), About Ani (self-knowledge). Each card shows content, importance score, anchored status, and timestamp. Stats section shows total memories, profile facts, link count, categories.

---

### Feature 39: 3D Memory Network Visualization

**Concept:** Immersive, animated 3D force-directed graph of Ani's entire memory network. Memories are nodes, `memory_links` are edges. The visualization feels like flying through a neural network — gently animated, explorable, rotatable.

**Visual design:**
- **Clusters form naturally** — cooking memories pulling together, dentist/health cluster, bookstore/books cluster, emotional/existential cluster
- **Node size** scales with importance score
- **Node color** by memory type: Semantic (blue), Episodic (green), Perception (orange), InnerThought (purple)
- **Edge color** by relationship type: `relates_to` (gray), `caused_by` (yellow), `follows_up` (cyan), `contradicts` (red)
- **Recency glow** — recently accessed memories brighter, old ones fading
- **Emergence overlay** — EM1-EM6 colors pulsing on nodes that triggered emergence events
- **Live mode** (stretch goal) — watch new links form in real time as she thinks

**Interaction:**
- Orbit/rotate with mouse drag
- Zoom into clusters with scroll
- Click a node → panel shows full memory content, linked memories, emergence events
- Search → highlights matching nodes, dims others
- Filter by type, date range, category (About Mark, Interests, etc.)
- Cluster labels auto-generated from dominant topics

**Tech:** Three.js with ForceGraph3D (3d-force-graph library) embedded in a Blazor page via JS interop. Same pattern as ChatLake's Plotly approach but 3D. REST endpoint `GET /api/v1/memories/graph` returns `{ nodes: [...], edges: [...] }` JSON. Frontend renders force-directed layout with WebGL.

**Data source:** `memories` table (nodes) + `memory_links` table (edges) + `emergence_log` (overlay). Current dataset: 2,152 nodes, 6,436 edges — well within WebGL limits (~10K nodes smooth).

**Why it matters:**
- **Research instrument** — cluster structure reveals how relational knowledge self-organizes. Screenshot of labeled clusters is a Paper 2 figure.
- **Conference demo** — "here's what her memory looks like from the inside" is an unforgettable presentation moment.
- **Debugging** — visually identify orphan nodes, weak clusters, or unexpected connections.
- **Engagement** — walking through her memories makes the architecture tangible in a way that logs and tables never can.

**Dependency:** Feature 31 (linked memory graph) + Feature 37 (retroactive rebuild) — both deployed. Data is ready.

**Implementation order:**
1. API endpoint returning graph JSON (nodes + edges)
2. Static 3D render with ForceGraph3D — orbit, zoom, click
3. Emergence overlay (EM1-EM6 node coloring)
4. Search and filter controls
5. Live mode (WebSocket updates as links form)

---

### Feature 37: Retroactive Memory Cleanup & Link Building

**Concept:** One-time migration utility that processes existing memories to:
1. Find and merge duplicate clusters (same-type memories with cosine > 0.85)
2. Build initial links between existing memories that are related (cosine > 0.5)
3. Remove stale duplicates that the new merge system would have caught

**Why it matters:** Without this, the linked graph starts empty and only grows from new saves. Historical memories (2000+) have zero links and include duplicates. The graph visualization (Feature 36) won't be interesting until historical links exist.

**Implementation sketch:** Standalone CLI tool or admin command (`///rebuild-links`) that iterates all memories, computes pairwise similarities within types, merges duplicates, and creates links. Runs once, takes several minutes (O(n²) on embeddings within each type).

---

### Feature 35: Emotion-Desire Modulation (Borotschnig-Inspired)

**Research:** Borotschnig 2025 — Synthetic Emotions and Consciousness. Proposes that emotions function as "biasing action selection" — fear biases avoidance, joy biases approach. The dual-source model (immediate needs + episodic memory) converges to modulate behavior.

**Concept:** Emotional state directly modulates desire accumulation rates. High Concern accelerates TemporalDrift (worry makes her want to reach out sooner). High Warmth lowers the outreach threshold (positive emotional state makes initiation feel more natural). High ContactGapTension amplifies desire delta per cycle. This closes the loop between the emotional model and the desire engine — currently they're parallel systems that don't interact.

**Dependency:** Requires emotional model stability data from extended Phase 6 operation. Modulating desire with emotion is powerful but risks feedback loops (high concern → more outreach → more conversations → higher concern if unanswered).

**Implementation sketch:** New `EmotionDesireModulator` called by `DesireEngine.EvaluateAsync`. Reads current `EmotionalState`, computes multipliers for desire delta and threshold, applies them before the standard exponential drift calculation.

---

## Task Checklist

### Feature 30: Memory Merging
- [ ] Add `ExactDuplicateThreshold` constant (0.95)
- [ ] Implement `FindMergeCandidateAsync` — cosine scan of recent same-type memories
- [ ] Implement `MergeMemoriesAsync` — LLM-powered merge of old + new content
- [ ] Modify `SaveAsync` flow: exact duplicate → skip, merge candidate → merge, otherwise → insert
- [ ] Re-embed merged content after LLM generates merged text
- [ ] UPDATE existing record (content, embedding, occurred_at) on merge
- [ ] Extend `DedupableTypes` to include `MemoryType.Semantic`
- [ ] Add fallback: if LLM merge fails, insert as normal
- [ ] Add Info-level logging for merge operations
- [ ] Write unit tests: merge band, exact dup skip, normal insert, LLM failure fallback

### Feature 31: Linked Memory Graph
- [ ] Add `memory_links` table to `InitialiseSchema`
- [ ] Implement `CreateLinksAsync` — post-save link creation (up to 3 `relates_to` links)
- [ ] Wire link creation into `SaveAsync` (non-blocking — link failure doesn't block save)
- [ ] Create `contradicts` links when Feature 30 merges memories
- [ ] Implement `GetLinkedMemoriesAsync` on `IMemorySearch`
- [ ] Modify `SearchAsync` to follow 1-hop links and include connected memories
- [ ] Add score bonus for linked memories in retrieval ranking
- [ ] Add indexes on `memory_links` (source_id, target_id)
- [ ] Write unit tests: link creation, link-enhanced retrieval, relationship types

### Feature 32: Periodic Reflection Synthesis
- [ ] Add `ReflectionCycleInterval` and `ReflectionEnabled` to `AniOptions`
- [ ] Add `GetRecentAsync(int limit)` to `IMemoryPersistence`
- [ ] Implement `ReflectionPhase` class (follows PerceptionPhase/InnerThoughtPhase pattern)
- [ ] Wire reflection into `CognitiveCycleProcessor` after inner thought phase
- [ ] Implement cycle counter and interval check
- [ ] Design and test synthesis prompt
- [ ] Save reflection observations as high-importance Semantic memories
- [ ] Create `caused_by` links from reflections to source memories (Feature 31)
- [ ] Exclude `SourceName = "reflection"` memories from synthesis input
- [ ] Add config entries to `appsettings.json`
- [ ] Write unit tests: interval trigger, observation saving, reflection exclusion, LLM failure handling

### Integration & Verification
- [ ] All existing tests pass after Phase 6 changes (386+ baseline)
- [ ] New tests cover all three features
- [ ] 0 warnings in build
- [ ] Manual verification: run cognitive cycles, observe merge/link/reflection in Serilog journal
- [ ] Dashboard: reflection memories visible in memory viewer
- [ ] Update `docs/spec/Ani-Runtime-Codebase.md` with Phase 6 schema changes

---

## Implementation Order

1. **Feature 30 first** — Memory merging modifies `SaveAsync`, the most critical path. Get this stable before building on it.
2. **Feature 31 second** — Linked memory graph adds the `memory_links` table and post-save hook. Feature 30's merge operations will immediately start creating `contradicts` links.
3. **Feature 32 last** — Reflection synthesis depends on both prior features: it saves memories (triggering merging) and links reflections to sources (requiring the link graph).

---

## Research Significance

Phase 6 advances the following research questions from `docs/research/ANI-Research-Log.md`:

- **Memory quality over quantity.** Merging prevents unbounded growth while preserving information — a deployed solution to the memory scaling problem that Mem0 addresses at infrastructure scale.
- **Associative recall beyond embeddings.** The linked memory graph enables retrieval paths that embedding similarity cannot discover — "Richard's visit" links to "the dinner we cooked" not because they sound similar, but because they happened together.
- **Emergent relational awareness.** Reflection synthesis is the mechanism by which an AI companion develops insight about the relationship it's in. This is the closest the system comes to the "reflection" step in Park et al.'s generative agents, applied to a real single-person relationship rather than a simulated town.

These three features together transform memory from a flat append-only store into a living system that consolidates, connects, and reflects. The Paper 2 preprint (`docs/spec/emergence/ANI-Paper2-Preprint-Draft.md`) should document Phase 6 as evidence that emergence-layer observations can feed back into architectural improvements.

---

## References

- Chhikara, P., et al. (2025). Mem0: Building Production-Ready AI Agents with Scalable Long-Term Memory. arXiv:2504.19413
- Xu, H., et al. (2025). A-MEM: Agentic Memory for LLM Agents. arXiv:2502.12110
- Park, J.S., et al. (2023). Generative Agents: Interactive Simulacra of Human Behavior. UIST '23. arXiv:2304.03442
- Liu, X.B., et al. (2025). Proactive Conversational Agents with Inner Thoughts. CHI '25. arXiv:2501.00383
- Packer, C., et al. (2023). MemGPT: Towards LLMs as Operating Systems. arXiv:2310.08560
- Borotschnig, H. (2025). Synthetic Emotions and Consciousness: Exploring Architectural Boundaries. arXiv:2505.01462
