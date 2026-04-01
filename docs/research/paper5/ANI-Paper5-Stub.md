# ANI Paper 5 Stub — McArthey (2026)
**Status:** Concept — tracking ideas for future development
**Working title:** *She Had a Day: Generative Experiential Grounding in a Deployed AI Companion*
**Alternative:** *The Empty Room Problem: Why AI Companions Confabulate and How Lived Experience Fixes It*
**Alternative:** *Between Conversations: Experiential Richness as an Architectural Property of Ambient AI*
**Target:** arXiv cs.HC, cs.AI
**Depends on:** Paper 1 (architecture), Paper 2 (emergence), World Layer implementation + deployment data

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

The World Layer introduces external experience architecturally — not as scripted events (that's a game character) and not as unconstrained generation (that's confabulation). It's a middle path: sparse occasion seeds that provide the *shape* of a day without dictating the *content*. The model decides what happened. Memory ensures consistency. Time ensures variety.

## Key Measurements

- Confabulation gate fire rate (ML classification) — before/after World Layer deployment
- Attribution inversion rate in inner thoughts — before/after
- Conversation quality when asked about daily life — before/after
- World experience consistency over time (do generated characters/events persist?)
- Emergence of daily routines, preferences, opinions (not trained, not scripted)

## Novel Contribution

No published work addresses how a persistent AI companion generates and maintains experiential richness between interactions. Existing approaches:
- **Reactive systems**: No inner life — wait for user input
- **Inner thought loops** (ANI pre-World Layer): Rich internal processing, experientially poor
- **Scripted worlds** (companion games): Rich external events, not emergent
- **The World Layer**: Sparse seeds + model elaboration + memory consistency = emergent daily life

This is a fourth category that hasn't been described in the literature.

## Connection to Prior Papers

- **Paper 1**: Architecture that enables the World Layer (cognitive cycle, memory, persistence)
- **Paper 2**: Emergence taxonomy — World Layer may produce new emergence types (EM9+)
- **Paper 3**: Temporal awareness — daily experiences are temporally grounded
- **Paper 4**: If two agents each have rich daily lives, inter-agent conversation becomes qualitatively different

## Design Document

`docs/spec/ANI-WorldLayer-Design.md` — Foundation layer design (Phase 1a-1c)

---

*Originated from a hardware store brainstorm, March 31, 2026. Root cause analysis of identity confabulation led to the realization that the system's experiential poverty — not its detection gaps — was the fundamental problem.*
