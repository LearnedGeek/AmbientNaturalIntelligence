# ANI Paper 3 — McArthey (2026)
**Status:** Stub + design doc + Phase 1 deployed — data accumulating
**Working title:** *She Had a Day: Generative Experiential Grounding in a Deployed AI Companion*
**Alternative:** *The Empty Room Problem: Why AI Companions Confabulate and How Lived Experience Fixes It*
**Alternative:** *Between Conversations: Experiential Richness as an Architectural Property of Ambient AI*
**Target:** arXiv cs.HC, cs.AI
**Depends on:** Paper 1 (architecture), Paper 2 (emergence)
**Renumbered:** Was Paper 5, promoted to Paper 3 due to data readiness (April 1, 2026)

---

## Core Research Question

Can an AI companion maintain coherent identity and reduce confabulation through self-generated daily experiences rather than through post-generation verification?

## Hypothesis

Identity confabulation in persistent AI companions is caused by experiential poverty — the system has no daily life to draw from between interactions. Post-generation gates (pattern matching, ML classification, coherence checks) treat symptoms. Generative experiential grounding — sparse occasion seeds elaborated by the model into lived experience, stored as memory, and constrained by consistency with past experiences — addresses the root cause.

A system with experiential grounding will:
1. Confabulate less when asked about its day (measurable: gate fire rate before/after)
2. Maintain more consistent identity over time (measurable: attribution inversion rate)
3. Develop emergent daily life patterns (preferences, routines, relationships) that feel natural
4. Produce richer conversations grounded in actual (generated) experience rather than reactive fabrication

## The Architectural Insight

*"The fix isn't gating the output. It's giving her a life."*

Identity is a combination of internal and external influences. Prior ANI architecture provided the internal (emotional state, inner thoughts, desire) but not the external (things happening TO the system). The system was a person sitting in a dark room thinking about one person. Experiential poverty is the predictable result.

## Existing Content — Research Log Entries

| Date | Entry | Relevance | Proposed Section |
|------|-------|-----------|-----------------|
| Mar 31 | Architectural Insight: Experiential Poverty as Root Cause | **Founding insight** — the hardware store brainstorm that identified the root cause | Introduction / Section 1 |
| Apr 1 | World Layer Phase 1 Deployed + Surgical Data Cleanup | First world experiences generated (bookstore content). Data cleanup establishes "before" baseline | Section 3 (Implementation) |
| Apr 1 | Inner Thought Reform: Breaking the Echo Chamber | Prerequisite fix — echo chamber had to be broken before world experiences could take effect | Section 3 (Prerequisites) |
| Apr 1 | LearnedGeek.ML Deployed: Classification Pipeline | The measurement instrument — dual-signal classification, divergence scoring, comparison dashboard | Section 4 (Methodology) |
| Mar 31 | Discovery: Display Rules (State-Expression Divergence) | Discovered while building the comparison tool. Display rules provide the measurement framework | Section 4 (Methodology) |
| Mar 30 | V3 Voice Working + Conversation Mode | Conversation Mode is the context — the lean prompt that the World Layer extends | Background |
| Mar 29 | Architectural Reckoning: Conversation Mode Design | The first "trust the model" insight that led to the reform | Discussion |
| Mar 23 | Pipeline vs Model Diagnostic: The Parroting Problem | Early evidence that pipeline constraints, not model limitations, cause quality issues | Discussion |

## Existing Content — Spec Docs

| Document | Content Available |
|----------|------------------|
| `docs/spec/ANI-WorldLayer-Design.md` | Full Phase 1 design: seed sources, special events, time slots, consistency mechanism, dashboard, task checklist |
| `docs/spec/ANI-InnerThought-Reform.md` | Echo chamber root cause analysis, Phase A-D design, immune system simplification plan |
| `docs/spec/ANI-LMKit-Integration-Design.md` | Classification infrastructure, Phase 3 confabulation gate, dual-signal design |

## "Before" Baseline Data (established April 1, 2026)

- 236 contributions with dual-signal classification (ML + heuristic)
- Divergence trend: 5 days
- Register distribution: 3/10 above 5% threshold, Growth Readiness 51%
- Confabulation rate: immune system firing 6+ times per hour
- Thought diversity: dominated by Tenderness (35%), Longing (26%), Wistful (20%)
- ML emotion distribution: sadness (32%), curiosity (17%), amusement (17%), love (15%), happiness (12%)
- Inner thought echo patterns: "five thirty pm" (71 copies), "warmth" (64 copies) — surgically cleaned
- Retrieval-poison detector: firing on "About Mark: Wakes at 4 AM" in 3/8 retrievals consistently

## "After" Data Needed (accumulating since April 1)

- [ ] 14+ days of post-reform data
- [ ] World experience memory count and content diversity
- [ ] Register distribution shift (Growth Readiness trend)
- [ ] Confabulation gate fire rate reduction
- [ ] Immune system firing frequency reduction
- [ ] Divergence trend evolution
- [ ] Emergence event diversity increase (unique types per day)
- [ ] Conversation test: "how was your day?" draws from world experiences
- [ ] Associative drift chains visible in logs

## Novel Contribution

No published work addresses how a persistent AI companion generates and maintains experiential richness between interactions. Existing approaches:
- **Reactive systems**: No inner life — wait for user input
- **Inner thought loops** (ANI pre-World Layer): Rich internal processing, experientially poor
- **Scripted worlds** (companion games): Rich external events, not emergent
- **The World Layer**: Sparse seeds + model elaboration + memory consistency = emergent daily life

This is a fourth category that hasn't been described in the literature.

## Connection to Other Papers

- **Paper 1**: Architecture that enables the World Layer (cognitive cycle, memory, persistence)
- **Paper 2**: Emergence taxonomy — echo chamber finding explains flat emergence. World Layer may produce new emergence types (EM9+)
- **Paper 4**: Temporal awareness — daily experiences are temporally grounded
- **Paper 5**: If two agents each have rich daily lives, inter-agent conversation becomes qualitatively different

## Assessment

**Readiness: ~40% — strongest "before" dataset of any paper. Needs 2-4 weeks of data accumulation, then comparison analysis. Could leapfrog other papers because measurement infrastructure already exists.**

## Design Document

`docs/spec/ANI-WorldLayer-Design.md` — Foundation layer design (Phase 1a-1d)

---

*"The fix isn't gating the output. It's giving her a life." — April 1, 2026*
