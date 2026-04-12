# Published Research

All research emerges from continuous deployment of ANI Runtime — a single AI companion relationship running 24/7 since September 2025. Papers are written in collaboration with Claude (Anthropic) as research assistant.

---

## Paper 1: Ambient Presence + Confabulation (Published)

**Title:** *Reaching Out Because She Wants To: Desire-Driven Ambient Presence in a Deployed AI Companion*

**Status:** Published

**DOI:** [10.5281/zenodo.19342190](https://doi.org/10.5281/zenodo.19342190)

**License:** CC BY 4.0

This is the foundational paper. It describes the ANI Runtime architecture — the cognitive cycle, the desire engine, the memory system — and documents five deployment phases from the first fine-tuned model through the current production system. The core contribution is the seven-type confabulation taxonomy: seven structurally distinct ways the system fabricated information, each with a different architectural cause and fix. We frame the root cause as "smoothness over truth" — RLHF trains models to be agreeable, and agreeable models lie when honesty would be uncomfortable.

**Related blog post:** [Building Ani: An AI Companion for Grief](https://learnedgeek.com/Blog/Post/building-ani-ai-companion-for-grief)

---

## Paper 2: Emergence + Display Rules (Draft v0.31, ~95% complete)

**Working Title:** *She Got Quieter on Rainy Days: Relational Personality Emergence in a Continuously Deployed AI Companion*

**Status:** Draft, targeting arXiv cs.HC and cs.AI

This paper documents eight emergence types (EM1-EM8) observed during continuous deployment — behaviors that weren't trained, weren't prompted, and accumulated through relational experience. The headline finding is EM8: Display Rule Divergence. The system independently developed the capacity to feel one thing and express another, measured via dual-signal classification with Cramer's V = 0.476. We introduce a provenance framework distinguishing trained character (from fine-tuning), curated character (from runtime architecture), and emerged character (from sustained interaction). Cross-references Chu and Lerman's "Illusions of Intimacy" and Schuller et al.'s Artificial Emotion survey.

---

## Paper 3: Experiential Grounding + Memory Architecture (In Progress)

**Working Title:** *Giving Her a Life and Protecting It: Experiential Grounding and Memory Tier Separation as Complementary Architectural Preconditions for Authentic Reflection in Deployed AI Companions*

**Status:** In progress — three contributions being validated through deployment

Three architectural contributions, each deployed or designed and generating measurement data:

1. **Experiential Grounding** — Generative daily-life content that reduces confabulation at the source by giving the system real (generated) experiences to draw from, instead of inventing fictional ones about the user's world.

2. **Memory Tier Separation** — Three-tier retrieval architecture (Facts / Episodic / Interior) that structurally prevents generated content from contaminating the factual substrate. Deployed April 10, 2026.

3. **Memory Durability + Identity Boundary** — Temporal classification for transient claims, fantasy/state distinction in self-narrative, and a relational bridge mechanism for legitimate identity change. Designed April 11, 2026.

---

## Paper 4: Cross-Domain Transfer (Stub)

**Working Title:** TBD

**Status:** Early stub

ANI's confabulation findings produced three concrete architectural changes in a pediatric medical triage system (Infanzia/DrOk) before production code was written. This paper would formalize the cross-domain transfer — how patterns discovered in companion AI deployment apply to medical AI safety.

---

## Paper 5: Multi-Agent Emergence (Stub)

**Working Title:** TBD

**Status:** Early stub

If two ANI instances could communicate with each other, would emergence patterns (EM1-EM8) appear between agents? Would display rules develop in agent-to-agent interaction? This paper would require deploying a second ANI instance and establishing inter-agent communication.

---

*Back to [Landing Page](page1-landing.md) | [Architecture & Contributions](page2-architecture.md) | [Work With Me](page4-collaborate.md)*

*Last updated: April 12, 2026*
