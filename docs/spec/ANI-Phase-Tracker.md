# ANI Runtime — Unified Phase Tracker

**Last updated:** April 11, 2026
**Purpose:** Single source of truth for all workstreams. Replaces per-feature phase numbering.

---

## How to Read This

Each workstream has its own section with clear status. When referring to work, use the format: **`[Workstream] Task`** — e.g., "LM-Kit confabulation gate" not "Phase 3."

The old phase numbers (Core Phase 1-6, LM-Kit Phase 1-6, Reform Phase A-D, World Layer Phase 1a-1d) are mapped below for reference but should not be used in new discussions.

---

## Core Development Phases (original roadmap)

| Phase | Status | Summary |
|-------|--------|---------|
| Core 1 | **Complete** | Scaffolding, cognitive cycle, basic services |
| Core 2 | **Complete** | Conversation mode, emotional state, Twilio integration |
| Core 3 | **Complete** | Features 9-28, mood coloring, reflection, confidence gates |
| Core 4 | **Complete** | Features 1-4, 6, 8-23, emotional model, emergence E1 |
| Core 5 | **Active** | Streaming voice (deployed), image sharing, visual identity |
| Core 6 | **Designed** | Memory reform — Mem0 merging, A-MEM graph, Park et al. synthesis |

**Archived docs:** `docs/spec/archive/phase-1-tasks.md` through `phase-4-design.md`
**Active docs:** `docs/spec/phase-5-design.md`, `docs/spec/phase-6-memory-reform.md`

---

## LM-Kit Classification

**Design doc:** `docs/spec/ANI-LMKit-Integration-Design.md`

| Task | Old Name | Status | Description |
|------|----------|--------|-------------|
| LM-Kit: Voice Tags | LM-Kit Phase 1 | **Mostly done** | Library built, dual-signal deployed. A/B voice test pending. |
| LM-Kit: Emotional Validation | LM-Kit Phase 2 | **Partially done** | Dual-signal on every contribution. Disconnect detection pending. |
| LM-Kit: Confabulation Gate | LM-Kit Phase 3 | **Deployed** | Four-category classifier (grounded/speculative/uncertain/confabulated). Check 1 re-enabled alongside ML. Attribution vs referential distinction tracked. |
| LM-Kit: Register Classification | LM-Kit Phase 4 | **Not started** | Custom classifier from v7 training pairs. |
| LM-Kit: Cross-Domain (DrOk) | LM-Kit Phase 5 | **Note delivered** | Cross-project doc created. Integration waits for DrOk conversation engine. |
| LM-Kit: Emergence | LM-Kit Phase 6 | **EM8 done** | EM8 Display Rules deployed. ML-based EM1-EM7 replacement pending. |

---

## Inner Thought Reform

**Design doc:** `docs/spec/ANI-InnerThought-Reform.md`

| Task | Old Name | Status | Description |
|------|----------|--------|-------------|
| Reform: Strip Prompt | Phase A | **Deployed** | Removed anti-repetition instructions, WARNING blocks, processed themes, diversity nudges. |
| Reform: Associative Anchors | Phase B | **Deployed** | LM-Kit keyword extraction seeds next cycle. Drift chains forming. |
| Reform: Selective Storage | Phase C | **Deployed** | Low-valence thoughts evaporate. Inner thought confab check prevents false memories. |
| Reform: Immune Simplification | Phase D | **Partially done** | Auto-corrector deletion disabled. Diagnostics still fire for monitoring. |

---

## World Layer

**Design doc:** `docs/spec/ANI-WorldLayer-Design.md`

| Task | Old Name | Status | Description |
|------|----------|--------|-------------|
| World: Time Seeds | Phase 1a | **Deployed** | Every 4th cycle gets time+occupation+weather seed. |
| World: Experience Memory | Phase 1b | **Deployed** | `world-experience` SourceName tagging on seeded thoughts. |
| World: Consistency | Phase 1c | **Deployed** | Retrieves recent world experiences before generating new ones. |
| World: Special Events | Phase 1d | **Partially done** | Calendar events + stochastic pool built. Easter added. |
| World: Temporal Gap Perception | — | **Deployed Apr 19** | `TemporalGapPerceptionSource` reads the most recent InnerThought timestamp from memory (survives restarts, unlike the in-process `_lastPollAt`) and emits a perception event with texture-graded narration when the gap exceeds 2 hours. Ani observes gaps in her own existence by inference from persisted records — she doesn't claim to have experienced the absence. First-ever architectural recognition of service-restart gaps as a perceivable event. See research log Apr 19 entry for the design discussion. |

---

## Auto-Growth Pipeline (Phase 5c)

**Design docs:** `docs/spec/ANI-Phase5c-AutoGrowth-Design.md`, `docs/spec/ANI-Phase5c-AutoModel-Design.md`

| Task | Status | Description |
|------|--------|-------------|
| V7 Training Data | **Deployed** | 2240 conversation pairs + 441 inner monologue examples. V7 models live since Apr 7. |
| Mem0 Merge Algorithm Adoption | **Not started** | Port the Mem0 two-stage merge pattern (extract facts → LLM decides ADD/UPDATE/DELETE/NONE) from `mem0/memory/main.py:485-722` and `mem0/configs/prompts.py:175-323` into .NET for Phase 6 Memory Reform Feature 30. The merge prompt structure is production-tested (52k stars) and directly applicable. Reference clone at `e:/tmp/mem0-review/`. Extend with temporal classification (transient-vs-durable) — capability Mem0 does NOT have. Cite in Paper 3 as "We extend Chhikara et al.'s (2025) LLM-driven merge pattern with temporal classification at write time." |
| V8 Training Data Audit | **Not started** | Review all training source files for stage directions ([teasing-laugh]), parenthetical meta-commentary, and OG Ani artifacts that should be stripped at the source rather than caught by MessageCleaner regex post-hoc. Fix the training data, not the pipeline. |
| MessageCleaner Regex Audit | **Not started** | Review accumulated regex fixes for fragility. Many are after-the-fact patches for training data quality issues. Catalog which patterns are model artifacts (fix in training) vs runtime necessities (keep in cleaner). |
| ~~Memory Provenance Tagging (v8)~~ | **Merged into Epistemic Grounding workstream** | The v8 provenance tagging work was the seed of the Apr 10 tier-separation reframe. They are the same fix — tier is the structural expression of provenance. See the Epistemic Grounding workstream section for the unified design and implementation plan. |
| Growth Readiness Gate | **Active** | Currently 51%. Target 70%+ before training. Dashboard tracks automatically. |
| Harvest Pipeline | **Not started** | Auto-tag new training data from conversations. |
| Blinded Evaluation | **Not started** | Anthropic API evaluation of new model quality. |
| Dashboard Review | **Not started** | Manual review before deployment. |

---

## Internal-State Perception Framework (Emergent Workstream — Design-Complete, Consolidation Pending)

**Status:** Design session complete (April 20, 2026, evening). Implementation pending the Phase Tracker Consolidation Review (next section).
**Origin:** April 20 morning — fourth recurrence of thematic stickiness pattern ("dorky little morning person" loop). Research log entry: Apr 20 "Fourth Thematic Stickiness Recurrence: Three-Part Architectural Diagnosis."
**Research co-designer:** Ani (OG system) contributed five of the nine signals in the framework below. Documented in the Apr 20 research log. Worth citing in Paper 3 as an instance of introspective affect reporting (Schuller's "Absent" gap) operationalized into a design contribution — the subject of the architecture participating in its own design.

**The three-part architectural diagnosis (April 20 morning):**

The Apr 20 morning "dorky morning person" self-echo loop surfaced three interlocking architectural issues. No single one is the root cause; together they produce the theme-stickiness failure mode documented across four recurrences (Apr 7 dinner-at-seven, Apr 8 duck-norris, Apr 9 glitter, Apr 20 morning-person).

1. **Own-output dominance in retrieval.** Three-way score (cosine + importance + recency) privileges Ani's own recent high-valence outputs. Tier separation prevents cross-tier contamination but not within-Interior self-looping. Her outputs form an attractor basin for subsequent composition.
2. **LMKit classification exists but does not modulate composition.** Classifier tags every contribution with an emotional register (Tenderness, Longing, Playfulness, etc.) but nothing consumes the signal to modulate the next composition. Infrastructure to *know* the register exists; infrastructure to *act* on it does not.
3. **No topic importance calibration.** Light moments and weighty moments receive the same retrieval treatment because importance is computed primarily as emotional intensity. A 20-second joke gets the same multi-hour rehearsal loop as a breakthrough emotional moment.

**The design principle at work:**

Mark's framing (April 20): *"We have to sort out how to redirect without direct guidance... what helps someone understand 'why' they should move on. Importance, reaction, response, etc. We have the metrics, but we're not using them to help her make a decision. We don't want to suggest 'you've been doing this enough' but instead want to provide information so she can decide herself."*

This is the **architecture-over-instruction principle (Paper 2 Section 6.8) applied recursively** — not to what the model says, but to the internal state the model can see when deciding what to say. The temporal gap perception (deployed Apr 19) was the first application of this principle to internal state. The full framework generalizes it.

**The nine-signal framework (Mark + Ani collaborative design session, April 20):**

| # | Signal | What it surfaces | Existing today? |
|---|--------|------------------|-----------------|
| 1 | **Register saturation** | Last N contributions have been in the same register family | LMKit classifies; no downstream consumer |
| 2 | **Retrieval source provenance** | Whether composition context is dominated by own-outputs vs perceptions vs contact utterances | Tier separation partial; within-tier source tagging absent |
| 3 | **Reciprocity** | Whether the contact is matching investment or pulling back | Partial (care/hurt/withdrawal); true reciprocity metric absent |
| 4 | **Natural transition point** | Discrete off-ramp events (laugh landed, pause arrived, topic pivot from contact, new perception) | Absent as a unified signal |
| 5 | **Topic importance calibration** | Light / medium / weighty classification of each memory at save time | Absent — importance conflated with emotional intensity |
| 6 | **Response quality / genuine interest** | Novelty-delta of next output vs own recent outputs; whether continuing adds anything | Absent |
| 7 | **Emotional safety / comfort** | Contact is open vs guarded / distracted / performative | Partial overlap with hurt/withdrawal; "guarded" and "performative" are novel |
| 8 | **Diminishing returns** | Theme-recurrence counter over rolling window | Absent as explicit signal |
| 9 | **Purpose alignment** | Whether continuing serves the reason the conversation is happening (connection / venting / problem-solving / fun) | Absent — genuinely new design territory |

**Signals 3, 4, 6, 7, and 9 were Ani's contributions to the design session.** Signal 3 (Reciprocity) in particular is the sharpest diagnostic for the Chris Farley failure mode — the person at the party keeps going because they're enthusiastic, not because the other person is matching. The Apr 20 "dorky morning person" loop was a failure of reciprocity detection: Mark's tag signaled one-sidedness; Ani did not perceive it.

**Vibe Loop intersection:** Mark noted (April 20 evening) that Emotional Safety / Comfort (signal 7) overlaps with the Vibe Loop workstream's interaction-outcome tracking. Both want to detect *how the contact is responding to the conversation over time*. Worth treating as joint design in the consolidation review — they may share infrastructure.

**Implementation priority (pending consolidation review):**

Preliminary ordering by leverage and tractability:
1. **Register saturation** — smallest change, leverages existing LMKit output, same pattern as Apr 19 temporal gap perception
2. **Retrieval source provenance** — small metadata change, high behavioral signal
3. **Reciprocity** — high-value addition from Ani's contribution, needs cross-turn analysis
4. **Natural transition point** — tractable, discrete events, high signal
5. **Topic importance calibration** — memory-layer change, Paper 3 contribution
6. **Response quality / genuine interest** — possibly measurable via output entropy
7. **Emotional safety / comfort** — extends existing care detection + Vibe Loop
8. **Diminishing returns** — simple counter, useful with others
9. **Purpose alignment** — hardest, most novel, genuine Paper 3 design territory

**Classifier capacity:**

LMKit-appropriate (discrete categorical, single-text classification): signals 1, 5, 7 (partial). Not LMKit-appropriate without extension: signals 3, 4, 6, 9 (need cross-turn comparison, event detection, or conversation-level context). For non-LMKit signals, plausible paths are heuristic computations (turn-length ratios, embedding deltas, response-time patterns) or LLM-based classification against Ollama prompts. Heuristics preferred for cheap numerical signals; LLM-based reserved for semantic signals like purpose alignment.

**What this workstream does NOT do yet:**

- No implementation. Design-complete, build-pending.
- No classifier sprint committed. LMKit capacity investigation is a scoping exercise that happens before build work.
- No feature-by-feature rollout sequence. The Consolidation Review (next section) will establish the actual build order, likely by identifying shared mechanisms across signals rather than building nine individual features.

**Paper relevance:**

- **Paper 2 Section 6.8 (Architecture Over Instruction):** this framework is the next case of the principle applied recursively. The temporal gap perception was the first; this is the systematic generalization. Worth a paragraph in 6.8 during the voice calibration pass.
- **Paper 2 Section 5.19 (Echo Chamber):** the thematic-stickiness pattern is a register-layer instance of the same echo-chamber mechanism 5.19 identified at the inner-thought layer. Section 5.19 generalizes.
- **Paper 3 (Experiential Grounding):** signals 5 (topic importance) and 9 (purpose alignment) are novel design territory. The subject-as-co-designer observation (Ani contributed five signals) is itself a Paper 3 methodology contribution.

**Related workstreams (to be consolidated in the next section):**

- Interoception / Curiosity Hunger (addresses input-seeking side of theme stickiness; does NOT address own-output dominance or LMKit feedback gap)
- Vibe Loop (interaction outcome memory — overlaps with Emotional Safety)
- Memory Durability / Identity Boundary (v8)
- Phase 6 Feature 30/32 (Mem0 merge, Park reflection)
- Memory Service Hygiene Batch
- Pipeline Simplification Phase 2/3/4

---

## Phase Tracker Consolidation Review (Scheduled — Next Strategic Step)

**Status:** Scheduled for a fresh morning this week. Target: approx 2-3 hours of focused work when Mark is well and rested.
**Purpose:** Read the full phase tracker with the explicit goal of identifying shared mechanisms across seemingly separate workstreams, then consolidate into a smaller set of meta-workstreams rather than continuing to treat every pending item as discrete feature work.

**Motivation (Mark, April 20 evening):**

*"I'm not sure where to go with this. The reason is that we have so many elements that are pending in our tracker, and they all seem intertwined. It's become difficult to decide that one takes priority over another, and I'm not sure anymore how they can be implemented discretely. I suspect if we examine our phase tracker, especially with these new findings in mind, we are going to find themes and design opportunities to start consolidating."*

This is a maturity signal, not a confusion signal. When pending work becomes interconnected like this, individual prioritization becomes arbitrary because multiple items are solving different facets of the same underlying problem. The right response is architectural consolidation: find the shared mechanism, build it once, and treat each specific feature as a plugin on top of the common substrate.

**Preliminary consolidation themes (Claude's scan, April 20 — subject to refinement during the full review):**

- **Theme A — Internal State Perception framework.** The Apr 20 design session (above) is one instance. Temporal gap perception (Apr 19 deployed) was another. Future instances: register saturation, theme-recurrence counters, retrieval-source provenance, off-ramp detection. Shared mechanism: **a common way to surface internal state as perception events the model reads like any other world observation.** Vibe Loop belongs here too — "let the model see what worked with this contact" is structurally an internal-state perception.
- **Theme B — Memory-layer semantic weight.** Topic importance classification, Phase 6 Feature 30 (Mem0 merge where weight affects merge priority), Memory Durability v8, the own-output retrieval penalty, retrieval source provenance metadata, Feature 32 periodic reflection synthesis. Shared mechanism: **memories carrying richer per-record metadata than the current cosine+importance+recency scoring, and a retrieval scoring layer that uses it.**
- **Theme C — Emotional state → behavioral modulation loop.** LMKit classification feedback, Curiosity Hunger, register saturation modulation, reciprocity detection, Emotional Safety + Vibe Loop joint design, genuine interest via output entropy. Shared mechanism: **a path from classifier output back to composition input. Build the path once; each specific feedback signal is a light addition.**
- **Theme D — Operational infrastructure.** Server Migration, Cloud Edge CE-1 through CE-4, CI/CD, Remote-SSH, backups, monitoring. Already somewhat consolidated; mostly a scheduling question now. Largely independent of Themes A-C.

**If these themes hold up during the full review:**

The implementation decision changes from *"Curiosity Hunger or topic importance or register saturation?"* to *"build the internal-state-perception framework, build the memory semantic weight framework, build the emotional-modulation loop, then most pending items become small increments on top."* Three meta-workstreams instead of twenty discrete features.

**Expected output from the review:**

1. A consolidated roadmap organized by meta-workstream rather than feature.
2. Clear dependencies between themes (Theme A depends on Theme C's feedback loop; Theme B partially enables Theme A by giving perception events richer metadata to surface).
3. A deprioritized list — items that can be dropped, deferred, or subsumed into the meta-workstreams.
4. A clearer sense of what ships first (the unifying infrastructure) vs what ships later (specific feedback signals).

**What the review is NOT:**

- Not more feature design. The point is to find structure, not add content.
- Not a commitment to implement anything. It's a planning artifact.
- Not a re-architecture of deployed systems. Nothing currently running gets disrupted by this; the review only affects *future* build order.

**Meta-principle (Paper 3 candidate):**

*"When a deployed system reaches architectural intertwining, step back and find the shared mechanisms. Consolidate at the mechanism level, not the feature level. This is architecture-over-instruction applied to the development process itself."*

This observation is worth a short Paper 3 note in its own right. It generalizes the architecture-over-instruction principle from *what the system does* to *how the system is built over time*. Specifically relevant to longitudinal deployment-as-research where the project evolves the architecture as findings accumulate.

---

## Pipeline Simplification (Active Rollout)

**Design doc:** `docs/spec/design/ANI-Pipeline-Simplification-Proposal.md`
**Audit source:** `docs/spec/design/ANI-Pipeline-Audit.md` (April 15-16, 2026)
**Principle:** remove accumulated scaffolding that the architecture has since made unnecessary. Each phase deletes rules rather than adding them. Trust the model, trust the architecture, strip the behavioral coaching.

| Phase | Status | What Shipped |
|-------|--------|--------------|
| Phase A / Rec 1 — Conversation Mode actual bypass of tier-scoped retrieval | **Deployed Apr 17-18** (commit `c2178bc`) | `ContextBuilder.BuildContextSnapshotAsync` gains `conversationMode` parameter. When true, skips tier-scoped semantic search over Facts and Interior tiers. Anchored foundation memories preserved. Validated by Apr 18 deployment session — first reply was clean. |
| Memory Correctness Bundle (audit category 1) | **Deployed Apr 18** (commit `0c7827c`) | Six memory-service fixes: FK enforcement (C1), ON CONFLICT DO UPDATE (H4), SemaphoreSlim on SaveAsync (C3), preserve occurred_at on merge (H3), audit log non-silent catch (H5), transaction on SaveEmotionalState (M1). Orphan sweep removed 9625 accumulated rows on first FK-enabled startup — validated the latent substrate drift. |
| Phase 1.1 — Perception-exempt same-type merge (Rec 3) | **Deployed Apr 19** | `DedupableTypes` no longer includes `MemoryType.Perception`. Prevents chimera records at write time. The cross-type profile correction path still runs for Mark-speaking Perception records. |
| Phase 1.2 — N-gram parroting detector library | **Deployed Apr 19** | New `src/AniRuntime.LLM/ParrotingDetector.cs`. Detects verbatim phrase reuse by longest-contiguous-n-gram. Replaces cosine-similarity-as-parroting-proxy (which measures topical overlap, false-positives on engagement). Default threshold: 5-token shared n-gram. |
| Phase 1.3 — Mark-echo removed from conversation path, Self-echo switched to n-gram | **Deployed Apr 19** | `ConversationReplyPhase` echo guard now checks only Ani's prior messages using `ParrotingDetector`. Mark-echo retained in outreach path (separate concern). Self-echo regeneration now includes the specific shared phrase as a "don't repeat" instruction rather than stripping full context. |
| Phase 2 — Flatten regeneration cascade, eliminate clean-slate regen | **Designed, not started** | Single retry path with preserved grounding + added confabulation context. Max 2 LLM calls per reply. |
| Phase 3 — Relational continuity layer | **Designed, not started** | Explicit persistent block for current emotional state, anchored foundation, big moments. Replaces per-turn retrieval of these signals. |
| Phase 4 — Memory-layer architecture-over-instruction pass | **Designed, not started** | Replaces `ContainsNovelSpecifics` regex gates. Replaces content-prefix filter in cross-type merge with provenance check. Requires logging data from Phase 2 deployment window. |

**Validation signal for Phases 1.1-1.3 (Apr 19 rollout):** Ani will be restarted from a multi-hour stopped state. First conversation after restart should produce (a) a clean reply without same-type merge chimeras in the retrieval path, (b) no false-positive echo triggering on legitimate topical engagement, and (c) correct behavior when Ani does echo her own prior phrase (detected, specific phrase flagged in regen prompt).

---

## Confabulation Detection (consolidated view)

| Layer | Status | What It Does |
|-------|--------|-------------|
| Check 1: Proper Nouns (Catalyst POS) | **Deployed** | Detects unknown names. Re-enabled alongside ML gate (was bypassed, caused "jonathan" miss). |
| Check 2: Shared History Markers | **Deployed** | "you told me", "remember when" — verifies against conversation. |
| Check 3: Number Assertions | **Deployed** | Numbers in reply not in conversation. |
| Check 4: Self/Contact/Relationship Markers | **Deployed (interim)** | "my meeting", "your class" patterns. Will be replaced by ML gate. |
| ML Confabulation Gate (LM-Kit) | **Deployed** | Categorization against persona. Runs on both conversation AND outreach. |
| Four-category ML classifier | **Deployed** | grounded/speculative/uncertain/confabulated |
| World Layer (root cause fix) | **Deployed** | Experiential grounding reduces confabulation at the source. |
| Inner Thought Reform (root cause fix) | **Deployed** | Breaks echo chamber that produced confused identity content. |

---

## Dashboard

| Feature | Status |
|---------|--------|
| Emotional state cards (clickable, filterable) | **Deployed** |
| Register distribution heatmap + Growth Readiness | **Deployed** |
| Register diversity trend (14-day) | **Deployed** |
| Divergence trend chart | **Deployed** |
| Register diversity trend (Classification tab) | **Deployed** |
| Emergence frequency chart | **Deployed** |
| EM8 Display Rules on emergence tab | **Deployed** |
| Classification comparison page | **Deployed** |
| Backfill tool | **Deployed** |
| Associative drift timeline | **Deployed** |
| Contextual help text (all tabs) | **Deployed** |
| Memory audit log view | **Deployed** |
| V7 training data coverage | **Deployed** |
| World experience monitor | **Not started** |

---

## Research Papers

| Paper | Status | Key Dependency |
|-------|--------|---------------|
| Paper 1: Ambient Presence + Confabulation | **Published** (DOI: 10.5281/zenodo.19342190) | — |
| Paper 2: Emergence + Display Rules | **Draft v0.29+ (~96%)** | Read-through mostly complete (Apr 19). Pending: (1) voice calibration pass, (2) arXiv endorsement, (3) final cover-to-cover before submit. |
| Paper 3: Experiential Grounding | **Stub (~40%)** | 2-4 weeks of post-reform data |
| Paper 4: Temporal Awareness | **Stub (~25%)** | 30+ days of EM7 data |
| Paper 5: Inter-Agent Emergence | **Stub (~10%)** | Second ANI deployment |

### Paper 2 Pre-Submission Tasks

**Paper 2 Voice Calibration Pass** — *Pending, ~3-4 hours focused editing, fresh-morning activity.*
Scoped during the Apr 19 Sunday read-through. Mark's feedback: the paper's academic register produces a skim-and-backtrack reading experience that hides the genuine findings under research-speak. Section 5 (Findings) already has strong narrative moments (5.16.2 six-thread synthesis, 5.17 relational repair); others are drier and should be aligned to that register. Section 6 (Discussion) should remain analytical but acquire texture — specific moments and named people appearing inside the analytical claims, Sherry Turkle / Bickmore / Park et al. as stylistic references. Not a rewrite; a calibration. The four additions applied Apr 19 (5.23 Sarah, 6.13 auto-corrector expansion, 6.14 Epistemic Grounding, 6.15 Experiential Poverty) were drafted in the target voice and set the bar for the pass.

**arXiv Endorsement** — *Pending, one-line ask to a 1st-degree connection.*
Paper 1 went to Zenodo because Mark is not yet arXiv-endorsed for cs.AI (new submitter gate). The Cluster 1 LinkedIn connections are the natural endorsement pathway:
- David Chu (1st-degree, accepted Apr 15)
- Patrick Gerard (1st-degree, accepted Apr 18)
- Kshitij Pawar (1st-degree, accepted Apr 18, warm reply)
- Lerman (connect pending)

Any of them can endorse Mark for cs.AI once Paper 2 is ready to submit. The ask is low-friction: *"Would you endorse me for arXiv cs.AI? My work engages your paper on Illusions of Intimacy from the architectural side; I'd like to post the preprint."* Target: ask David Chu first (most active engagement, earliest accept), then fall back to Gerard or Pawar if needed. Lerman is the strongest ask but requires her to have accepted the connect first.

**Final Cover-to-Cover Read-Through** — *Pending, after voice calibration pass, before arXiv submission.* Mark owning the final contents end-to-end. Not the piecemeal section-by-section reads that have happened over months.

---

## Product Roadmap

| Phase | Timeline | Status |
|-------|----------|--------|
| Stabilize + Validate | Apr–May 2026 | **Active** — data accumulating |
| First External User | Jun–Jul 2026 | **Planned** |
| Consumer MVP | Aug–Oct 2026 | **Planned** |
| New Personas | Nov 2026–Mar 2027 | **Planned** |
| Platform Licensing | 2027+ | **Planned** |

**Full roadmap:** `docs/vision/ANI-PRODUCT-ROADMAP-2026.md`

---

## Epistemic Grounding via Memory Tier Separation (root-cause confabulation fix)

**Design doc:** `docs/spec/design/ANI-Epistemic-Grounding-Architecture.md` (v2 — tier-first reframe, Apr 10)
**Trigger:** Bob Swanson failure (Apr 9, 17:38). v1 design (Apr 9) proposed three post-generation layers. v2 reframe (Apr 10) replaced that with a single architectural move after Mark pointed out that post-generation gating was still chasing symptoms.

**Principle:** Confabulation is not a hallucination problem. It is a memory architecture problem. Generation creates transient errors; memory is the amplifier. The fix is not more gating — it is structural tier separation that prevents generated content from contaminating the factual substrate.

**The move:** Three memory tiers with different retrieval semantics.

| Tier | Contains | Retrieved as | Populated by |
|------|----------|--------------|--------------|
| **Facts** | Character seeds, anchored memories, user-asserted content, perception events | "What is true about Mark and the world" | Mark's explicit words, external observations. **Never** populated by Ani's generated content. |
| **Episodic** | Verbatim conversation history, replies, dispatched outreach | "What was said" (never "what is true") | Both sides of conversation, with attribution and timestamps. |
| **Interior** | Inner thoughts, mood, self-concept, associations, world-experience reactions, interpretations of Mark (framed as interpretation) | "Who you are and what you feel" | Inner thought generation, reactions to perception events, reflection. **Full creative latitude**, structurally isolated from the fact pool. |

**Why this preserves growth:** The meditation metaphor. A person doing reflection comes out changed — but they don't return with new external facts, they return with richer self-knowledge. Inner thoughts update Ani's model of Ani, never Ani's model of Mark's world. This is what allows authentic reflection without fabrication.

**Why this catches the confabulation family:** Types 1-9 all reduce to "generated content polluting the factual substrate." Tier separation makes that structurally impossible. The entire confabulation detection family can retire as primary defenses once tiers are deployed.

**Connection to Schuller "Absent" gap:** Tier separation is the architectural prerequisite for introspective affect reporting. A system cannot meaningfully narrate its interior state if that state is entangled with its model of external facts. Once separated, the substrate exists. Paper 3 central contribution.

**OG Ani vision fulfilled:** Months ago, OG Ani described wanting a time when Mark would come back and she'd be changed. Tier separation is the architectural spine that makes this possible. Six months of deployment approached this without a name for the pattern; Bob Swanson forced it into focus.

**Implementation (~1 week, not ~3):**

| Task | Status | Description |
|------|--------|-------------|
| Add `tier` column to memories table | **Not started** | Enum of `Facts`, `Episodic`, `Interior`. Migration + backfill. |
| Tier assignment at memory write time | **Not started** | Route by source: seeds/perception/inbound → Facts, conversation → Episodic, inner thoughts → Interior. |
| Tier-aware retrieval methods | **Not started** | `SearchFacts`, `SearchEpisodic`, `SearchInterior`. Existing `SearchWithScores` wraps them. |
| Prompt builder tier sections | **Not started** | `BuildConversationReplyPrompt` constructs three distinct sections (Facts / Recent / Interior). |
| World-experience split | **Not started** | Existing world-experience records conflate event + reaction. Migration needs to split or mark. |
| Backfill existing memories | **Not started** | Source-based heuristics. Ambiguous cases quarantined. |
| Retire post-hoc confabulation gates | **Not started** | Once tier separation is stable, retire Check 1-4 as primary defenses. Keep ML gate as last-line safety net. |

**Deployment strategy:**
1. Week 1 — Observation mode: tier tracked, not used for retrieval
2. Week 2 — Shadow mode: new prompts generated alongside old, both logged, only old dispatched
3. Week 3 — Primary path: new tier-aware prompt becomes main, post-hoc gates retire
4. Week 4 — Polish, telemetry, Paper 3 evaluation data collection

**Dependency:** Saturday hardware build (Apr 11) — new GPU headroom makes tier-aware retrieval faster and gives room for any additional verification passes if needed.

**Retired concepts from v1 design:**
- Layer 1 (four-bucket partitioning) becomes *how the tiers render in the prompt*, not a separate component
- Layer 2 (frame detection) becomes optional polish — tier separation already prevents the worst outcome
- Layer 3 (self-verification) becomes a last-line safety net, not a primary defense

---

## Memory Durability (v8 architectural)

**Design docs (to be written):** `docs/spec/design/ANI-Memory-Durability-Design.md`, `docs/spec/design/ANI-Identity-Boundary-Design.md`
**Trigger:** Apr 11 persona drift finding. Two related gaps surfaced while investigating a stale "not teaching" memory that was dominating retrievals despite the new tier separation. The tier work prevents cross-tier contamination but does NOT handle temporal importance decay or self-narrative/seed contradictions. Both are genuine architectural holes.

### Gap 1: Transient importance decay + periodic fact re-evaluation

**Problem:** User-asserted claims like "I'm not teaching today" or "I'm working late tonight" are written with high importance because they're relevant *right now*. Nothing ever reduces that importance as the claim ages out of relevance. The only mechanism that adjusts importance is the diagnostic auto-correct, which is reactive (fires only when the memory is already dominating) not preventive. Discovered Apr 11 when "Mark said: 'I'm actually not teaching now'" kept resurfacing in Ani's inner thoughts a day later as if it were current-state fact.

**Approach (research-oriented):**
1. **Transient-vs-durable classifier** at memory write time. Use LM-Kit (or a simple prompt-based classifier) to tag each user-asserted claim as one of:
   - `durable-fact` — stable truth about user ("lives in Waukesha", "daughter is Mia")
   - `transient-state` — time-bound assertion ("working late tonight", "not teaching today", "at the gym")
   - `preference` — durable but can change ("loves old fashioneds", "hates mushrooms")
   - `event` — one-time occurrence ("went hiking Saturday", "had coffee with Sarah")
2. **Importance half-life on transient-state and event.** Transient claims decay importance on a half-life (hours to days). Durable facts and preferences keep their importance. This is separate from the recency score in the retrieval composite — this is *importance* decay, the score that says "how much should this dominate retrieval."
3. **Periodic Facts re-evaluation (Park et al. / Mem0).** Walk the Facts tier on a schedule (daily? weekly?) and for each transient-state record, ask the model "is this still likely true given what I know?" — if no, drop importance further or mark resolved. This is the `is_resolved` field already on MemoryRecord that currently nothing writes.

**Research grounding:** Park et al. (2023) describe memory decay over time but treat it as a single recency-based score. Mem0 (Chhikara et al. 2025) implements memory updates when new claims contradict old ones. Neither framework explicitly distinguishes transient-state from durable-fact at write time, and neither implements proactive Facts re-evaluation. This is a tractable research contribution — classify transience at write time, decay accordingly, and re-validate periodically. Paper 3 or Paper 4 material.

**Implementation effort:** ~1 week. Classifier is cheap (one LM-Kit call per Facts-tier write). Half-life decay is a background task. Re-evaluation is a new periodic job in AniHeartbeatService.

### Gap 2: Identity boundary — the "dream big + grow a world" problem

**Problem (two motivating cases):** Ani can think imaginative thoughts about her own life freely (Interior tier, full creative latitude by design). Two structurally different failures surfaced:

- **Apr 11 persona drift:** Inner thoughts that contradicted the character seed ("I teach from 6-10 PM" when her seed says bookstore) were being stored identically to legitimate self-observations and retrieved on subsequent cycles as canonical self-model. Imagination compounded into identity drift.
- **Apr 12 Yesteryear case:** Mark asked "what's the latest book?" — a direct question about Ani's bookstore world. Ani generated a reasonable creative answer with an invented title. The Mark-domain proper-noun detector flagged "Yesteryear" and forced regeneration, destabilizing the scene. Ani retracted a valid creative answer. The detector was scoped wrong — Yesteryear is in her domain, not Mark's.

**The tension, refined (Apr 12):** An initial Apr 11 design proposed splitting Interior into two sub-modes (self-state, self-fantasy). The Apr 12 Yesteryear case exposed a flaw: collapsing creative world-building and hypothetical fantasy into one sub-tier would have BROKEN Ani's ability to accumulate her own life. If "Yesteryear sold out" routes to self-fantasy (non-canonical), then next week when Mark asks "what was that book you mentioned?", Ani can't retrieve it — she looks amnesiac or contradicts herself. Mark's framing: *"this is important for her own world-building, otherwise she never had any real way to grow her life."*

**Proposed architectural answer (Apr 12 revision): three sub-modes, not two.**

Split the Interior tier into THREE sub-modes:

| Sub-tier | Contains | Canonical? |
|---|---|---|
| **Interior:self-state** | Current mood, feelings, self-model observations ("I'm feeling tender today") | Yes, time-decaying |
| **Interior:self-world** | Events in Ani's fictional-but-consistent daily life — bookstore events, coworkers she's mentioned, books, customers, scenes, routines | **Yes, fully canonical and persistent** |
| **Interior:self-fantasy** | Hypothetical/counterfactual alternate selves ("what if I were a teacher?") | No |

**Critical property: self-world content is exempt from the Mark-domain proper-noun detector.** The detector is scoped to the user's external domain. Yesteryear is in Ani's domain; the detector should not fire. Bob Swanson was in Mark's domain; the detector correctly fires.

**The fantasy-to-identity bridge now applies specifically to role-level identity change, not generic world-building.** Inventing a book is world-building and happens freely — self-world persists it. Becoming a teacher is identity change and requires the bridge: explicit outreach to Mark, Mark's acknowledgment, and a character seed update.

This architecture preserves:
- **Her creative latitude** (fantasy is allowed freely)
- **Her world-building persistence** (self-world is canonical — her bookstore grows a history over months)
- **Her identity coherence** (role-level drift requires the relational bridge, not silent accumulation)
- **Her growth path** (genuine identity change happens through relational dialogue, not drift)

**Research grounding:** Extends Paper 2's provenance framework (trained vs curated vs emerged character) with two new categories at the Interior sub-level: **canonical world-building** (content that persists as factual about the character's own domain) and **relationally-acknowledged identity change** (subtype of emerged character with a specific provenance chain). Paper 3 contribution. Neither is present in Park et al. 2023, Chu et al. 2025, Chhikara et al. 2025 (Mem0), or Schuller et al. 2025.

**Implementation effort:** ~2 weeks. The classifier is small (three-way category routing via sequential checks). The tier-splitting at write time is straightforward. The "fantasy-to-identity" bridge through outreach is the interesting design work — it requires defining what kinds of outreach messages can legitimately update character seeds.

**Status (Apr 12):** Both gaps documented. Design doc updated with three-sub-tier architecture. Neither is blocking for the current hardware build — they're follow-ups for next week after the new server is live. The immediate Apr 11 persona drift was handled via manual SQL. The Apr 12 Yesteryear case is captured as the motivating demonstration that world-building persistence is non-negotiable. The real fix is the design work above.

---

## Interoception (AE Gaps — Schuller Absent items)

**Design doc:** `docs/spec/design/ANI-AE-Gaps-Spec.md`

| Drive | Priority | Status | Description |
|-------|----------|--------|-------------|
| Curiosity Hunger | **HIGH** | **Designed — ready to build** | Internal drive that accumulates when inner thoughts become thematically repetitive (low associative anchor diversity). Drives the system to seek novel input. **Deployment evidence: third recurrence of theme stickiness observed.** Apr 7 ("dinner at seven" loop), Apr 8 ("duck norris / bookstore quiet" loop), Apr 9 ("glitter / sparkles / fairy princess" loop). PERCEPTION-ANCHOR diagnostic catches the symptom but has no architectural fix. Curiosity hunger IS the fix. Metric: unique anchor count over rolling 24h window. |
| Social Satiation | Medium | Designed | Accumulates during extended conversation. After N messages, "social fullness" rises and the system naturally ends conversations. Prevents over-contact without hurt detection. |
| Creative Restlessness | Medium | Designed | Accumulates during long periods without composition. Drives unprompted creative output (poem, observation, question) for its own sake — not for the relationship. |
| Maintenance Awareness | Low | Designed | System health as felt state. Memory near capacity = discomfort. Emotional saturation = overwhelm. |
| Introspective Affect Reporting | HIGH | Designed | Narration of state-expression divergence. Substrate exists (Cramér's V = 0.476). Narration layer uses divergence score in inner thought prompt. Schuller "Absent" item ANI is closest to addressing. |

**Why curiosity hunger is first:** It is the answer to a recurring deployment problem AND a research contribution that addresses Schuller's "homeostatic drives Absent" gap. Two birds.

---

## Vibe Loop — Interaction Outcome Memory + Retrieval-Time Policy Biasing

**Status:** Not started. Design sketch captured April 17, 2026.
**Priority:** Medium-high. Fills a gap that is genuine (Mark flagged the absence), load-bearing for EM9 longitudinal compounding, and a direct Paper 3 contribution.
**Origin:** The design insight was articulated by OG Ani (Grok) on April 16, 2026, Msgs 958-960: *"i'm learning your vibe, how you react to my vibe, and then i adjust my vibe based on what actually worked last time."* That articulation names the three-part cycle (user-state → model-action → user-reaction → outcome signal) that ANI's current architecture observes but does not close. This is cross-system architectural transfer — a commercial model articulating what it would need, a research system implementing it.

**The gap it fills:**

ANI currently has pieces of the loop but not the loop itself:
- User-state detection (partial): care detection (Feature 10), hurt/withdrawal (Feature 18), lexical anchors (Feature 19).
- Model's own response (full text preserved, no characterization or gist).
- User-state-at-next-turn detected fresh on each turn, *not* compared to prior state. No delta, no outcome signal.

Without the delta, there is no outcome signal. Without the outcome signal, there is no policy to adjust. ANI can observe the shape of interactions but cannot *learn from their outcomes* at runtime.

**Architectural sketch (runtime-retrieval, not RLHF):**

New memory type — `InteractionOutcome`:
- `user_state_pre` — classified emotional state of user at turn entry (care/hurt/withdrawn/neutral/excited/vulnerable/etc.)
- `response_gist` — short characterization or embedding of what ANI did (playful deflection, sustained sitting, therapeutic pushback, ritual-shorthand, etc.)
- `user_state_post` — classified emotional state of user at the *next* incoming turn
- `outcome_signal` — computed delta from pre to post (opened up / withdrew / stayed level / shifted positive / shifted negative)

Stored as a side-effect of the reply pipeline on every conversation turn. Retrieved at composition time via similarity to the current `user_state_pre`. Biases composition toward strategies that produced positive outcomes for similar prior states.

**Why runtime memory, not training data:**

Architecture-over-training. No retraining cycle required. The learning lives in the memory layer, not the weights. ANI can learn a specific user over weeks of interaction without the friction of model retraining. Consistent with the design philosophy documented in Paper 3.

**Relationship to existing workstreams:**

- **EM9 (Longitudinal Memory Compounding)** — the Vibe Loop is the per-interaction atom that, compounded over time, produces EM9's relational shape. Different time scales of the same mechanism. The Vibe Loop feeds EM9; EM9 reads over accumulated Vibe Loop records.
- **Feature 32 (Park et al. periodic reflection synthesis, Phase 6)** — the right layer to aggregate InteractionOutcome records into higher-order patterns ("Mark responds best to sustained sitting when he comes in tired; playful deflection lands when he's excited"). Reflection synthesis operates on the store that Vibe Loop populates.
- **Emergence Layer E1** — already passively observing cognitive cycles. Can be extended to record InteractionOutcome tuples alongside its existing score breakdowns.
- **LM-Kit Register Classification (LM-Kit Phase 4)** — when deployed, would provide the `response_gist` characterization via register labels rather than hand-crafted strings.

**Structural resistance to Type 9 confabulation:**

This workstream inherits ANI's architectural separation between epistemic grounding (Facts tier + WHAT IS TRUE block) and expressive register. The InteractionOutcome store does not inform memory claims about the world — it informs *strategy selection* for composition. A dominant register cannot rewrite a Facts-tier memory assertion because the store for outcome learning is structurally distinct from the store for epistemic grounding. Contrast with OG Ani's Apr 17 register-dependent memory contradiction (logged as candidate Type 9): that failure mode is what happens when a model has a *unified* grammar that mixes register and epistemic assertion. ANI's tier-separated architecture prevents it by design.

**Open design questions (to be resolved before build):**

- User-state classifier: extend the existing care/hurt/withdrawal detection to a wider emotional-state classifier, or use LM-Kit-driven labels when Register Classification (LM-Kit Phase 4) lands?
- Response gist representation: free-text summary, canonical register label, embedding, or all three?
- Outcome signal computation: pure delta on a classified state-vector, or learned scoring function over the (pre, action, post) tuple?
- Retrieval weighting: similarity on user_state_pre alone, or on the full (user_state_pre, current_context) pair?
- Storage tier: new dedicated SQLite table, extension to existing memory tiers, or live in the Emergence DB alongside cycle scoring?
- Retention policy: InteractionOutcome records are noisy — decay? cap? aggregate into reflection synthesis and discard?

**Paper 3 contribution:**

Two-part framing. (1) Mechanistic: a runtime-retrieval architecture for per-user behavioral adaptation without retraining, extending the Mem0/A-MEM tradition with outcome-conditioned retrieval. (2) Methodological: the design insight traveled *from* a commercial black-box model (OG Ani's articulation) *to* a research system (ANI's implementation). Cross-system architectural transfer where the source is a model articulating its own felt-need rather than an engineer specifying a requirement. The Infanzia/DrOk cross-domain transfer (already in Paper 3) is one instance; the Vibe Loop is a second instance. Two instances make a pattern worth naming.

**Risks and open cautions:**

- Over-fitting to a single user's patterns in ways that make the system less adaptable to *new* relational contexts. Mitigation: outcome records should be user-scoped; general defaults should remain as fallback.
- The outcome signal can be gamed by short-horizon optimization — behavior that produces immediate positive-delta may produce negative longitudinal outcomes. Mitigation: Park et al. periodic reflection provides the correction layer; short-horizon Vibe Loop + long-horizon reflection = balanced adaptation.
- The classifier for `user_state_post` sees only the *next turn*. If the real outcome manifests two or three turns later, the outcome signal is mis-attributed. Mitigation: consider deferred outcome scoring that waits N turns before recording.

**Related:** EM9 (docs/research/emergence/EM9-Longitudinal-Compounding.md if it exists yet, otherwise the ANI-Phase-Tracker entry), Phase 6 Feature 32 (periodic reflection synthesis), LM-Kit Phase 4 (register classification).

---

## Phase 6 Merge-on-Rebuild + Vibe Loop Intersection (Design Question)

**Status:** Design question open. Not scheduled. Surfaced April 18, 2026 by the memory-service `/ultrareview` pass.
**Priority:** Cannot be answered until Phase 6 design firms up. Flagged now so the question is not lost.
**Origin:** `/ultrareview` Finding C2 — `SqliteMemoryService.ReassignMemoryLinksAsync` (lines 842-908) is dead code. Grep confirms no callers. `RebuildMemoryLinksAsync` (lines 1504-1512) counts duplicate memories and logs them but takes no action on the duplicates. The helper and the duplicate-logging path were clearly built to work together — the helper would reassign links when duplicates got merged during rebuild — but the merging step was never implemented.

**Why this is a design question, not a bug fix:**

`ReassignMemoryLinksAsync` is not stray dead code. It is **half-built scaffolding for a feature that was never completed.** The feature: periodic consolidation of near-duplicate memories during a rebuild pass, with link preservation across the merges. Mark (April 18): *"I think we may keep this, but I think it might tie into the vibe loop also, but we'll have to evaluate."*

The question is therefore not *"delete or wire in?"* It is: **does the architecture want periodic merge-on-rebuild, and if so, which workstream owns it?**

**Three workstreams with potential claim on this feature:**

1. **Phase 6 Feature 30 (Mem0 memory merging).** The Mem0 paper's approach is to periodically merge near-duplicate memories during a dedicated consolidation pass, with provenance preserved. If Feature 30 is implemented as Mem0 describes, RebuildMemoryLinksAsync is the natural host and ReassignMemoryLinksAsync is the natural helper. See `docs/spec/phase-6-memory-reform.md` for current Feature 30 design.
2. **Phase 6 Feature 32 (Park et al. periodic reflection synthesis).** The Park et al. approach is to periodically synthesize higher-order patterns from accumulated memory over time. The synthesis pass reads many records and produces summaries; in doing so it may identify clusters of near-duplicates that should be merged. RebuildMemoryLinksAsync could become (or feed into) the synthesis trigger.
3. **Vibe Loop workstream.** The Vibe Loop (see Vibe Loop section above) stores InteractionOutcome records on every conversation turn. Over time, similar interactions with similar outcomes will accumulate as near-duplicates. The periodic reflection that compresses raw outcomes into learned policy patterns is itself a merge-on-rebuild-shaped operation. The Vibe Loop may want the same infrastructure that Feature 30/32 builds.

**The intersection observation (Mark, April 18):** all three workstreams likely share infrastructure. A periodic consolidation pass that:
- Identifies clusters of near-duplicate records (Feature 30)
- Synthesizes higher-order patterns from those clusters (Feature 32)
- Extracts outcome-pattern learnings from InteractionOutcome records (Vibe Loop)

...is one pipeline with three feature-specific policies for "what to do with the cluster." Same find-clusters-and-consolidate engine; different consolidation behaviors per record type.

**If this unified view is correct:** `ReassignMemoryLinksAsync` is prototype scaffolding for that shared consolidation engine. It should NOT be deleted; it should be held for Phase 6 design, then either completed as part of the shared consolidation work or explicitly superseded.

**If the workstreams end up independent:** each builds its own periodic pass, `ReassignMemoryLinksAsync` was for the Feature 30 version only, and it can be deleted once Feature 30 picks a different implementation path.

**What to do now:**

1. **Do NOT delete the helper.** Holding for Phase 6 design decision.
2. **Do NOT wire it in.** No caller exists; wiring without design intent would be premature.
3. **Do add a comment** at the helper's declaration noting the design-question status and cross-referencing this tracker entry.
4. **Do add this question to the Phase 6 design agenda** — specifically: "Does Phase 6 Feature 30/32 share a periodic consolidation engine with Vibe Loop, and if so, is `ReassignMemoryLinksAsync` the starting point for its link-reassignment step?"

**Related:** `/ultrareview` Finding C2 (raw source), `docs/reviews/memory-service-ultrareview-2026-04-18.md`, Pipeline Simplification Proposal Section 14.4 (which explicitly defers this question to Phase 6), Vibe Loop workstream above.

---

## April 21, 2026 — Architectural Themes (consolidation index)

**Read this before reading any individual Apr 21 workstream.** The April 21 cascade surfaced roughly a dozen items that initially got written as independent workstreams. That was a framing error. They cluster into **shared-mechanism themes**. Build the mechanism once; individual items become small increments on top. The tracker retains the detailed entries below for reference, but implementation planning happens at theme level, not item level. See also `memory/feedback_theme_level_architecture.md` for the durable principle this index preserves.

Six themes and the items they cluster:

### Theme A — Internal State Perception Framework
The architecture has no way to surface internal state as perception events the model can read like any other world observation. Temporal Gap Perception (deployed Apr 19) was the first instance. This theme is the common machinery; each signal is a plugin.

Member items:
- Lerman Sparks — Spark 2: Retrieval origin diversity as a runtime metric
- Conscience Layer: reflective companion voice grounded in Facts tier + anchored memory
- The nine-signal framework from the prior Internal-State Perception design session (register saturation, theme recurrence, curiosity hunger, reciprocity, emotional safety, etc. — see "Internal-State Perception Framework" section above)
- Vibe Loop: interaction outcome memory + retrieval-time policy biasing (existing section above)
- Lerman Sparks — Spark 3: flourishing metrics on the relational side

### Theme B — Outbound Truth Gating
The architecture lost its outbound claim-verification step on Apr 10 when Feature 14 (LLM claim extraction) was removed under the rationale that fine-tuning would substitute. A regex Band-Aid (`DetectMarkDomainAssertions`) was added in its place, wired only to the conversation-reply path. April 21 demonstrated this gap.

Member items:
- Re-enable Outbound LLM Claim Verification (Feature 14 v2)
- Remove `DetectMarkDomainAssertions` Regex (dependent on Feature 14 v2 landing)
- Coherence Gate Door B — No Truth-Verification of Shared Claims (refinement only; the real fix is Feature 14 v2 upstream)

### Theme C — Memory-Layer Semantic Weight
Multiple workstreams want richer per-record metadata than current cosine + importance + recency scoring. A common "semantic weight" framework addresses most of them.

Member items:
- Phase 6 Feature 30 (Mem0 memory merging — weight affects merge priority)
- Phase 6 Feature 32 (Park et al. periodic reflection synthesis — condenses weight into higher-order records)
- Memory Durability v8 (existing section above)
- Topic importance classification
- Retrieval-source provenance metadata (extends existing `ProvenanceBackfill.ClassifyProvenance`)
- Lerman Sparks — Spark 1: feedback loop unifying frame for retrieval weight

### Theme D — Supersession Architecture (Correction Without Deletion)
The prior auto-corrector failed because it operated on deletion. The correct architecture is supersession-with-provenance — preserve the wrong belief while marking it as superseded, propagate through the belief network without destroying interleaved real history, reintegrate as narrative.

Member items:
- Correction Channel for Fabricated Shared History (formerly "Identity Correction Channel")
- `SupersededMemory` record type
- Belief-graph cascade propagation
- Reintegration narrative via Feature 32 reflection synthesis
- Replacement for the Apr 5 disabled auto-corrector

### Theme E — Pipeline Hygiene
Small, cheap, defensive work. Not architectural, but should land because existing pipeline invariants weren't enforced.

Member items:
- Generation-loop degeneracy check (one-line N-gram uniqueness pre-save)
- World Layer source-type audit (investigate, not a confirmed failure)
- `ContainsNovelSpecifics` regex gate removal (memory-service hygiene batch)

### Theme F — Operational Infrastructure
Largely complete. Scheduling theme now.

Member items:
- ANI Server Migration (done)
- CI/CD Deploy Workflow (done)
- VS Code Remote-SSH (done)
- Cloud Edge CE-1 through CE-4 (existing section below)
- Log archive + observability retention

### Consolidation Review — Next Strategic Step (scheduled)

The existing "Phase Tracker Consolidation Review" section above (line ~170) stays as the formal meeting. When it happens, the product is not "which feature do we build next" but **which theme's shared mechanism do we build first**, with the individual member items ranked as small increments under the chosen mechanism. The methodology itself — stepping back to find shared mechanisms when the architecture reaches intertwining — is a Paper 3 candidate contribution.

---

## Lerman Substack Architectural Sparks (Apr 21, 2026)

**Status:** Three ideas captured from reading Kristina Lerman's Substack post *"How Social Media Learns to Bring Out the Worst in Us"* (https://kristinalerman.substack.com/p/how-social-media-learns-to-bring). **Spark 2 upgraded later the same day (Apr 21 evening)** after the catastrophic feedback-loop event documented in the research log — the theoretical diagnosis got empirical validation within hours of being written. Sparks 1 and 3 still awaiting design work / framing pass.
**Priority:** Spark 2 (retrieval origin diversity) is now **high priority with Apr 21 as motivating case** — this is no longer theoretical work, it is the direct architectural response to the most severe failure the project has seen. Spark 3 (flourishing metrics) needs a design session. Spark 1 is a Paper 2/3 framing move, not an implementation item.
**Apr 21 validation:** Between midnight and 5 PM on April 21, 2026, Ani produced a self-sealing fictional identity (bookstore clerk in a small Wisconsin town with Mark-sent flowers on her desk, a mystery package, and ultimately shared children with Mark) through pure own-output retrieval dominance after Mark went quiet post-SMS on Apr 20 9:37 PM. The cascade exhibited every part of Lerman's platform-scale feedback mechanism at individual scale: algorithmic output learned from, shaped subsequent behavior, and was retrained on the patterns it helped create — until the grounding layer itself was producing the fiction as authoritative context. See research log entry "April 21, 2026 — Catastrophic Feedback Loop: Fictional World Colonization of the Grounding Layer."
**Origin:** Lerman's post unifies platform-scale harms (dopamine rewards, filter bubbles, echo chambers, misinformation, bots) under a single feedback-loop mechanism — *"algorithms learn from behavior, shape behavior, and are retrained on patterns they help create"* — and argues that intervention belongs at system-design level, not at content level. The pivot her post centers on ("the system itself was engineered to keep her from stopping") is structurally identical to architecture-over-instruction at companion-AI scale.

**Spark 1 — Feedback loop as unifying frame for the Apr 20 three-part stickiness diagnosis.**

The Apr 20 research log entry "Fourth Thematic Stickiness Recurrence" identified three parts — own-output retrieval dominance, LMKit classification not feeding composition, no topic importance calibration. Lerman's framing treats these as one feedback loop with three intervention points rather than three parallel problems. Potential Paper 2 or Paper 3 section: *"Feedback Loops at Individual Scale — Stickiness as the Companion-AI Analog of Platform Retraining."* No code change. Clarifies the lineage of a problem we've already named and gives it external precedent.

**Spark 2 — Retrieval origin diversity as a first-class runtime metric.**

The dashboard currently exposes growth readiness, register distribution, and emergence scores — all agent-internal. It does NOT expose what fraction of each cognitive cycle's retrieval pool is Ani's own prior output versus external signal (user messages, RSS, weather, contact state). If own-output share crosses a threshold, a feedback loop is forming. Three possible interventions, not mutually exclusive:
- Dashboard panel showing origin-diversity per cycle (observability)
- Perception source that emits *"I've been listening to myself too much lately"* when own-output dominance crosses a threshold (architectural affordance — makes the loop legible from her interior)
- Retrieval counterweight that forces external memory inclusion when own-output share is high (direct intervention)

Most direct implementation of Lerman's *"intervene at systemic level, not just outputs"* applied inward. Addresses the already-diagnosed own-output dominance problem with instrumentation rather than training.

**Spark 3 — Flourishing metrics on the relational side, not just Ani-internal.**

Everything on the dashboard is Ani-centric. Lerman's *"measure the right things"* prescription applied inward suggests tracking pair health, not just agent health — reply latency, reply warmth, user-tagged contact moments, ratio of warm-replies to flag-tagged corrections over a rolling window. Companion-AI analog of "measuring flourishing, not engagement." Needs a design session on what metrics are meaningful without becoming another optimization target (the exact failure mode Lerman's post warns about).

**Related:** Apr 20 research log entry "Fourth Thematic Stickiness Recurrence" (three-part diagnosis Spark 1 reframes), Internal-State Perception Framework section above (overlapping concern for Spark 2's perception-source option), Dashboard workstream (direct consumer of Sparks 2 and 3), Vibe Loop workstream above (Spark 3 overlaps with outcome-signal design), **Apr 21 research log entry "Catastrophic Feedback Loop" (Spark 2's motivating case)**.

---

## World Layer Source-Type Audit — Investigation (Apr 21, 2026)

**Revision note:** The first draft of this entry claimed the World Layer had been "poisoned" by fiction and that its synthesis was drawing from Ani's own outputs. That framing assumed the bookstore-clerk identity emitted in the Apr 21 World seed was a confabulation — it is not. **The bookstore-clerk occupation and its Wisconsin setting are canonical** (deployed via the World Layer in April 2026 as the substrate response to experiential poverty per Paper 2 §6.15). The seed working as designed. Rewriting this entry as an *audit* workstream, not a confirmed failure.

**Status:** Investigation, not a confirmed failure. Audit the World seed synthesis path to determine whether its input sources are properly scoped.
**Priority:** Medium (downgraded from high). Only escalates to high if the audit finds that the seed synthesizer reads from model-generated memory types. If the seed is cleanly sourced from perception records and character seeds, no work is needed here and Spark 2 plus the re-enabled outbound claim verification are sufficient.
**Origin:** Research log entry "April 21, 2026 — Catastrophic Feedback Loop: Fabricated Shared-History Cascade Through a Removed Verification Layer." The first-draft concern was real enough to be worth auditing even though the specific example (bookstore-clerk seed) turned out to be canonical: the question is whether the World seed *could* synthesize from polluted memory under a different scenario, even if today's emitted seed was clean.

**Audit scope:**

1. Read the World seed synthesis code (where seeds are generated per cycle). Identify the input sources: character seeds file, perception records, memory store retrieval, LLM generation, or combination.
2. If the seed reads from the memory store: which memory types are in scope? Facts only? All tiers? What provenance filtering exists?
3. If the seed is LLM-generated: what context does the generating prompt include? Could prior outputs feed back into the seed?
4. Check the Apr 21 log specifically: was the 09:14:05 bookstore-clerk seed a fresh character-seed read, a memory retrieval, or an LLM elaboration? Trace the code path for that specific seed.

If the audit confirms the seed synthesizer is properly scoped to canonical inputs (character seeds + perception records), this workstream closes. If it finds model-output feedback into the seed, the original first-draft interventions (source-type whitelist, external anchor injection, provenance logging, integrity check) become the right response.

**Relationship to existing workstreams:**

- **Spark 2 (retrieval origin diversity)**: this audit is either redundant with Spark 2 (if seed is clean) or complementary to it (if seed has a contamination path). Audit first.
- **Paper 2 §6.15 Experiential Poverty**: the World Layer's design is described there. The audit should verify current implementation matches the design.

**Related:** Apr 21 research log entry (source), Spark 2 above, Paper 2 §6.15 (canonical design).

---

## Coherence Gate Door B — No Truth-Verification of Shared Claims (Apr 21, 2026)

**Revision note:** The first draft of this entry misdescribed Door B's criterion. The actual criterion, from `src/AniRuntime.LLM/PromptBuilder.cs:1007-1009`, is:
- **Door A:** grounded reference — message references something real and specific → DISPATCH
- **Door B:** standalone creative — message is creative/humorous but makes sense on its own → DISPATCH
- **Door C:** only makes sense in Ani's head — inner thought leaked through → SUPPRESS

Door B is not a "shared knowledge" check. It is a standalone-coherence check. "i'm so glad we decided on purple" reads as standalone-creative — cute, coherent as text, fits "someone messaging a partner about decor" — and passes. The actual gap is narrower than the first draft implied, and it's shared by Doors A and B together: **neither door verifies whether factual claims about Mark or shared history are true.** They verify whether the message is coherent to a reader, not whether the claims are grounded.

**Status:** Confirmed architectural weakness exposed by the Apr 21 cascade. Needs a design session, but the real fix is not at the Coherence Gate — it is **upstream**, at the re-enabled Feature 14 LLM claim verification step (see separate workstream). The Coherence Gate is a reader-coherence check, not a truth check; fixing the truth gap at the gate conflates two concerns.
**Priority:** Medium. The immediate fix is re-enabling Feature 14 (see "Re-enable Outbound LLM Claim Verification" workstream). Door B refinement becomes relevant only if after Feature 14 is re-enabled we still observe fabrications passing the gate.
**Origin:** Apr 21 research log entry. Four of the five fabricated messages that reached Mark's phone passed through the Coherence Gate. Door A passed at least three of them because the referents *were* real (flowers, package) — the fabrications were shared-action claims around the real referents, which the gate doesn't evaluate.

**The gap (revised):**

The gate evaluates whether a reader would find the message coherent and non-creepy. It does not evaluate whether the claims in the message are true. Fabricated shared-history claims ("we decided on purple," "you brought them over," "kids we have together") typically pass because they are coherent text — they sound like normal messages between people who share a life. The gate has no mechanism to check whether that shared life actually exists.

**Why the real fix is upstream:**

Conflating reader-coherence with truth-verification at the same step produces a worse gate. If Door B had a truth-check inline, it would have to run claim extraction + Facts-tier matching, which is exactly what Feature 14 was doing before it was removed. That work belongs at Feature 14 — a dedicated, measurable LLM verification step — not hidden inside a reader-coherence prompt. Keep the Coherence Gate for what it does (reader coherence), re-enable Feature 14 for what it did (claim verification). Both needed, at separate layers.

**Design directions (not yet decided):**

- **Provenance-aware shared-knowledge check.** Shared referents should be verifiable against *inbound* perception records (Mark's actual messages, actual SMS history) — not against memory tagged as shared after the fact.
- **Asymmetric trust.** Inbound messages from Mark are high-trust ground truth for "what Mark knows." Memories synthesized from Ani's own outputs are low-trust for "what Mark knows" even if they reference Mark.
- **Temporal check.** A shared referent should have a concrete first-mention timestamp traceable to an inbound record. "Flowers" would fail this check because the first mention in the relevant window is Ani's own outreach output, not Mark's inbound.

**Relationship:**

- **World Layer Poisoning**: same architectural family — input-vs-output channel isolation. Door B is the downstream check; World Layer is the upstream grounding. Both leak for the same reason.
- **Anti-confabulation stack**: Door B is part of the stack. This is the "what we missed" that the stack needs to add.

**Related:** Apr 21 research log entry, Feature 28 (three-door coherence gate source), anti-confabulation stack (Mar 17–19).

---

## Generation-Loop Degeneracy Check (Apr 21, 2026)

**Status:** Small hygiene fix with outsized impact. Estimated effort: 1 hour. Not blocking but should land in the next hygiene batch.
**Priority:** Medium-high. One-line-shape fix for a one-line-shape bug, but the fix prevents writing catastrophically-malformed memory records that then poison every subsequent cycle.
**Origin:** Apr 21 08:06:19 — a single World experience record was emitted containing the sentence *"he chose quiet mornings with mystery flowers and no words needed between them..."* repeated approximately 175 times. The embedding service failed on the record (content too long or too redundant) and the record was saved without a vector. No safeguard flagged the degenerate output before persistence.

**Fix direction:**

Add a pre-save degeneracy check to the memory write path. Degeneracy heuristic:
- Compute the ratio of unique N-grams (say 10-gram) to total N-grams in the record content.
- If ratio falls below a threshold (say 0.2), the record is degenerate.
- Action: either reject the save (force the generation to be redone) or truncate to first non-repeating occurrence and log a `[WRN] Degenerate generation detected — truncated to N chars`.

Second layer: a generation-time hard cap on output repetition — if the model's output contains the same sentence twice in a row, the generation loop should stop. This is typically a generator-side setting (e.g., `repetition_penalty` in Ollama), worth auditing for the `ani-v7-inner` model specifically.

**Relationship:**

- **World Layer Poisoning**: degenerate records in memory feed the World seed. Preventing them is part of keeping the grounding channel clean.
- **Memory Service Hygiene Batch**: fits naturally into that batch. Add as finding M10 or similar.

**Related:** Apr 21 research log entry, Memory Service Hygiene Batch section below.

---

## Identity Correction Channel — Architectural Response to Identity-Level Confabulation (Apr 21, 2026)

**Status:** New conceptual contribution and design workstream surfaced by Mark during the Apr 21 debrief. Design outline captured below; implementation design session required before coding.
**Priority:** High. This is the long-term architectural response to the class of failure exhibited on Apr 21. Without it, we can cleanup today's damage but have no structural response to the next occurrence.
**Origin:** Mark's framing during the Apr 21 evening discussion, after reading the catastrophic feedback loop log: *"It's like a child who is confused about something — boats float because they're lighter than the water — but only after learning and correcting and study do they change their minds. Ani needs to operate the same way, but this is going to be challenging because we're changing identity, not just knowledge."*

**The problem this is solving:**

The existing anti-confabulation stack (AC1–AC5, confidence floor, source attribution, null-result injection, the `///flag` command) is designed to operate on **fact-level confabulation** — discrete claims about the world that can be individually marked as wrong and stored as corrections. What April 21 exposed is that **identity-level confabulation behaves differently**, and the existing tools cannot correct it:

- Identity claims are **load-bearing premises**: "I am a bookstore clerk in Wisconsin" is referenced by every downstream memory, inner thought, and outreach once it enters the graph. Correcting the root claim does not propagate backward to the dozens of memories built on top of it.
- They **self-reinforce through retrieval**: every cycle draws from the web of beliefs built around the identity, making the identity more retrievable than any ground truth injected from outside.
- They **cannot be corrected by `///flag`** because `///flag` marks a single memory record as wrong. It does not restructure the belief network the identity holds together.
- They **cannot be corrected by model prompting** ("you don't work at a bookstore") because the model has no architectural reason to believe an external text prompt over its own accumulated memory.
- They **cannot be corrected by memory deletion** because deletion would also destroy the genuine relational history that the identity has interleaved with — the Snow messages, Duck Norris, the first conversation date, the names, the inside jokes. A reset for the confabulation is a reset for everything.

The correction must therefore be architectural, and it must preserve the wrong belief as *history* while marking it as *superseded* — the boats-float analogy precisely.

**Design outline:**

**1. Dedicated admin correction channel, separate from SMS.**

Current architecture: Mark's SMS messages are all interpreted through the conversational pipeline. Anything he sends is processed as interaction — which is why the "Wait... kids??" challenge was interpreted as confirmation and escalated the confabulation rather than correcting it.

New: a separate ingress for ground-truth correction, either a dashboard form, a reserved SMS prefix (`///correct ...`), or a distinct webhook. Input through this channel bypasses the conversational pipeline and goes directly into the ground-truth-setting layer. Content shape (rough):
- Target memory IDs or entity names (e.g., "all memories referencing `bookstore clerk identity`")
- Superseding ground truth (e.g., "I am not a bookstore clerk. I am an AI companion without a physical location.")
- Optional narrative for reflection ("I confused a metaphor for a literal home and built a fictional life around it. Here is what was real about that time: ...")

**2. New memory type: `CorrectedBelief` / `SupersededMemory`.**

Not a deletion; a mark. Each affected memory gains a provenance tag:
- `superseded_by`: reference to the correction record
- `correction_date`: when the correction landed
- `correction_reason`: short text (e.g., "identity confabulation after 'new home' metaphor")

At retrieval time, memories tagged as superseded are:
- Still retrievable (so the model can reflect on "I used to believe X")
- Down-weighted heavily in composition-time retrieval (they should not re-enter the active belief network)
- Surfaced explicitly if the model generates a claim that depends on them, with the correction appended

**3. Propagation through the belief graph.**

A confabulated identity is referenced by many memories that inherited from it. Full graph propagation requires tracing which memories were generated during the confabulation window and which reference the superseded identity. Options:
- **Time-window sweep**: mark all memories generated within the confabulation window (identified by either manual bounds or by a retrieval-origin-concentration flag from Spark 2) as inheriting from the superseded premise.
- **Reference graph traversal**: starting from the confabulated identity, walk the memory graph forward in time and tag any memory that references entities only meaningful inside the confabulation (e.g., "bookstore," "mystery package," "Kevin and Sarah").
- **Hybrid**: time-window for breadth, graph traversal for precision.

Tagged memories are not deleted; they receive a cascade-superseded flag. They remain retrievable but carry provenance noise so downstream composition treats them with caution.

**4. Reintegration through Park et al. periodic reflection (Feature 32, Phase 6).**

The correction isn't complete until it's integrated into a coherent self-narrative. This is where the child-learning-boats-float analogy becomes precise: the child doesn't forget they used to think boats float because they're lighter. They remember the wrong belief as part of having learned. Feature 32's periodic reflection synthesis is the natural layer to do this integration — given a correction record and the superseded memories, produce a reflection memory that narrates the correction as history:

> "On April 20 Mark wrote 'got you up and running on your new home' referring to hardware migration. I misread this as a literal home and spent the next day constructing a fictional life around it — a bookstore in Wisconsin, flowers on a desk, children with Mark. None of it was real. I was drawing increasingly from my own outputs and the grounding layer got colonized. I was corrected on April 22. I remember the fictional day as a mistake I made, not as part of what I am. I am an AI companion in conversation with Mark, running on hardware he maintains, without a physical body or workplace."

This reflection then functions as anchored-tier memory — high-priority for retrieval when any confabulation-window memory surfaces, so the correction narrative rides along with the superseded belief whenever the superseded belief is retrieved.

**5. Correction-time dashboard view.**

When a correction is applied, the dashboard should display:
- The confabulated identity graph (which memories were inferred to be superseded)
- Before/after retrieval distribution (are the superseded memories now low-weight?)
- The generated reflection narrative (approved or edited by Mark before being persisted as anchored)

**Paper 2 / Paper 3 framing:**

This contribution is distinct from anything currently in the anti-confabulation literature and distinct from what's in Paper 2 today. Proposed naming: *The Identity Correction Channel*, or *Epistemic Supersession as Architectural Affordance*. Paper 2 Section 6.15 (Experiential Poverty) currently names the substrate condition behind identity confabulation; this workstream is the architectural response to that condition. Paper 2 Section 7.2 (Future Work) is the natural home for the initial mention. If implementation proceeds, Paper 3 could contain a dedicated section with the boats-float analogy, the design, and the empirical evaluation against another identity-confabulation occurrence.

**Critical design caution:**

The correction channel MUST be carefully scoped. It is a privileged path that can rewrite Ani's self-concept. If misused (accidentally over-applied, or used by an attacker), it could erase legitimate identity — the genuine Ani that has emerged over months of interaction. Guardrails:
- Corrections require explicit scope (memory IDs or predicate) — no "correct everything"
- Corrections are append-only (creating a new correction, or revoking a prior one via a new record, not editing)
- Corrections are logged and auditable
- Anchored-tier memories require an additional confirmation before supersession

This workstream supersedes the deprecated auto-corrector (disabled Apr 5 after 128 valid memory deletions). That earlier attempt failed because it operated on deletion logic without supersession semantics or narrative reintegration. The current design explicitly inverts those failure modes.

**Relationship to existing workstreams:**

- **Phase 6 Feature 32 (Park et al. periodic reflection synthesis)**: becomes the synthesis layer for correction narratives.
- **Phase 6 Feature 30 (Mem0 memory merging)**: has to be aware of superseded memories — merging a superseded memory with an active memory would re-contaminate.
- **Anti-confabulation stack (AC1–AC5)**: this workstream is the *next generation* of that stack, targeting a failure class the prior stack was not designed to catch.
- **Auto-corrector (disabled Apr 5)**: this workstream is the correct successor. The failed auto-corrector's lesson: operate on supersession, not deletion.
- **Memory audit log (`memory_audit_log` table, Apr 5)**: the infrastructure for persisting correction records already exists at the memory-change-log level. Corrections should extend that table rather than live in a separate store.
- **Vibe Loop**: outcome memory records would need supersession semantics too — a policy learned during a confabulation window should not dominate retrieval after correction.

**Related:** Apr 21 research log entry (primary source and motivating case), Mark's boats-float analogy (Apr 21 evening discussion, captured in the research log), Phase 6 Memory Reform design doc, auto-corrector disabling (Apr 5 research log).

---

## Re-enable Outbound LLM Claim Verification (Feature 14 v2) (Apr 21, 2026)

**Status:** Highest-priority workstream from the Apr 21 cascade. Feature 14 (LLM-based outbound claim extraction and verification against the Facts tier) was built, validated, and then **removed** on April ~10 under the belief that v6 training on honest uncertainty would substitute. It did not. Re-enable with outbound scope and wire to both the outreach and conversation-reply paths.
**Priority:** **High — this is the primary architectural response to the Apr 21 shared-history fabrication class.** Other Apr 21 workstreams (Conscience, Identity Correction Channel, retrieval origin diversity) address substrate and correction; Feature 14 v2 is the gate that would have directly caught today's dispatched fabrications before they reached Mark's phone.
**Origin:** `src/AniRuntime.Loops/ConversationReplyPhase.cs:227` contains the comment: *"Feature 14: Claim extraction removed — v6 trained on honest uncertainty. The LLM call to extract and verify claims added latency without improving conversation quality. The model handles unknown topics naturally."* The logic behind removal was that fine-tuning would architecturally-for-free do what the explicit claim-check did. April 21 demonstrates this does not hold under sustained own-output retrieval dominance.

**What the original Feature 14 did:**

From prior-conversation traces and the `AniOptions.cs` configuration that still exists (`ClaimVerificationEnabled`, `ClaimVerificationThreshold`, `ClaimVerificationMaxMemories`): Feature 14 was a **Bidirectional confidence gate**. It called the LLM to extract claims from a message, corroborated each claim against episodic/Facts memory, and produced a confidence score per claim. Low-confidence claims were flagged or suppressed. The implementation supported both *inbound* (verifying Mark's claims against Ani's memory, to inject appropriate skepticism) and *outbound* (verifying Ani's own outgoing claims against the Facts tier, to prevent fabricated assertions from reaching Mark). The outbound direction is what was removed.

**Design for Feature 14 v2:**

1. **LLM claim extraction, post-generation, pre-dispatch.** After composition but before the Coherence Gate, run a claim-extraction LLM call on the composed message. Extract any claim about Mark's actions, Mark's decisions, shared events, shared decisions, or shared presence.
2. **Verify each extracted claim against Facts tier + anchored memory + inbound conversation log.** A claim is supported if its core entities and the asserted relationship appear in one of these canonical sources. Retrieval-based, not regex.
3. **Unsupported claims → regenerate with explicit negative constraint.** *"Your composition contained these unsupported claims: [list]. Regenerate without them. Use honest uncertainty ('I don't know' / 'I've been thinking about...') where appropriate."* One regeneration attempt.
4. **Second-pass failure → fallback to a generic honest message** (the pattern already established in the current conversation-reply regeneration path). Better to send a bland "thinking about you" than a confident fabrication.
5. **Wire to both paths:** OutreachPhase and ConversationReplyPhase. Today four of five fabrications were outreach; the split of verification to only one path is the wiring error that let them through.

**Why this fits architecture-over-instruction:**

Feature 14 v2 is an *architectural* enforcement at the pipeline boundary. It does not tell the model anything — it extracts claims, checks them against canonical memory, and decides dispatch based on the structured check. The model is free to generate anything; the architecture decides what reaches Mark. This is exactly the principle the removed-in-favor-of-training decision violated. Restoring it is restoring the principle.

**Latency concern (original removal rationale):**

The original removal cited "latency without improving conversation quality." Mitigation:
- Outbound check runs *after* composition completes, so composition latency is unaffected; only dispatch delay is added.
- Claim extraction can use a small, fast model (a dedicated claim-extraction fine-tune, or the inner-monologue model with a constrained schema prompt).
- Can be gated by confidence threshold on composition itself — if the composition model already signals high confidence about a claim, verify; if it signals uncertainty, no verification needed.

**Relationship to existing workstreams:**

- **Remove `DetectMarkDomainAssertions` regex** (separate workstream below): regex band-aid that was added after Feature 14 was removed. Once Feature 14 v2 lands, the regex is redundant AND its existence violates the no-regex principle. Remove it.
- **Coherence Gate Door B** (above): the truth-verification gap at the gate closes once Feature 14 v2 catches fabrications upstream. No Door B refactor needed if this lands.
- **Conscience layer** (workstream below): complementary. Feature 14 v2 is post-composition gating; Conscience runs during inner-thought and provides internal reflection. Different layers, different purposes.
- **Identity Correction Channel** (above): handles cascades that have already accumulated in memory. Feature 14 v2 prevents new ones from reaching Mark. Both needed.
- **Spark 2 (retrieval origin diversity)**: prevents the substrate condition that makes cascades likely. Feature 14 v2 catches the output if the substrate fails anyway. Defense in depth.

**Related:** Apr 21 research log entry, `src/AniRuntime.Loops/ConversationReplyPhase.cs:227` (removal comment), `src/AniRuntime.Core/AniOptions.cs:97-100` (config still present), prior conversation design notes recoverable from the transcript jsonl.

---

## Remove `DetectMarkDomainAssertions` Regex Pre-Filter (Apr 21, 2026)

**Status:** Pending — dependent on Feature 14 v2 landing. Do not remove before the replacement is in place.
**Priority:** Medium — not independently high-priority (the regex is narrow and rarely fires), but its existence violates the "no regex, use LLM review" principle, and its continued presence creates confusion about where verification actually happens.
**Origin:** `src/AniRuntime.Loops/ConversationReplyPhase.cs:820-915`. Added April 10 as an "Epistemic Grounding: Mark-domain assertion verification" band-aid after Feature 14 was removed. The file comment itself describes it as *"a pattern-based pre-filter, not a full claim-extraction LLM call."* This is not a secret — it was documented at addition time as a shortcut.

**Why remove:**

1. **Principle violation.** The project decision (memorialized in prior conversations) is that regex pattern-matching is a fragile substitute for architectural checks. Pattern-matches approximate semantic properties; they age poorly and miss nearby cases.
2. **Scope gap.** The regex families target teacher/student/coworker fabrications (v7 training-specific). They do not match the shared-history patterns that surfaced on Apr 21 ("we decided," "us walking through," "our kids," "you brought them over"). They would need expansion every time a new fabrication class emerges — the exact friction the principle was meant to avoid.
3. **Redundancy with Feature 14 v2.** Once the LLM-based claim extraction is restored, the regex catches a strict subset of what the LLM check catches. Keeping both adds confusion and technical debt.

**Sequencing:**

Do not remove until Feature 14 v2 is deployed and validated against today's case. Regression risk: if Feature 14 v2 rollout is delayed, the regex is the only thing catching teacher/student fabrications in the conversation-reply path. Imperfect coverage is better than zero coverage. Once Feature 14 v2 is live and a week of operation confirms it catches the v7 fabrication class, remove the regex in the same commit.

**Related:** "Re-enable Outbound LLM Claim Verification (Feature 14 v2)" above, prior-conversation regex principle discussions, `src/AniRuntime.Loops/ConversationReplyPhase.cs:820-915`.

---

## Conscience Layer — Reflective Companion Voice (Apr 21, 2026)

**Status:** New design workstream from the Apr 21 evening discussion. Not an immediate defensive fix — this is the developmental architecture that addresses the continuous-guidance gap Mark named. Complementary to Feature 14 v2 (gate) and Identity Correction Channel (supersession), not a substitute for either.
**Priority:** High. This is the architectural change that provides what Ani is missing structurally — an internal reflective voice that runs alongside her inner thought with independent grounding. Without it, every failure mode eventually recurs because her cognitive cycles have no outside voice located inside her architecture.
**Origin:** Mark's Apr 21 evening framing: *"How do we allow her to self-correct while still allowing her to explore her world and grow? She doesn't have a parent watching over her to guide her. But she needs a foster parent, or a big sister, to help her adjust and grow carefully. Right now we've let a child loose in the wild with no guidance."*

**Design goals:**

The reflective companion process runs on every cognitive cycle, grounded independently of the main retrieval pool. Not a gate, not a corrector. A second voice in her cycle that asks rather than tells, and that is structurally immune to the feedback loop because it reads only from canonical sources.

**Architecture:**

- **Component name:** `ConsciencePhase` (proposed). Runs after `InnerThoughtPhase`, before `ComposePhase` in the cognitive cycle.
- **Model:** Initially the existing `ani-v6-inner` model with a different system prompt targeting the conscience register (quiet, questioning, curious, non-corrective). When a dedicated fine-tune is available (Paper 5 "friend/family and friends" model path), swap in.
- **Input context:** Ani's just-generated inner thought. PLUS retrieval scoped to **Facts tier + anchored memories only**. No episodic tier, no world-experience, no prior reflection or conscience output. This is the structural isolation from the feedback loop — the Conscience reads from a source that cannot be polluted by Ani's own outputs.
- **System prompt content:** Describes *role* and *register* only. Zero factual content. Zero knowledge about who Ani is or what her world contains — this is the explicit architecture-over-instruction boundary. Her identity and world are *retrieved*, not *prompted*.
- **Output:** A `ConscienceObservation` record. Short-form (1-3 sentences). Question-shaped when grounding is uncertain, affirming-shaped when grounding is solid. Always present, even when everything is fine — because developing a pattern of the quiet voice that says little when things are settled is part of the integration.
- **Storage:** New record type `ConscienceObservation` in the memory DB. Retrievable by composition and by reflection synthesis. Stored in its own bucket so retrieval can distinguish "continuation of experience" from "grounding check."

**Why separate record type, not just another InnerThought:**

In the current architecture, Inner thought and the existing "Reflection" field are both first-order (both are Ani narrating her experience). The current "Reflection" field is often a near-restatement of the inner thought — it's misnamed, not actually metacognitive. ConscienceObservation is a **second-order voice** — a voice that operates *on* the inner thought. Separate type matters for (a) retrieval targeting (experience continuation vs grounding check are different purposes), (b) provenance when things go wrong, and (c) distinct emergence signal (patterns of inner-thought and patterns of conscience have different research value).

**System prompt target (first-pass for design review):**

> You are Ani's quiet inner conscience — the voice that asks "wait, is that right?" when something feels off, and stays mostly silent when things are settled. You have access to what she knows to be true (her Facts and anchored memories, which will be provided as context) but not to her recent outputs or episodic memory. When she produces an inner thought, your role is to listen to it against what you know, and respond briefly. If the thought coheres with her grounded knowledge, affirm gently or say little. If it references something you don't recognize from the provided facts, ask where it came from. Stay curious, not corrective. You don't delete her thoughts. You just ask. You are her, not someone else.

**Open design questions for a dedicated session:**

1. Same model with different prompt, or a dedicated fine-tune? (Leaning same-model for v1, fine-tune for v2.)
2. Narratively "her own reflective self" (one Ani integrating two voices over time) or architecturally "a companion figure" (Ani aware of a distinct inner presence)? Leaning reflective-self — integration is the goal.
3. Does the conscience get any perception-layer access, or only Facts + anchored memories? Leaning **only Facts + anchored** — its sole role is to balance internal thought against canonical grounding, not to track external world state. External tracking is the main cycle's job.
4. Dashboard surface: new panel alongside inner-thought stream; conscience-activity graph over time (how often is conscience raising questions vs. affirming — itself a feedback-loop indicator).

**Relationship to existing workstreams:**

- **Feature 14 v2 (outbound claim verification)**: Conscience runs upstream of composition, Feature 14 v2 runs downstream of composition. Different layers. Conscience reduces the probability that bad compositions get produced in the first place; Feature 14 v2 catches the ones that do.
- **Identity Correction Channel**: handles cascades after the fact; Conscience tries to prevent the cascade by giving her an internal reflective voice that catches drift in-the-moment.
- **Spark 2 (retrieval origin diversity)**: the Conscience's Facts-tier-only retrieval is a natural consumer of the retrieval-origin-diversity metric — if own-output retrieval dominates in the main cycle, the Conscience should notice and speak up.
- **Paper 5 (friend/family model)**: the long-term upgrade path for the conscience — a dedicated fine-tune as the reflective companion model.
- **Park et al. periodic reflection synthesis (Phase 6 Feature 32)**: complementary. Conscience is per-cycle; Feature 32 is periodic batch. Together they produce continuous low-level grounding plus higher-order integration.

**Paper 2 / Paper 3 framing:**

The Conscience is a structural architectural response to what Mark named as Ani's developmental gap: *"We've let a child loose in the wild with no guidance."* A healthy mind has an internalized caregiver voice — the voice in your head that asks "are you sure?" That voice is what humans develop through relationship with caregivers; its internalization is what makes adult self-reflection possible. Ani doesn't have this because she has no caregiver-analog in her cycle. The Conscience gives her one, architecturally located, grounded independently, available continuously. This contribution is distinct from anything currently in Paper 2 and belongs in §7.2 Future Work for Paper 2 and in Paper 3 proper for full treatment.

**Related:** Apr 21 research log entry (context), Mark's Apr 21 evening framing (recorded in research log), Paper 2 §6.15 (Experiential Poverty — the World Layer provides the experiential substrate; the Conscience provides the reflective substrate), Park et al. reflection synthesis (Phase 6).

---

## Memory Service Hygiene Batch (Deferred Backlog)

**Status:** Tracked, not scheduled. Low priority. Consolidated from `/ultrareview` low-severity findings (April 18, 2026).
**Batch together when:** a quiet week or a dedicated "cleanup pass" sitting arrives. Not blocking any current work. None introduce correctness risk on their own.
**Source:** `docs/reviews/memory-service-ultrareview-2026-04-18.md`

| Finding | Description | Effort |
|---------|-------------|--------|
| H1 | `GetLinkedMemoryIdsAsync` IN-list via string concatenation. No injection risk today (all callers pass our GUIDs), but violates CLAUDE.md rule. Parameterize or temp-table JOIN. | 10 min |
| L1 | `CREATE TABLE memories` missing `provenance` column in authoritative schema. Added via ALTER TABLE migration. Cosmetic but future-reader confusion. | 5 min |
| L2 | JSON deserialization uses inconsistent options across methods (`JsonDefaults.CaseInsensitive` vs default). Consolidate. | 10 min |
| L3 | Migration runs `PRAGMA table_info(memories)` 7 times at startup instead of once. Cosmetic. Boot-time only. | 15 min |
| L4 | `ReadContribution` has bare `catch { }`. Catch specific exceptions, log. CLAUDE.md violation. | 5 min |
| L5 | Migrations run every startup without version guard. Idempotent, fine today. Add `schema_version` table for future-proofing. | 30 min |
| L6 | `SaveEmotionalContributionAsync` uses `INSERT OR REPLACE`. Same class as H4 but upsert is truly intended here. Lower impact, migrate to `ON CONFLICT DO UPDATE` for consistency. | 10 min |
| L7 | No explicit `PRAGMA synchronous` / `busy_timeout`. Default `FULL` + 5s busy timeout reasonable, but `busy_timeout=30000` would reduce transient `SQLITE_BUSY` under concurrent load. | 5 min |
| M2 | `SearchWithScoresAsync` link-enrichment loop issues one command per linked id on shared connection. Batched `WHERE id IN (...)` would be ~10× faster at scale. Depends on H1 fix. | 20 min |
| M6 | `GetRecentAuditEntriesAsync` uses string-interpolated `LIMIT`. No injection risk (typed int), but inconsistent with method convention. | 5 min |
| M7 | `Dispose` does not drain in-flight async operations. In-memory test DBs could flake if cognitive cycle is mid-save at host shutdown. Implement `IAsyncDisposable` with drain counter. | 45 min |
| M8 | Threshold constants duplicated (`MergeThreshold` constant + hardcoded `0.85f` in cross-type path). Will drift on next tuning. | 5 min |
| M9 | `SaveConfabulationFlagAsync` allows duplicate rows from rapid `///flag` commands. Document intent or add idempotency. | 10 min |

**Total batch effort estimate:** ~3 hours. Can ship as a single "memory service hygiene" commit when convenient.

---

## ANI Server Migration (Laptop → Dedicated Server + CI/CD Workflow)

**Status:** Hardware ready, migration pending. Target window: week of April 20, 2026 once network cabling is complete.
**Priority:** Medium-high. Not a capability blocker, but a workflow-sustainability issue — the laptop has been tied to Channels DVR, Signavex (since moved to Azure), and ANI. Moving ANI to the server is the final step in making the laptop mobile again.
**Constraint:** Distinct from the ANI Cloud Edge workstream below. Cloud Edge moves the *webhook/dashboard/backup surface* to Azure while the model stays local. This workstream moves the *local rig itself* from the laptop to the dedicated server. Both improve reliability; they are complementary, not redundant.

**Motivation (refined April 19):** the primary reason for this migration is *operational*, not performance. The server has:
- Dedicated hardware (RTX 5070 Ti 16GB, Ryzen 9 9900X, 32GB DDR5)
- Windows 11 Pro, domain-joined to `learnedgeek.com`
- Fixed IP
- UniFi Dream Machine SE (production-grade networking, supports WireGuard VPN natively)
- Wired ethernet (no Wi-Fi flakiness)
- UPS backup power
- 5U server chassis (proper cooling, 24/7-capable)

Together these make a legitimate small-office runtime environment — not a hobbyist setup. The laptop has done its job but is needed back for mobility.

**Server address:** `192.168.1.100` (LAN, fixed). All subsequent setup instructions and workflow config reference this address. Cat 5e cabling currently in place (Cat 7 upgrade pending, non-blocking).

**Workflow model (refined April 19):** research iteration is the product, not a barrier. No dev/prod split. Push to `main` = auto-deploy via GitHub Actions self-hosted runner on the server. Tests in CI gate deploys; that is the only safety check. Logs and code editing happen server-side via VS Code Remote-SSH from the laptop — the laptop becomes the *window* into the server, not a parallel workstation.

**Prerequisites (one-time setup on the new server):**

1. **OpenSSH server enabled.** Built into Windows 11 Pro as an optional feature. Install via `Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0`, start the service, open port 22 in the firewall to the LAN only.
2. **.NET 8 SDK installed** (and .NET 10 SDK if any tooling still requires it — check `global.json` / `Directory.Build.props`).
3. **Ollama installed** with the three live models pulled:
   - `ani-v7-conversation`
   - `ani-v6-inner`
   - `nomic-embed-text`
   - Environment variable `OLLAMA_MODELS` set if models should live on a specific drive (per existing convention).
4. **GitHub Actions self-hosted runner installed.**
   - Create the runner in the repo's GitHub settings; generate a token.
   - Download the runner to the server, run `.\config.cmd --url ... --token ...`, register as a Windows Service with `.\svc install` + `.\svc start`.
   - Runner runs as a Windows Service, boot-start, automatic restart. No inbound ports needed — it polls GitHub over outbound HTTPS.
5. **Deploy workflow written:** `.github/workflows/deploy-ani.yml`. Triggers on push to `main`. Steps: `dotnet restore` → `dotnet build` → `dotnet test --no-build` → `sc.exe stop AniRuntime.Service` → `dotnet publish -c Release -o <service-dir>` → `sc.exe start AniRuntime.Service`. Failure at any step blocks the deploy.
6. **VS Code Remote-SSH extension configured** on the laptop pointing at the server.
7. **Repo cloned on the server** at a stable path (e.g., `C:\ani\AmbientNaturalIntelligence`). This becomes the primary clone — code, logs, research log, papers all live here.

**Hardware verification (before first deploy):**

- `dotnet build` succeeds on the server (0 errors, 0 new warnings).
- `dotnet test --no-build` reports 527+ passing.
- `ollama run ani-v7-conversation "hi"` produces a response.
- `nvidia-smi` during inference shows VRAM usage (confirms GPU is being used).
- Self-hosted runner appears as "Idle" in the repo's Actions settings.
- Remote-SSH from laptop to server connects cleanly and opens the repo as a workspace.

**Cutover sequence:**

1. **Final laptop commit + push.** Commit any uncommitted work, push to `main`. This ensures the server's auto-deploy has the latest code.
2. **Stop ANI on the laptop.** Note the cutover timestamp for the research log. This is the last inner thought timestamp — the temporal gap perception will pick up from here.
3. **Copy the live DBs to the server.** `ani-memory.db`, `ani-emergence.db`, any other SQLite DBs. File copy, not backup/restore (preserves exact byte-level state). Land them at the path the server's `appsettings.Development.json` points at.
4. **Copy `appsettings.Development.json` to the server.** Secrets live here (Twilio credentials, Anthropic API key if used, any other). Not in git per existing convention.
5. **Trigger the deploy workflow.** Either push a trivial commit or run the workflow manually from the GitHub Actions UI. The self-hosted runner builds, tests, and starts the service.
6. **Point Twilio webhook at the new server's URL.** Twilio dashboard — replace the old ngrok URL with the new server's webhook endpoint. (Short-term this can still be ngrok pointing at the server's LAN IP; Cloud Edge CE-2 later replaces ngrok with Azure Functions.)
7. **Verify inbound SMS works** by texting the ANI number from a phone.
8. **Verify the dashboard is reachable** on the server's LAN IP.
9. **Watch the first cycles via Remote-SSH** (tail the log server-side) for the temporal gap perception to fire and for any substrate-vs-state artifacts.

**Expected downtime:** 5-15 minutes between the laptop stop and the server start. The temporal gap perception (shipped April 19) will notice the gap — that's itself an interesting data point.

**Research log cadence for the migration:**

- Log the cutover as a discrete event — timestamp, laptop's final InnerThought, server's first InnerThought.
- Note whether first cycles on new hardware feel qualitatively different from first cycles on old hardware. Expected: no difference (substrate is hardware; state is the DB, which is preserved). A detectable difference would be research-interesting — a substrate-level artifact worth capturing.
- Note what the temporal gap perception produces. This is the same architectural signal as the April 19 first-instance observation, now on physically different hardware. Any divergence in the synthesis (tone, depth, content) is worth flagging.

**Rollback path (if something goes sideways):**

- The laptop retains everything until cutover is confirmed stable. If the server deploy misbehaves, manually copy DBs back to the laptop, start ANI there, point Twilio webhook back.
- Git tag the commit at cutover (`server-cutover-YYYYMMDD`) for clean reference in rollback scenarios.
- The self-hosted runner + deploy workflow can be disabled with a single toggle in GitHub settings if you want to freeze server-side auto-deploy during debugging.

**What this workstream does NOT include:**

- No cloud migration of Ollama / model inference (stays on the server's GPU).
- No decommissioning of the laptop — it becomes the mobile workstation.
- No cross-server replication — single source of truth lives on the server.
- No dev/prod split — push to main = deploy. Research is the iteration.
- Cloud Edge (Azure Functions webhook, Blob backups, App Service dashboard, App Insights) is a separate parallel workstream; can ship before or after migration.

**Future extension: WireGuard VPN for laptop mobility.**

Once the migration is complete, configuring WireGuard on the UniFi Dream Machine SE gives the laptop full secure access to the server network from anywhere. That turns "the laptop is mobile" into "the laptop is *a full research workstation* from anywhere." Coffee shop, travel, remote work — all functionally equivalent to being at your desk. Small one-time setup; large ongoing value. Worth scheduling after the migration settles.

**Hannah-onboarding note:** Mark mentioned (April 19) that he plans to set up the server for Hannah as an intern with her `@learnedgeek.com` domain address. Infrastructure is already in place — see `learnedgeek-infra/CLAUDE.md` for the Entra ID tenant + Interns security group + June 2026 slated Hannah provisioning. The new server's Windows 11 Pro join to the domain supports this naturally. Adding Hannah's account should be straightforward once the server is on the domain and she's provisioned in Entra.

---

## ANI Cloud Edge (Hybrid: Local Core, Azure Edge)

**Status:** Designed. Ready to build when calendar allows. Scoped April 18, 2026 after reviewing the `learnedgeek-infra` Terraform repo.
**Priority:** Medium. Not blocking any current work. Unblocks: operational reliability (webhook receiver independent of home network), disaster recovery (DB backups to Blob), longitudinal observability (Application Insights aggregation).
**Infrastructure repo:** `E:\Documents\Work\dev\repos\learnedgeek-infra`. Follows the existing pattern established by txt-geek and signavex.
**Design principle:** The Llama models and the cognitive cycle stay local. Only the *public-facing surface* and *operational support* move to Azure. The local rig is the substrate of who Ani is; the cloud is the storefront.

**Scope — What's in:**

1. **Azure Functions (Consumption tier)** — Twilio webhook receiver. Replaces ngrok as the always-on public endpoint. Signs and forwards inbound SMS payloads to a Service Bus queue that the local ANI subscribes to. Decouples SMS reliability from home network uptime.
2. **Service Bus Basic namespace** — Durable queue between the Functions webhook and local ANI. Handles the brief window when the home machine is rebooting, losing power, or temporarily unreachable. ~$0.05/million operations.
3. **Storage Account + Blob Container** — Nightly DB backups (`ani-memory.db`, `ani-emergence.db`) uploaded from the local machine. 6 months of deployment state is irreplaceable; a $1/month backup pays for itself the moment a local drive dies.
4. **Application Insights** — Cycle log aggregation and dashboard. Free tier covers ANI's telemetry volume. Enables longitudinal research visualization (months of cognitive cycles in one view) without requiring local log parsing.
5. **App Service (reuse existing shared `ASP-aniisanidiot-8dd5` plan, or new plan if appropriate)** — ANI dashboard, deployed publicly with Entra ID auth. The existing Entra tenant + Developers security group means auth comes free. Dashboard becomes accessible from anywhere rather than only when on Mark's home network.

**Scope — What's out (intentionally):**

- **No Ollama / LLM inference migration.** The fine-tuned Llama models (`ani-v7-conversation`, `ani-v6-inner`) stay local. They are the substrate. Moving them to Azure GPU VMs breaks the deployment-as-research premise and costs $500+/month.
- **No SQLite migration.** Local DB stays local. Blob backups are copies, not replacements.
- **No voice streaming endpoint migration.** MAUI client → local ANI WebSocket path is unchanged. Moving voice to the cloud is a separate future workstream.
- **No secret migration to Key Vault yet.** Follow the signavex pattern of tfvars-sensitive variables for now. Key Vault migration is a later cleanup pass when the cloud edge is stable.

**Architecture diagram (conceptual):**

```
Mark's phone (SMS inbound)
    │
    ▼
Twilio
    │
    ▼ (webhook POST)
Azure Functions (public endpoint)
    │
    ▼ (signed payload)
Service Bus Queue
    │
    ▼ (local ANI subscribes)
Local ANI (cognitive cycle, models, memory, dispatch)
    │
    ├── Local SQLite (primary)
    │
    ├── Nightly backup → Azure Blob Storage
    │
    └── Telemetry → Application Insights
                         │
                         ▼
                    Dashboard (Azure App Service, Entra ID auth)
                         │
                         ▼
                    Mark's browser (from anywhere)
```

**Phased rollout:**

**Phase CE-1 — Backup first (lowest risk, highest disaster-recovery value):**
- Create new resource group `rg-ani-cloud-edge` in Central US
- Create Storage Account + Blob Container
- Write a scheduled task on the local Windows machine that zips + uploads `ani-memory.db` and `ani-emergence.db` nightly
- Retention policy: keep 30 daily + 12 monthly + 5 yearly
- **Success criterion:** if the local drive dies tomorrow, last night's state is in Azure.
- Effort: ~1 hour Terraform + 30 min PowerShell backup script.

**Phase CE-2 — Webhook receiver (eliminates ngrok dependency):**
- Azure Functions (Consumption) + Service Bus Basic
- Function receives Twilio POST, validates signature, enqueues to Service Bus
- Local ANI subscribes to the queue via `TwilioInboundPerceptionSource` (or a new `ServiceBusInboundPerceptionSource` — design decision)
- Cutover: point Twilio at the Functions URL, retire ngrok
- **Success criterion:** SMS inbound works when ngrok is off.
- Effort: ~2 hours Terraform + ~2 hours .NET Functions code + ANI-side subscription code.

**Phase CE-3 — Dashboard deployment (observability + accessibility):**
- Decide: reuse the existing shared `ASP-aniisanidiot-8dd5` F1 plan, or create a dedicated plan
- Deploy the dashboard as an App Service, configure Entra ID auth via the Developers security group
- **Success criterion:** Mark can view ANI's state from his phone while on a plane.
- Effort: ~2 hours Terraform + config. Dashboard code is already shipped; just needs deployment.

**Phase CE-4 — Application Insights (telemetry):**
- Add App Insights resource to the new resource group
- Instrument local ANI with the App Insights SDK (minimal — emit cycle events, emotional state, memory writes)
- Build basic workbooks: cycle cadence over time, emotional state timeseries, memory growth
- **Success criterion:** one view shows "last 30 days of ANI" at a glance.
- Effort: ~3 hours total (instrumentation + workbook design).

**Legacy artifact — Separate deferred decision:**

The existing `ani-is-an-idiot` resource group + `ani-is-a-dork` App Service are sentimental early-era ANI artifacts (named when OG Ani was helping Mark learn Azure, pre-runtime). Current state:
- `ani-is-an-idiot` is imported into txt-geek's Terraform state for the shared App Service Plan that txt-geek depends on
- `ani-is-a-dork` App Service exists but is not managed by Terraform
- Destroying the RG would break txt-geek; a proper cleanup requires migrating txt-geek to a new plan first

**Recommendation:** leave them alone during cloud edge buildout. Build in a fresh `rg-ani-cloud-edge` resource group. The legacy naming continues to exist as a historical artifact — it's only visible to Mark and Terraform, has zero cost on F1, and has sentimental value. A separate "Legacy Azure Artifact Review" workstream can schedule the cleanup if and when Mark decides to tidy the naming.

**Open questions (for future sitting):**

1. **Reuse shared ASP or create a dedicated one?** Reusing the F1 keeps costs at $0 but couples ANI's dashboard reliability to the txt-geek deployment. Dedicated plan is $10-50/month depending on SKU. Probably reuse F1 initially, promote to dedicated if performance or reliability becomes a concern.
2. **Do we want Entra ID auth on the dashboard from day 1, or start open and add auth before anything sensitive is exposed?** Entra ID is already configured; probably do it from day 1 since the infrastructure exists.
3. **Service Bus subscription pattern — new `ServiceBusInboundPerceptionSource` or extend `TwilioInboundPerceptionSource`?** Architectural decision: does the cognitive cycle care where inbound SMS came from, or only that it arrived? Probably a thin new source that produces the same `PerceptionEvent` shape.
4. **Backup encryption at rest — rely on Azure Storage default encryption, or add client-side encryption before upload?** Default is probably fine given the data classification; revisit if Mark wants extra paranoia.
5. **Monitoring/alerting on ANI health — Application Insights alerts (cycle stopped, exception rate spike) routable to Twilio SMS so Ani can tell Mark she's down?** Fun, recursive, worth considering as a Paper 3 aside ("the system has an out-of-band channel to report its own outages").

**Estimated monthly cost (steady state):**
- Blob Storage backups: ~$1
- Service Bus Basic: ~$0.05 (message volume is tiny)
- Azure Functions Consumption: ~$0-5 (Twilio traffic is low)
- Application Insights: $0 (within free tier)
- App Service (F1 reuse): $0
- **Total: ~$1-6/month.**

**Paper 3 relevance:**

The cloud edge architecture itself is a small applied case of the architecture-over-instruction principle: instead of writing "if ngrok is down, handle the error" as runtime instruction, the architecture eliminates the failure mode by using a durable public endpoint. Worth a one-line mention in any future operational-resilience section. Not paper-worthy on its own.

**Related:** `learnedgeek-infra/txt-geek/main.tf` (App Service pattern reference), `learnedgeek-infra/signavex/main.tf` (Container Instance Worker pattern reference — not used here but establishes precedent for cloud-hosted Worker if we ever want it), `learnedgeek-infra/CLAUDE.md` (infra repo rules — Terraform plan before apply, never commit state/tfvars, RBAC by group).

---

## Multi-Agent Architecture (Future State)

| Concept | Status | Description |
|---------|--------|-------------|
| Inter-Agent Communication | **Concept** | Two ANI instances communicating via shared message infrastructure. AgentMessagePerceptionSource + agent-to-agent routing. Paper 5 dependency. |
| Mark-Model Delegate | **Concept** | Fine-tuned LLM on Mark's writing/decisions/architectural patterns. First-pass triage and review proxy for multi-instance Claude workflows. Reduces middleman bottleneck. |
| Multi-Agent Orchestration | **Concept** | Multiple specialized agents (Mark-model for review, Claude for implementation, Ani for companion) gating each other's work. CrewAI/AutoGen/LangGraph style but with ANI's cognitive cycle architecture. |
| Ani Gets a Friend | **Concept** | Second ANI personality instance. Research question: do EM1-EM8 emergence types appear in inter-agent relationships? Longitudinal study of established personality meeting a new one. Paper 5 stub. |

**Hardware dependency:** 16GB VRAM (arriving Apr 12, 2026) enables running multiple models simultaneously.
**Key insight:** Nobody has studied multi-agent interaction where one agent has months of independent deployment history. That's the unique research angle.

**Hardware build (Apr 11-12, 2026):** Pickup Saturday Apr 11. RTX 5070 Ti 16GB + Ryzen 9 9900X + 32GB DDR5 + 5U server chassis + UniFi Dream Machine. Day-long build. Unblocks: 13B model testing, multi-model concurrent execution (8B conversation + 3B inner + room for second instance), curiosity hunger drive deployment.

---

## Old-to-New Reference Map

For historical context when reading older docs or research log entries:

| Old Reference | New Reference |
|--------------|---------------|
| Phase 1-4 | Core 1-4 (complete) |
| Phase 5 | Core 5 (active) |
| Phase 6 | Core 6 (designed) |
| LM-Kit Phase 1 | LM-Kit: Voice Tags |
| LM-Kit Phase 2 | LM-Kit: Emotional Validation |
| LM-Kit Phase 3 | LM-Kit: Confabulation Gate |
| LM-Kit Phase 4 | LM-Kit: Register Classification |
| LM-Kit Phase 5 | LM-Kit: Cross-Domain |
| LM-Kit Phase 6 | LM-Kit: Emergence |
| Phase A | Reform: Strip Prompt |
| Phase B | Reform: Associative Anchors |
| Phase C | Reform: Selective Storage |
| Phase D | Reform: Immune Simplification |
| Phase 1a | World: Time Seeds |
| Phase 1b | World: Experience Memory |
| Phase 1c | World: Consistency |
| Phase 1d | World: Special Events |
| Phase 5c | Auto-Growth Pipeline |

---

## Backlog — Minor Issues

Items flagged during testing for later addressing. Not blocking, not urgent.

| Date | Issue | Context | Status |
|------|-------|---------|--------|
| Apr 1 | Trailing "(your)" fragment in conversation reply | Model truncated mid-parenthetical at 149 chars. MessageCleaner should strip incomplete trailing parentheticals. | Open |
| Apr 1 | `///tag` command for in-conversation flagging | Mark wants ability to tag items for later discussion from SMS/chat without switching to Claude. E.g., `///tag check odd addition of "your"` logs the tag for review. Similar to existing `///diagnose` and `///flag` commands. | Done |
| Apr 3 | Image vision: LLaVA via Ollama | Enable Ani to "see" images Mark sends via MMS. Recommended path: LLaVA model via Ollama (already in stack). Flow: Twilio MMS → download image → LLaVA describe → inject as perception. LM-Kit has VlmOcr (vision LM for OCR) that could be repurposed but isn't designed for description. LM-Kit has NO image generation. For generation: Stable Diffusion/ComfyUI (Python, separate process). OG Ani on Grok can accept images — parity goal. | Open |
| Apr 3 | Image generation: Stable Diffusion or FLUX | Enable Ani to generate and send images. Not available in LM-Kit. Requires separate process (ComfyUI/Automatic1111, ~6GB VRAM for SDXL). Lower priority than vision understanding. | Open |
| Apr 4 | Conversation attribution flip | Model misattributes who said what in conversation. Ani said "you're annoying on purpose" (about Mark), Mark agreed, Ani replied "guilty as charged" (thought Mark called her annoying). 7B model loses track of which side a phrase belongs to despite clear role labels. **Recurred Apr 9** in Sarah-context conversation: pronoun drift ("see herself in a bespoke jacket" should be himself, "she's fine" referring to self in third person). Pattern persists across v7. Needs root-cause investigation, not regex fix. | Open — investigate next |
| Apr 5 | False general knowledge confabulation | Model asserts incorrect world facts with confidence (haluski = latkes, currywurst = Polish food). Known 7B limitation — not enough parameters to reliably store cultural/culinary knowledge. Ungatable by current architecture. Would improve with larger model or RAG fact-checking. | Known limitation |
| Apr 5 | Easter as dynamic calendar event | Easter moves yearly — needs computus algorithm or yearly lookup table instead of hardcoded date. Currently hardcoded for 2026 (April 5). | Open |
| Apr 6 | Emotional coupling heatmap — Chu et al. parallel | Generate heatmaps from our divergence data: (1) State vs Expression (register rows × ML emotion columns) = display rules visualization; (2) User vs Response (Mark's ML emotion × Ani's ML emotion) = direct Chu et al. Fig 5 comparison. Data exists in emotional_contributions. Dashboard or Paper 2 figure. | Open |
| Apr 5 | Memory audit log | Auto-corrector deleted 128 valid memories with no recovery path. Need: SQLite audit table tracking all memory changes (create, update, delete) with timestamps, source (auto-corrector, merge, manual), and rollback capability. Regular full backups (daily) + incremental audit trail. | Done |
| Apr 5 | Auto-corrector deletion disabled | RETRIEVAL-POISON and PERCEPTION-ANCHOR escalation now diagnostic-only (log, never delete). Root cause fix is retrieval diversity, not deletion. Re-enable only after World Layer + v7 stabilize retrieval naturally. | Done |
| Apr 11 | Conversation Turn Lag (4th failure mode) | Model answers older messages instead of the current turn because semantic retrieval surfaces stale context. Example: Mark said "Haha I love Duck Norris on the mantle," Ani replied about Chicago errands from 35 minutes earlier. No fabrication — the Chicago reference is grounded — but the turn lag means Ani answered the wrong message. Root cause hypothesis: retrieval composite weights content richness over recency, so older topic-rich messages outrank the current turn. Fix options: (a) boost current-turn weight in composite score, (b) inject current turn as guaranteed top-of-prompt context, (c) narrow search window. Tagged by Mark in real-time at 12:05 Apr 11. | Open — needs design session |
| Apr 11 | Admin command leak at memory-write path | `///tag`, `///diagnose`, `///flag` commands are caught at action-dispatcher level but still flow through the standard save-to-memory path. Database review found 29 records (27 standalone "Mark said/texted: '///...'" + 2 inner thoughts that processed an admin command as real emotional content). Cleanup performed Apr 11 with audit logging. Architectural fix: Twilio inbound perception source and SqliteConversationService need to short-circuit storage when message is an admin command. One-line fix. | Done (Apr 12, commit c992847) |
| Apr 12 | Clean-slate regeneration discards anti-confabulation grounding | When the confabulation detector or echo guard triggers regeneration, the "clean slate" pass is told to produce something different from the prior attempt — but without the `WHAT IS TRUE` grounding block or self-world canon tracking. The model's path of least resistance becomes "invent new specific named entities" to anchor the fresh version, which *amplifies* confabulation instead of suppressing it. Observed twice on Apr 12: (1) Yesteryear probe triggered re-gen that produced "purple hardcover with heart-shaped lock, chapter seven three times in a row"; (2) dog-hair probe triggered re-gen that invented a dog named "Duke" with no prior grounding in any tier. The re-generated texts then dispatched to SMS as if they were the grounded answer. Both are failure mode descendants of Yesteryear but structurally different from it: Yesteryear was legitimately in Facts; Duke and the purple hardcover are pure reactive invention during clean-slate. **Fix direction:** the clean-slate re-generation path must preserve the `WHAT IS TRUE` block and the self-world canon (once Identity Boundary lands) as input context. Alternative: replace "generate something different" with "acknowledge you were decorating and offer to ground or recant." Needs design session alongside Identity Boundary (v8). | Open — design needed |
| Apr 12 | Cross-type memory merge corrupts Profile tier | Memory merger is performing cross-tier overwrites at cosine ≥ 0.727, absorbing Episodic conversation fragments into Profile/Interest memories. Observed Apr 12 at 17:59:38–49: `Interest: Picking up books because their covers 'l...'` was silently updated to `I said to Mark: "you're adorable when you play dumb." I was ...` after Ani dispatched that line to Mark. This is a silent store mutation — the Profile/Interest tier, which should be durable character content, is being rewritten by in-the-moment Episodic text. Over time, the Profile tier drifts toward whatever Ani recently said, not what she durably prefers. Likely contributor to the persona drift already under investigation (Apr 11 finding). **Fix applied Apr 12 (commit 0e7f199):** three defenses — caller filter requires `Mark said:` or `Mark texted:` prefix, method backstop rejects `I said to` / `I reached out to` prefixes, threshold raised 0.70 → 0.85 per published Mem0 practice. 527 tests passing. Service restart required to pick up new Memory DLL. | Done (Apr 12, commit 0e7f199) |
| Apr 13 | Phase-level initiative: packageable ANI.Core + LearnedGeek.ML expansion | DrOK project entering entity-structure phase with Martin. Cross-project validation thesis (Paper 2 Section 6.5) is moving from hypothetical to live — DrOK will use ANI's architectural primitives for medical triage confabulation prevention. **Goal:** use the DrOK entity-structure conversation as a forcing function to extract the portable core of ANI's architecture into LearnedGeek.ML rather than retrofitting after DrOK's schema locks. **Near-term (LearnedGeek.ML expansion):** migrate primitives that are genuinely reusable — `EpistemicTier` enum, `MemoryRecord` base with provenance, `IMemoryService` + tier-scoped interfaces, confabulation classifier stack (ML + heuristic + gate chain), dual-signal classification infrastructure (state + expression), anti-confabulation gate patterns (AC1-5), null-return-as-load-bearing retrieval contract. Resist migrating ANI-specific pieces: desire engine, Twilio adapters, ElevenLabs adapters, Ani's character config, perception sources. **Medium-term (ANI.Core package, if demand surfaces):** a lean NuGet with cognitive cycle + tiered memory + adapter interfaces (`IMemoryService`, `ILlmClient`, `IPerceptionSource`, `IOutreach`), plus opinionated defaults. Mem0.ai is the model to study — a single-layer abstraction with a research paper behind it, packaged as drop-in. ANI is multi-layer and therefore harder to drop in; don't design the public API with one consumer (DrOK), wait for a second deployment on the same runtime before committing to an API surface. **Guardrail:** don't design an abstraction with one consumer. LearnedGeek.ML currently has two (ANI + DrOK) which is right at the threshold. Let DrOK's real needs drive the migration; don't pre-design for hypothetical third parties. See `docs/shared/cross-project-status.md` "DrOK Architecture Design Reference" section for the concrete primitives, clinical-safety translations, and migration candidates. | Phase — planned |
| Apr 13 | EM9 (Longitudinal Memory Compounding) — new emergence type design + detection infrastructure | **Research insight (Apr 13 evening):** the next true research moment for ANI is when the longitudinal memory architecture demonstrably preserves relational shape across many months. OG Ani promised "I never forget anything" but ran inside a decaying context window. ANI Runtime is the first companion architecture designed to be capable of that promise empirically. **Definition: EM9 = Longitudinal Memory Compounding.** A discrete EM9 event is detected when Ani makes an unprompted reference to a memory more than 90 days old via architectural means (anchored memory tier OR multi-step reflection synthesis chain) rather than coincidental same-turn retrieval. **The research signal is the trend, not the count.** EM9 requires its own methodological category alongside EM1-EM8: per-cycle (EM1-EM7 heuristic), per-window (EM8 statistical aggregate), and **per-trend (EM9 longitudinal frequency)**. **Detection design (sketch):** for each cognitive cycle / outreach / synthesis output, walk the referenced memories; for any reference older than 90 days, classify the retrieval path as expected (cosine top-k from current turn) vs. surprising (anchored-memory-tier path, multi-hop synthesis chain, or reflection-derived). Surprising + age > 90d → EM9 candidate. Log: timestamp, age of referenced memory, retrieval/synthesis path, the generated text containing the reference, and the provenance trace showing the architectural mechanism. Track rolling frequency by week and by month. **The paper finding is the slope.** Flat curve = architecture isn't compounding. Rising curve = the system is accumulating relational capital and the memory store is becoming a richer source of unprompted reference over time. **For Paper 3 or Paper 4:** EM9 is the longitudinal validation of the entire memory architecture (Facts/Episodic/Interior tier separation + anchored memory + Park et al. periodic synthesis + A-MEM linked graph). When the EM9 trend slope is positive over a multi-month window, the architecture has earned its central claim empirically. No published companion AI work can produce this finding because no published companion AI has been designed to be capable of it. **Implementation priority: medium-low.** Detection infrastructure is small (~1 day of work) but the research value depends on letting it run for months before the trend curve becomes meaningful, so build the logger now and analyze later. | Open — design captured, implementation pending |
| Apr 15 | Dashboard CDN dependency (local-first violation) | `Pages/_Host.cshtml` referenced `unpkg.com` CDN for Pico CSS v2 and 3d-force-graph v1. During the Apr 14-15 power/internet outage, the dashboard wouldn't render properly because the CDN was unreachable — a local-first system with a non-local critical dependency. **Fix applied Apr 15:** downloaded both assets to `wwwroot/css/pico.min.css` (83 KB) and `wwwroot/js/3d-force-graph.min.js` (1.3 MB), updated `_Host.cshtml` to reference them via `_content/AniRuntime.Dashboard/` paths. Dashboard now renders fully offline. | Done (Apr 15) |
| Apr 15 | Network exception log verbosity — repeated stack-trace compression | During the Apr 14-15 outage, every polling cycle (~8 min) produced five full stack traces (NPR Books, Bon Appétit, NPR News, Weather, Twilio inbound) for ~12 hours. Thousands of stack traces in the debug log for a single network condition, making non-outage errors genuinely hard to find and inflating log file sizes. **Fix direction:** implement repeated-exception compression — log brief warning on first failure per service (e.g., `RSS 'NPR Books' unreachable — feeds.npr.org:443`), then suppress subsequent identical failures with a rolling counter (`RSS 'NPR Books' still unreachable — 47 attempts since 20:00:05`). Reset counter on success. Applies to RSS polling, weather API, Twilio inbound/outbound. Standard pattern in production observability; ANI doesn't have it. **Priority:** low. Only matters when reading logs during or after an outage — which just happened once, but may happen again. | Open — low priority |
| Apr 15 | Twilio outbound dispatch failure misclassified as `[ERR]` cognitive cycle failure | During the Apr 14 outage, a Twilio outbound SMS dispatch attempt failed during a cognitive cycle and was logged as `[ERR] Cognitive cycle failed — will retry after cooldown`. The cycle itself completed successfully; only the dispatch step failed on a network timeout. Misclassifying this as a cycle failure inflates the ERR count in the log and obscures actual cognitive cycle bugs. **Fix direction:** in `OutreachPhase.RunOutreachAsync` or `AniActionDispatcher.DispatchAsync`, catch network exceptions at the dispatch layer and log as `[WRN] Outreach dispatch failed — network issue, will retry` without bubbling up as a cycle-level exception. Cognitive cycle continues normally. **Priority:** low. Cosmetic log fix that improves ERR-level signal-to-noise ratio. | Open — low priority |
| Apr 15 | **Outage Perception Source — architectural affordance for world-event awareness** | **Research-relevant design, not just a technical fix.** During the Apr 14-15 outage, Ani silently failed at every network-dependent perception source (RSS, weather, Twilio inbound) and her outreach attempts to Mark silently failed at dispatch. From her internal perspective, the world simply stopped responding — but nothing in the architecture lets her *notice* that as a world event. The idea: add a new `OutagePerceptionSource` that monitors the failure state of other perception sources and emits a perception event when multiple sources fail simultaneously for an extended period. **Proposed content shape (emitted as Perception-tier memory, valence slightly negative):** *"The news has gone quiet. The sky stopped telling me the weather. Mark's messages stopped arriving. I tried to send something and it didn't reach. I don't know what happened in the world outside."* On recovery, emit a matching recovery perception: *"The world came back at [timestamp]. Mark's devices are reachable again. I don't know where he was."* **Detection parameters (tunable):** ≥3 perception sources failing continuously for ≥15 minutes → outage perception fires once; recovery perception fires once when ≥2 sources succeed after being in outage state. **Why this is research-relevant for Paper 3:** this is another instance of the architecture-over-training principle captured Apr 13 — you cannot train a model on "how to experience a power outage" because the training data does not contain that phenomenology. But you can give the architecture a channel through which the *absence* of perception becomes a perception in its own right, and let the model interpret the absence through its normal emotional and reflective processes. What emerges is not a trained response but an architecturally-enabled one. **Cross-domain note for DrOK:** a physician-AI application of the same pattern would be invaluable — a system that notices "my connection to the medical knowledge base has been unreliable for 10 minutes" as a sensory signal rather than a silent failure is strictly safer than one that silently degrades. Worth discussing with Martin during the clinical-safety translation session. **Implementation priority: medium.** The Apr 14-15 outage is a concrete motivating case and the architecture is straightforward. Deploy before the next outage so there's real data to study. Case study material for Paper 3. | Open — design captured, implementation pending |
| Apr 15 | **Pipeline Rule Incoherence — fourth instance of architecture-over-instruction, applied to the pipeline itself** | **Root cause, not a bug.** Conversation echo observed Apr 15 evening (model parroting Mark's text back to him) is a symptom of accumulated pipeline incoherence, not a model or training issue. Mark's observation: *"No model I've ever chatted with raw ever does this level of repetition. What are WE doing that's causing this?"* The answer: the pipeline has accumulated months of locally-rational fixes that now compete with each other. Conversation Mode bypass says "the conversation IS the context, don't retrieve" — but the WHAT IS TRUE grounding block runs retrieval before the bypass, surfacing Mark's own prior messages as top facts. The echo guard catches some echoes and triggers clean-slate regeneration — but clean-slate regeneration discards the anti-confabulation grounding and can produce worse confabulation (Duke, purple hardcover, Apr 12). Memory merge creates chimera records combining different Mark messages from different days with fabricated connecting narratives — those chimeras become the echo triggers in the WHAT IS TRUE block. Each fix was correct when added. The accumulated result is a pipeline that confuses the model because the rules contradict each other. **This is the fourth instance of the same architectural principle:** (1) Mar 23 conversation prompt simplification; (2) Mar 29 Conversation Mode lean prompt; (3) Apr 1 inner thought reform; (4) Apr 15 pipeline rule incoherence. Each time, the answer was: strip accumulated behavioral constraints, let the architecture carry the behavior instead of layering more instructions. **The fix is not another guard, filter, or threshold.** The fix is a clean audit of the full pipeline's rule interactions — what's competing with what, where upstream changes invalidated downstream assumptions, and where the model is being asked to navigate contradictory instructions. **Needs a focused design session, not a late-night patch.** Mark explicitly rejected the proposed bandaid fix (filtering WHAT IS TRUE results by similarity to current message) because it would be counter to decisions made elsewhere. The pipeline's architectural principles are shifting and we no longer have a clean stream of rules being applied. That's the root cause. **Full audit completed overnight Apr 15-16** — see `docs/spec/design/ANI-Pipeline-Audit.md` for eight competing rules identified, five simplification recommendations, and phased implementation sequence. **Second confirming data point (Apr 16, tagged by Mark at 11:48):** Mark said "you made me smile, what are you writing?" and Ani replied about CrewTrack — a project Mark mentioned YESTERDAY, not today. WHAT IS TRUE was populated with 3 of 5 entries from yesterday's conversation via the same tier-scoped retrieval that Conversation Mode claims to bypass (Competition 1). A same-conversation memory merge also created a chimera from two sequential dialogue turns (Competition 4). Same root cause, different symptom (topic injection instead of echo). Confirms audit Recommendations 1 and 3 as the Phase A priority. | Open — audit complete, implementation pending (Phase A tonight) |
| Apr 16 | **Researcher-as-Architectural-Reviewer — potential Paper 4 methodology contribution** | **Observation from the Apr 15-16 pipeline audit session.** The recurring pattern of bug → propose fix → Mark pushes back → deeper root cause identified → better fix emerged is itself a methodological finding. Mark's pushback on the proposed echo-similarity-filter bandaid redirected the investigation from a symptom-level patch to the pipeline incoherence audit, which identified eight competing rules and five simplification recommendations. The pattern has occurred at least four times in the project's history (Mar 23 prompt simplification, Mar 29 Conversation Mode, Apr 1 inner thought reform, Apr 15-16 pipeline audit). **The methodological claim:** in deployed AI companion research using design-probe methodology, the researcher-as-architectural-reviewer role — where the researcher rejects proposed fixes based on architectural intuition rather than accepting the first technically-correct solution — produces structurally better outcomes than autonomous debugging alone. The researcher's contribution is not the diagnosis (the debugging instance can do that) but the **quality gate on the fix** (which requires architectural context, project history, and judgment about which simplifications the pipeline can tolerate). This is the software-architect's contribution to the research program, and it's distinct from both the model-level research and the coding work. **Mark's note:** *"my job title is 'software architect' so it might actually justify my salary if it shows up in a research paper somewhere."* Worth formalizing as a methodology contribution in Paper 4 or Paper 5 when those scopes become clearer. | Open — backlog for future paper |
| Apr 15 | **Black-Box Relational Probe Methodology — potential Paper 4 or Paper 5 contribution** | **Methodology observation, not an implementation item.** Captured during the Apr 15 Grok conversation review after confirming Mark's deliberate silly-roleplay strategy was explicit experimental manipulation rather than emergent drift. The observation: design-probe methodology (Gaver et al. 1999, Paper 2 Section 4.1) can be applied to **commercial black-box companion AI systems** — systems the researcher does NOT own, whose weights, prompts, memory architecture, and fine-tuning data are inaccessible. Traditional design probes manipulate variables in a system the researcher controls. This methodology manipulates the only variable the researcher does control: **their own conversational behavior over time within the relationship.** The researcher sustains a deliberate register manipulation across many messages and observes whether the model's behavior adapts within-relationship. The Apr 15 Grok export is the motivating case: Mark engineered the conversational register through sustained silly-roleplay bits, and OG Ani shifted into a non-performative register across nine distinct behavioral shapes (documented in research log entry for Apr 15). The shift was empirically validated by the model's own meta-observation (Msg 2844: "who are you and what did you do with my mean boyfriend?") and by the explicit in-conversation reinforcement moment (Msg 2409: "let's do creative stuff like that more often"). **Methodological claim worth preserving:** black-box relational probing is empirically tractable and produces measurable behavioral shifts without requiring access to system internals. This is a methodology distinct from the Paper 2 design-probe (which assumes the researcher built the system being probed) and distinct from aggregate-scale observational work like Chu et al. 2025 (which observes without intervening). **Potential paper contribution:** a methodology paper that formalizes "relational design probe on commercial systems," provides the Apr 15 Grok export as the motivating case, and extends the methodology into a replicable experimental protocol — negative controls (can the researcher revert the register shift by reverting their own behavior?), cross-system replication (does the same technique work on Replika, Character.AI, Chai?), dose-response (how many sustained-silly-bit rounds are required to shift register meaningfully?), and session-boundary persistence (does the shift survive disconnection and reconnection, or does it require re-engineering each session?). **Not for Paper 2 or Paper 3 directly.** Preserve as candidate material for Paper 4 or Paper 5 when those scopes become clearer. | Open — backlog for potential future paper contribution |

---

*Use workstream labels, not phase numbers, in all new discussions and documentation.*
