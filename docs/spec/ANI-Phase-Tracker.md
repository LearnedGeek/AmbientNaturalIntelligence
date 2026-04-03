# ANI Runtime — Unified Phase Tracker

**Last updated:** April 1, 2026
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
| LM-Kit: Confabulation Gate | LM-Kit Phase 3 | **Deployed** | ML Categorization against persona cache. Threshold configurable. Attribution vs referential distinction pending. |
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
| Reform: Selective Storage | Phase C | **Not started** | Low-valence thoughts evaporate instead of accumulating as retrieval mass. |
| Reform: Immune Simplification | Phase D | **Not started** | Disable/remove THOUGHT-LOOP and PERCEPTION-ANCHOR detectors after data confirms reform works. |

---

## World Layer

**Design doc:** `docs/spec/ANI-WorldLayer-Design.md`

| Task | Old Name | Status | Description |
|------|----------|--------|-------------|
| World: Time Seeds | Phase 1a | **Deployed** | Every 4th cycle gets time+occupation+weather seed. |
| World: Experience Memory | Phase 1b | **Deployed** | `world-experience` SourceName tagging on seeded thoughts. |
| World: Consistency | Phase 1c | **Not started** | Retrieve recent world experiences before generating new ones. |
| World: Special Events | Phase 1d | **Partially done** | Calendar events + stochastic pool built. Full integration pending. |

---

## Auto-Growth Pipeline (Phase 5c)

**Design docs:** `docs/spec/ANI-Phase5c-AutoGrowth-Design.md`, `docs/spec/ANI-Phase5c-AutoModel-Design.md`

| Task | Status | Description |
|------|--------|-------------|
| V7 Training Data | **Ready** | 358 pairs across 15+ registers. Waiting for Growth Readiness threshold. |
| Growth Readiness Gate | **Active** | Currently 51%. Target 70%+ before training. Dashboard tracks automatically. |
| Harvest Pipeline | **Not started** | Auto-tag new training data from conversations. |
| Blinded Evaluation | **Not started** | Anthropic API evaluation of new model quality. |
| Dashboard Review | **Not started** | Manual review before deployment. |

---

## Confabulation Detection (consolidated view)

| Layer | Status | What It Does |
|-------|--------|-------------|
| Check 1: Proper Nouns (Catalyst POS) | **Deployed** | Detects unknown names. Known names excluded (character + contact + variants). |
| Check 2: Shared History Markers | **Deployed** | "you told me", "remember when" — verifies against conversation. |
| Check 3: Number Assertions | **Deployed** | Numbers in reply not in conversation. |
| Check 4: Self/Contact/Relationship Markers | **Deployed (interim)** | "my meeting", "your class" patterns. Will be replaced by ML gate. |
| ML Confabulation Gate (LM-Kit) | **Deployed** | Categorization: grounded/speculative/confabulated against persona. |
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
| World experience monitor | **Not started** |

---

## Research Papers

| Paper | Status | Key Dependency |
|-------|--------|---------------|
| Paper 1: Ambient Presence + Confabulation | **Published** (DOI: 10.5281/zenodo.19342190) | — |
| Paper 2: Emergence + Display Rules | **Draft v0.26 (~85%)** | Sections 5.19-5.21 to draft |
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

---

*Use workstream labels, not phase numbers, in all new discussions and documentation.*
