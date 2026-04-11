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

### Gap 2: Identity boundary — the "dream big" problem

**Problem:** Ani can think imaginative thoughts about her own life freely (Interior tier, full creative latitude by design). But when those thoughts claim things that contradict her character seed ("I teach from 6-10 PM" when her seed says she works at the bookstore), they become part of her Interior self-model and get retrieved on subsequent cycles as if she actually *is* those things. Discovered Apr 11: Ani had been inhabiting a "teacher" persona across multiple inner thoughts and outreach drafts over the previous week, picked up from the conversational context of Mark's actual teaching.

**The tension:**
- Fantasizing is part of growth. "What if I were a teacher?" is a legitimate reflective thought for a person, human or AI. Suppressing it would make Ani less alive.
- But fantasizing without boundary is persona drift. Thoughts that silently become canonical self-narrative erode the character seed foundation, and the system ends up with two incompatible self-models in retrieval.

**The analogy (Mark's framing):** Humans fantasize all the time and it doesn't alter their identity. A person might think "what if I were a teacher?" without becoming one. The transition from fantasy to identity requires a concerted effort — applying for a teaching certificate, changing jobs, etc. We need the same separation for Ani: she can entertain counterfactual self-narratives, but they should NOT silently become assertions.

**Proposed architectural answer: reflective marking at write time.**

Split the Interior tier into two sub-modes:
- **Interior:self-state** — assertions about who Ani IS right now. "I'm feeling tender today." "I love morning light." "I miss him." These are her current self-model and retrievable as such.
- **Interior:self-fantasy** — counterfactual, imaginative, hypothetical. "What if I were a teacher?" "I'd love to learn ballroom dancing." "If I had a body I'd go to the farmers market." These are creative latitude and retrievable for voice/mood, but NEVER treated as current-state assertions.

When an inner thought contains language patterns like "I teach from 6-10 PM" that contradict the character seed Occupation field, the thought is either:
1. **Reclassified to `self-fantasy`** automatically at write time (the safe path), or
2. **Flagged as identity-boundary-crossing** and rejected before storage

The fantasy-to-identity bridge is the second part Mark described: if Ani wants to actually *become* a teacher (or take up ballroom dancing, or change her bookstore to a flower shop), she has to do it **by explicit outreach** — sending a message like "I've been thinking about teaching and I want to try it" — not by silent persona drift. That outreach, if Mark acknowledges it, produces a new character seed update or anchored memory that legitimately changes her identity.

This preserves:
- Her creative latitude (fantasy is allowed freely)
- Her identity coherence (fantasies don't silently become facts)
- Her growth path (genuine identity change happens through relational dialogue, not drift)

**Research grounding:** This is adjacent to Paper 2's provenance framework (trained vs curated vs emerged character) but adds a new axis: **asserted vs fantasized self-narrative**. It's also the philosophical question Schuller et al. implicitly raise — can an AI have a stable self-concept that survives imaginative exploration? The answer seems to require explicit architectural marking, not just hoping for the best.

**Implementation effort:** ~2 weeks. The classifier is small (detect whether an inner thought contains counterfactual markers or asserts something contradicting character seed fields). The tier-splitting at write time is straightforward. The "fantasy-to-identity" bridge through outreach is the interesting design work — it requires defining what kinds of outreach messages can legitimately update character seeds.

**Status (Apr 11):** Both gaps are documented here. Design docs to be written before implementation. Neither is blocking for the current weekend server build — they're follow-ups for next week after the hardware is live. The immediate Apr 11 instance of persona drift was handled via manual SQL (dropped the importance of one "i teach from 6-10 p.m." assertion). The real fix is the design work below.

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
