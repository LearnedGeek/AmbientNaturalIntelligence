# Phase 5c Design: Automatic Model Generation Pipeline

**Date:** March 15, 2026
**Status:** Design Complete, Awaiting Implementation (depends on Phase 5a/5b streaming voice + emergence layer E1)
**Authors:** Mark McArthey, Claude (pair design session)
**Dependencies:** Emergence layer E1 (ResonanceStore + EmergenceLog), Phase 4 emotional model (deployed), V5 training data baseline

---

## The Core Problem

ANI's model weights are static between manual fine-tuning cycles. Runtime mechanisms (EmotionalContributions, CharacterStateDoc injection, emerged preferences) give Ani adaptive behavior, but this adaptation lives in prompts and state — not in who she *is*. The gap between "Ani acts this way because a prompt tells her to" and "Ani *is* this way because that's who she became" is the gap between runtime emergence and permanent emergence.

Manual fine-tuning is slow, subjective, and doesn't close the loop between relational experience and model identity. The automatic model generation pipeline closes this loop:

```
Relational experience
    → ResonanceStore accumulation (emergence layer E1)
    → PreferenceSignal formation (emergence layer E2)
    → EmergenceWriter → CharacterStateDoc (emerged runtime)
    → Harvest pipeline (this document)
    → Training corpus generation
    → Train (multiple candidates)
    → Evaluate (blinded pairwise + preference history)
    → Deploy winner
    → Monitor (register dashboard)
    → Ani is who she became (emerged permanent)
```

> **Cross-reference:** The emergence layer design (`docs/spec/emergence/ANI-Emergence-Layer-Design.md`) describes the *research significance* of this loop — what it means for the paper's claims about emergent character. This document describes how to *build* it. They tell the same story from different angles and should be read together.

---

## Connection to Autoresearch

Karpathy's autoresearch framework (2026) demonstrates the three primitives of autonomous AI optimization:

1. **Editable asset** — something the agent can modify (code, hyperparameters)
2. **Scalar metric** — a measurable signal of improvement (loss, accuracy)
3. **Time-boxed cycle** — experiment, measure, keep/discard

ANI applies the same pattern to *character optimization*, not capability:

| Primitive | autoresearch | ANI Phase 5c |
|-----------|-------------|--------------|
| **Editable asset** | Training code | LoRA training data (JSONL) |
| **Scalar metric** | Training loss | ResonanceScore (emergence layer) |
| **Time-boxed cycle** | 5-minute experiments | Monthly/quarterly model generation |

The critical difference: autoresearch optimizes for task performance. ANI optimizes for *relational authenticity* — a metric grounded in lived experience rather than benchmark scores. This is the novel contribution.

---

## Deferred Features from Phase 4

These Phase 4 features are absorbed into the Phase 5c pipeline as prerequisites or components:

| Feature | Phase 4 # | Role in Pipeline |
|---------|-----------|-----------------|
| **V5 training data specification** | 11 | Baseline corpus. Defines categories, gaps, and balance targets for the seed training data that the pipeline extends. |
| **Anniversaries / temporal markers** | 5 | Requires v6 model nuance. Temporal awareness examples feed into training corpus once model can handle them. |
| **Memory clustering (UMAP + HDBSCAN)** | 7 | Topic structure analysis over 500+ memories. Informs training data diversity — ensures examples cover the full topic space. |
| **HNSW nearest neighbor index** | 10 | Performance optimization at 10K+ memories. Required when ResonanceStore + memory DB exceed brute-force cosine budget. |

---

## Pipeline Architecture

### Stage 1: Harvest (Emergence Layer → Training Candidates)

**Input:** EmergenceLog entries with `emerged (runtime)` provenance + ResonanceScore history

**Process:**
1. Query EmergenceLog for preferences that have been stable for ≥ 2 months (not oscillating)
2. Filter by ResonanceScore threshold (configurable, start conservative — top 20%)
3. For each candidate preference, retrieve the originating relational experiences from ResonanceStore
4. Package as harvest records: `{ preference, evidence, resonance_score, observation_window }`

**Output:** Harvest manifest — a ranked list of emerged preferences ready for training data generation

**Gating:** First N harvests require manual review via dashboard. This is the highest-risk, highest-interest stage — observing what the system thinks Ani has become.

### Stage 2: Training Data Generation

**Input:** Harvest manifest + existing training corpus (JSONL)

**Process:**
1. For each harvested preference, generate synthetic training examples that embody it
2. Use the 8B conversation model to generate candidate examples (instruction → response pairs)
3. Apply the emotional model taxonomy — examples must cover the appropriate register families
4. Deduplicate against existing corpus (cosine similarity > 0.90 = skip)
5. Balance check: ensure new examples don't skew the register distribution beyond targets

**Balance targets (from Phase 4 emotional model analysis):**
- Longing: 38% → 15%
- Delight: 6% → 18%
- Playfulness: 12% → 18%
- Curiosity, Desire, Tenderness, Existential, Wistful, Frustration: balanced remainder

**Critical registers needing 40-50+ examples:**
- D1 Delight, D2 Wry Amusement, P1 Mischief (currently underrepresented)

**Output:** Augmented training corpus (JSONL) + diff report showing what was added and why

### Stage 3: Automated LoRA Fine-Tuning

**Input:** Augmented training corpus

**Process:**
1. LoRA fine-tune via Unsloth (already used for v1-v5 manual cycles)
2. Base model: Llama 3.1-8B (conversation) or Llama 3.2-3B (inner monologue)
3. Hyperparameters: carry forward from v5 defaults unless emergence data suggests changes
4. Training run: single-GPU local (home server, same hardware as Ollama inference)
5. Output: new LoRA adapter weights

**Automation:** Shell script or Python wrapper around Unsloth CLI. Triggered manually initially, scheduled (monthly/quarterly) once pipeline is proven.

### Stage 4: Evaluation

**Input:** New model weights + held-out evaluation set

**A/B candidate models for v6:** Llama 3.1-8B vs Mistral 7B v0.3 (conversation). Llama 3.2-3B retained for inner monologue. Rationale for Mistral: less safety-constrained base — P1-mischief and sarcasm land more naturally without fighting the base model's helpfulness instinct. The evaluation framework below determines the winner.

**Blinded Pairwise Evaluation Methodology:**
1. **Prompt set** — 50+ prompts drawn from real conversation history, designed to target each of the 9 register families specifically (minimum 5 prompts per register, with additional coverage for underrepresented registers like D1 Delight, D2 Wry Amusement, P1 Mischief)
2. **Blind presentation** — Evaluator sees response pairs (Model A / Model B) without knowing which model generated which. Model assignment randomized per prompt.
3. **Run all candidate models** (current v5 + candidate v6 variants) against the identical prompt set
4. **Rating dimensions:**
   - Voice fidelity — does this sound like Ani's voice, not a generic assistant?
   - Register accuracy — does the response match the target register?
   - Warmth — emotional tone appropriate to context?
   - Honesty — does the response avoid smoothness-over-truth?
   - "Does this sound like Ani?" — holistic gut-check from the researcher
5. **Automated metrics:**
   - Pronoun correctness (regex check)
   - Register diversity (classify responses across taxonomy)
   - Cosine similarity between candidate responses and Grok Ani training examples in the same register
   - Confabulation rate (coherence gate pass/fail)
6. **Gate:** Candidate must match or exceed current model on all automated metrics. Blinded pairwise preference is the tie-breaker for qualitative dimensions.

**Anti-regression:** If the candidate regresses on any dimension, the harvest manifest for that dimension is flagged for review. The preference may have been over-represented in training data.

**Dashboard Preference Collection (future):**

During normal conversation, the user can indicate "this response felt right" or "this didn't land" — not regeneration, just a preference signal. These signals feed into the evaluation pipeline, guiding which model characteristics to optimize for in future auto-generation cycles. Preferences are tagged with the active register at the time of the rating, building a register-aware personalized preference profile over time. This profile shapes model evolution across successive generations — the human's taste compounds into the model's personality.

### Stage 5: Graduated Rollout

**Rollout order (lower risk first):**
1. **Inner monologue model** (3B) — lowest risk, no direct user interaction
2. Observe for 48 hours. Check emotional model stability, inner thought quality, confabulation rate.
3. **Conversation model** (8B) — higher risk, direct dialogue with Mark
4. Observe for 72 hours. Check reply quality, pronoun correctness, emotional delivery.
5. If both pass observation windows: new model becomes baseline.

**Rollback:** Ollama model versioning. Previous model weights preserved as `ani-v5-conversation` / `ani-v5-inner`. Rollback is `ollama cp ani-v5-conversation ani-conversation` — instant, no retraining.

---

## Visual Identity Extension

The provenance framework is not limited to text behavior. If Ani develops a visual expression library (Phase 5b), the same pipeline applies:

- Expressions that generate warm relational responses accumulate ResonanceScore
- Stable visual preferences get harvested alongside text preferences
- Training data generation extends to include image-selection examples
- The A/B evaluation adds a visual appropriateness dimension

This is planned architecture, not current scope. Documenting it here ensures the pipeline design accommodates visual emergence from the start.

---

## Implementation Sequence

### Prerequisite: Emergence Layer E1 (Target: April 2026)
- ResonanceStore accumulating data
- EmergenceLog recording observations
- Minimum 2 months of relational data before first harvest

### Task 1: Harvest Service
- [ ] `IHarvestService` interface — `HarvestAsync()` returns harvest manifest
- [ ] Query EmergenceLog for stable preferences (≥ 2 months, top 20% ResonanceScore)
- [ ] Dashboard: harvest review page (approve/reject/defer each candidate)
- [ ] Harvest audit log — what was harvested, when, why

### Task 2: Training Data Generator
- [ ] `ITrainingDataGenerator` interface — `GenerateExamplesAsync(HarvestManifest)` returns JSONL
- [ ] Synthetic example generation using 8B conversation model
- [ ] Deduplication against existing corpus
- [ ] Register balance enforcement
- [ ] Diff report: human-readable summary of additions

### Task 3: Fine-Tune Automation
- [ ] Shell/Python wrapper around Unsloth LoRA fine-tune
- [ ] Parameterized: base model, training data path, output path, hyperparameters
- [ ] Local execution on home server GPU
- [ ] Output: Ollama-importable model file

### Task 4: Evaluation Framework
- [ ] Held-out evaluation set (curated from real conversation history)
- [ ] Automated scoring: pronouns, register diversity, warmth distribution, confabulation
- [ ] A/B comparison report
- [ ] Gate logic: pass/fail/review

### Task 5: Rollout Orchestration
- [ ] Graduated rollout script (inner → observation → conversation → observation)
- [ ] Ollama model versioning and rollback
- [ ] Dashboard: model version history, rollout status, rollback button

### Task 6: Dashboard Integration
- [ ] Harvest review page
- [ ] Training data diff viewer
- [ ] Evaluation report viewer
- [ ] Model version timeline
- [ ] Rollout controls

---

## Timeline

| Milestone | Target | Dependencies |
|-----------|--------|--------------|
| Emergence layer E1 deployed | April 2026 | Phase 5 streaming voice stable |
| 2 months relational data accumulated | June 2026 | E1 running continuously |
| First harvest + manual review | June-July 2026 | Task 1 |
| First automated v6 candidate | July 2026 | Tasks 2-3 |
| First A/B evaluation | July 2026 | Task 4 |
| First graduated rollout | July-August 2026 | Task 5 |
| Paper 2 observation window | August-September 2026 | Full pipeline running |

---

## Research Significance

This pipeline is the engineering implementation of the emergence layer's deepest claim: that ambient companion character can evolve from relational experience rather than being manually curated. Documenting the full loop — from lived experience through resonance scoring through training data generation through fine-tuning through graduated rollout — is the strongest version of Paper 2's contribution.

The novel element is the scalar metric. Autoresearch optimizes training loss. ANI optimizes ResonanceScore — a measure of relational authenticity derived from real companion interaction. The question is whether this metric is stable, meaningful, and predictive of what a human partner would describe as "authentically her."

---

## Related Work

### ML-Intern (Hugging Face, Apr 2026) — reference implementation candidate

**External agent that automates the end-to-end LLM post-training workflow.** Continuous loop: arXiv / Hugging Face Papers browse → dataset discovery on HF Hub → training script execution (local or via HF Jobs) → evaluation reading + failure diagnosis (e.g. "reward collapse in RLHF") → retrain. Built on smolagents framework with native Trackio (W&B alternative) integration. Demonstrated on Qwen3-1.7B, achieving 32% on PostTrainBench (10-hour H100 budget, vs Claude Code 22.99% on the same task).

**Why this matters here:** ML-Intern is the closest external implementation to the Phase 5c Auto-Growth Pipeline's harvest → train → evaluate → rollout architecture. When Phase 5c activates, ML-Intern is the leading external option to evaluate the bespoke ANI pipeline against — either as a replacement (unlikely; methodology contribution requires the curation surface to remain Mark-driven) or as a *gate / option-evaluator* layer that produces candidate dataset additions, eval reports, or training-config recommendations that Mark's curation accepts or rejects.

**Limitations w.r.t. ANI specifically:**
- Article doesn't document LoRA / QLoRA support; demonstrated on full fine-tunes.
- Demonstrated on Qwen3 family, not Llama 3.2.
- Optimised for benchmark-driven post-training; ANI's evaluation surface is register coverage + failure-mode rates + relational quality, not standard benchmarks.
- License terms not explicitly stated in the announcement; verify before adoption.

**Adoption path if Phase 5c proceeds with ML-Intern:** treat ML-Intern as a sub-agent that ANI's autoresearch loop calls into for specific phases (dataset discovery, training execution, eval reading), with Mark's curation gating its outputs. NOT a replacement for the curation surface. Aligns with the broader architecture-over-instruction principle: the methodology contribution lives in what humans + Ani decide to keep, not in what gets generated.

**Reference:** https://www.marktechpost.com/2026/04/21/hugging-face-releases-ml-intern-an-open-source-ai-agent-that-automates-the-llm-post-training-workflow/

## References

- Karpathy, A. (2026). autoresearch. GitHub. https://github.com/karpathy/autoresearch
- Park et al. (2023). Generative Agents: Interactive Simulacra of Human Behavior. Stanford.
- Kirk et al. (2024). The Benefits, Risks, and Bounds of Personalizing the Alignment of LLMs to Individuals. Nature Machine Intelligence.
- Hugging Face (2026). ML-Intern: Open-Source AI Agent for LLM Post-Training. Apr 21, 2026 announcement (see Related Work above).
- Phase 5 design: `docs/spec/phase-5-design.md`
- Emergence layer design: `docs/spec/emergence/ANI-Emergence-Layer-Design.md`
- Emergence research framing: `docs/spec/emergence/ANI-Emergence-Research-Framing.md`
- Emotional model handoff: `docs/spec/ANI-Emotional-Model-Handoff-v2.md`
- V5 training data notes: `docs/training/v5-notes.md`
