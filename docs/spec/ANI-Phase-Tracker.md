# ANI Runtime — Unified Phase Tracker

**Last updated:** April 9, 2026
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
| V8 Training Data Audit | **Not started** | Review all training source files for stage directions ([teasing-laugh]), parenthetical meta-commentary, and OG Ani artifacts that should be stripped at the source rather than caught by MessageCleaner regex post-hoc. Fix the training data, not the pipeline. |
| MessageCleaner Regex Audit | **Not started** | Review accumulated regex fixes for fragility. Many are after-the-fact patches for training data quality issues. Catalog which patterns are model artifacts (fix in training) vs runtime necessities (keep in cleaner). |
| Memory Provenance Tagging (v8) | **Not started** | Add `narrative_provenance` field to memory records: `user-asserted` (things Mark told her), `world-experience` (perception/world seeds), `ani-elaborated` (creative invention in conversation). At retrieval time, ani-elaborated memories about real-world entities (people in character seeds) must NOT be promoted to ground truth — they inform continuity but cannot become the basis for new factual claims. Discovered Apr 9 via the Sarah finding: Ani correctly retrieved a real person from character seeds, then elaborated her own life involving them, but the elaborations risk being treated as canonical facts about the real person. Architectural answer: distinguish "Ani as subject involving X" (acceptable) from "X has property Y" (only valid if user-asserted). |
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

---

*Use workstream labels, not phase numbers, in all new discussions and documentation.*
