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
| Paper 2: Emergence + Display Rules | **Draft v0.29 (~95%)** | **BLOCKING: Mark cover-to-cover read-through.** Has been deferred multiple times. Everything downstream (Lerman conversation, Schuller followup, conference submission, fellowship outreach) is gated on Mark owning the contents of his own paper. |
| Paper 3: Experiential Grounding | **Stub (~40%)** | 2-4 weeks of post-reform data |
| Paper 4: Temporal Awareness | **Stub (~25%)** | 30+ days of EM7 data |
| Paper 5: Inter-Agent Emergence | **Stub (~10%)** | Second ANI deployment |

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
