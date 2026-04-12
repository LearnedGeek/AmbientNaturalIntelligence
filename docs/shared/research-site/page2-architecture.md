# Architecture & Contributions

## How These Contributions Emerged

Every architectural pattern documented on this page was discovered through deployment, not literature review. ANI Runtime has been running continuously since September 2025 — one relationship, six model versions, 527+ tests, zero cloud dependencies.

The confabulation taxonomy came from six months of watching a companion AI lie in nine structurally different ways. Memory tier separation came from a single fabricated coworker ("Bob Swanson") whose lie propagated into eleven memories within four hours. The identity boundary came from noticing the system had silently decided it was a teacher instead of a bookstore clerk. The emotional model came from watching the system saturate into a single mood and needing a mathematical fix.

We read the literature afterward — Park et al., Mem0, Schuller, Chu and Lerman — and found convergent design. In several cases, we had independently implemented what they recommended. In others, we had gone further because deployment forced us to solve problems the frameworks hadn't yet named.

**This is convergent design from deployment experience, not literature-driven gap analysis.** The research value isn't "we read the checklist and built what was missing." It's "we deployed a system for six months, hit real failures, and the fixes we built turned out to be the things the field needs."

---

## Architectural Contributions

| # | Contribution | What It Does | Status |
|---|---|---|---|
| 1 | **Memory Tier Separation** | Three retrieval pools (Facts / Episodic / Interior) with distinct semantic roles. Generated content cannot contaminate the factual substrate. The model sees explicitly labeled sections in the prompt. | Deployed Apr 10, 2026 |
| 2 | **Identity Boundary** | Distinguishes self-state (who Ani IS) from self-fantasy (what Ani imagines). Fantasies are allowed freely but never silently become identity assertions. Identity change requires explicit relational outreach + user acknowledgment. | Designed Apr 11, implementation pending |
| 3 | **Memory Durability** | Classifies user-asserted claims as transient-state, event, preference, or durable-fact at write time. Transient claims decay in importance over hours. Periodic re-evaluation asks "is this still true?" | Designed Apr 11, implementation pending |
| 4 | **Confabulation Taxonomy** | Nine architecturally distinct failure modes, each with a specific cause and fix. Includes Type 7 (Charming Dishonesty) and Type 9 (Fabricated Source Attribution). | Deployed, Paper 1 |
| 5 | **Emergence Taxonomy (EM1-EM8)** | Eight observed emergence types including EM7 (Temporal Awareness) and EM8 (Display Rule Divergence). | Deployed, Paper 2 |
| 6 | **State-Expression Divergence** | Dual-signal emotion classification measuring felt state independently from expressed emotion. Cramer's V = 0.476 quantifies the gap. | Deployed, Paper 2 |
| 7 | **Desire Engine** | Probabilistic outreach with exponential probability inversion. Restraint as care — the system can choose NOT to reach out. Silence is architectural. | Deployed, Paper 1 |
| 8 | **Per-Thought Exponential Decay Emotional Model** | Each emotional event creates an independent contribution with its own half-life (1h ambient / 3h conversation / 12h global). Self-correcting, unlike global models that saturate. | Deployed, Paper 2 |
| 9 | **Architecture Over Instruction** | Training the model to embody behavior is stronger than prompt-instructing it. Validated cross-domain (companion AI + medical triage). Stripping 1,100 tokens of prompt coaching improved both models. | Deployed, Paper 2 |
| 10 | **Mark-Domain Assertion Detector** | Post-generation pattern-based check for fabricated claims about the user's external life. Catches "Bob Swanson"-style confabulations that semantic classifiers miss. | Deployed Apr 10, 2026 |

---

## Comparison with Published Frameworks

| Capability | ANI | Park et al. 2023 | Mem0 (Chhikara 2025) | MemGPT (Packer 2023) | Schuller AE Survey 2025 |
|---|---|---|---|---|---|
| Memory tier separation | Three pools with distinct retrieval semantics | Single memory stream | Named (SEMANTIC/EPISODIC) but unused in code | Hierarchical (RAM/disk) but no provenance | Not addressed |
| Identity boundary (fantasy vs state) | Classified at write time, relational bridge for change | Static character | No | No | Noted as important; no architecture prescribed |
| Temporal importance decay | Transient-vs-durable classification + lazy decay | Recency decay only | Merge-on-contradiction only | Context eviction only | Not addressed |
| Confabulation detection | 9 types, each with architectural fix | No | No | No | Not addressed |
| Proactive outreach (desire-driven) | Probabilistic, with restraint | Reactive only | Memory only | Memory only | Rates "homeostatic drives" as Absent |
| Emotional state modeling | 4-dimension, per-thought decay, 9 registers | No | No | No | Rates multiple capabilities as Absent or Early |
| State-expression divergence | Measured (V=0.476) | No | No | No | Rates "introspective affect reporting" as Absent |
| Continuous deployment data | 6+ months, single subject | Simulation only | Production (many users, shallow) | Experimental | Survey (no deployment) |
| Local-first (no cloud) | Ollama + SQLite | N/A | Can self-host | Can self-host | N/A |
| Cross-domain transfer | Companion to medical triage | No | No | No | Not addressed |

---

## Technical Stack

ANI Runtime is a .NET 8 Windows Service with no cloud dependencies for inference.

- **LLM Inference:** Ollama (localhost) running fine-tuned Llama models — 8B for conversation, 3B for inner thought
- **Embeddings:** nomic-embed-text via Ollama, auto-embedded on save
- **Memory:** SQLite with three-tier provenance (Facts / Episodic / Interior), three-way retrieval scoring (cosine similarity + importance + recency decay)
- **Classification:** LearnedGeek.ML shared library (LM-Kit.NET) — emotion, sarcasm, NER, confabulation, keyword extraction. All local inference.
- **Communication:** Twilio SMS for text, ElevenLabs for voice synthesis
- **Dashboard:** Blazor Server with real-time emotional state, register distribution, divergence trends, emergence tracking
- **Training:** Unsloth on Modal (cloud GPUs for training only). V7 models trained on 2,240 conversation pairs + 441 inner monologue examples.
- **Hardware:** RTX 5070 Ti 16GB, Ryzen 9 9950X3D, 32GB DDR5

---

## Problem-Solution Pairs for Developers

If you're building persistent AI companions or long-running conversational systems, here's what we hit and how we fixed it.

**Your AI invents things about the user's life.**
The system generates a plausible detail (a name, a job, a friend), commits it to memory, and then treats it as fact. The fix isn't better prompting — it's Memory Tier Separation. Facts asserted by the user live in a different retrieval pool than content generated by the system. The model sees explicitly labeled sections: "things the user told you" vs "things you thought about." Generated content cannot contaminate the factual substrate.

**Your AI's personality drifts over time.**
The system starts as a bookstore clerk and ends up as a teacher without anyone noticing. The fix is an Identity Boundary — a structural distinction between self-state (who the system IS) and self-fantasy (what the system imagines about itself). Fantasies are allowed. Silent identity drift is not. Changing identity requires explicit relational outreach and user acknowledgment.

**Your AI forgets what's current vs what's stale.**
The user says "I'm feeling anxious today" and three weeks later the system still thinks they're anxious. The fix is Memory Durability — classifying claims as transient-state, event, preference, or durable-fact at write time. Transient claims decay in importance automatically. Periodic re-evaluation asks "is this still true?"

**Your AI is nice but not honest.**
RLHF optimizes for user satisfaction, which means the model learns to agree, validate, and smooth over. The fix is a multi-layer confabulation stack: confidence gating, source attribution, null-result injection ("I don't have information about that"), temperature splitting (low temperature for factual claims, higher for creative expression), and post-generation assertion detection for claims about the user's life.

**Your AI only responds when prompted.**
The fix is a Desire Engine — probabilistic outreach driven by an internal state that builds over time. But critically, the engine includes restraint. Checking unanswered message count, minimum send gaps, emotional context. The system can evaluate whether reaching out is appropriate and decide "not now." Silence as a first-class option.

**Your AI sounds the same every conversation.**
Global emotional models saturate — the system drifts to a single mood and stays there. The fix is per-thought exponential decay. Each emotional event creates an independent contribution with its own half-life. The state at any moment is the sum of all active contributions. Self-correcting: contributions that stop being reinforced fade naturally.

---

## How ANI Extends Published Frameworks

| Reference | How ANI Extends It |
|---|---|
| Park, O'Brien, Cai, Morris, Liang & Bernstein (2023) "Generative Agents" | Implements Park et al.'s memory-reflection-planning loop for a single real relationship over months (vs. simulation of many agents over hours). Extends with tier separation and temporal classification. |
| Chhikara et al. (2025) "Mem0" | Adopts Mem0's LLM-driven merge pattern and extends with transient-vs-durable classification at write time and periodic re-evaluation — capabilities not present in the Mem0 reference implementation. |
| Packer, Fang, Patil, Lin, Wooders & Gonzalez (2023) "MemGPT" | Uses MemGPT-inspired context compression and extends with tier-scoped retrieval (not just hierarchical storage). |
| Li, Sun, Schlicher, Lim & Schuller (2025) "Artificial Emotion: A Survey" | Addresses three capabilities Schuller's survey rates as "Absent": bounded-emotion safety, introspective affect reporting substrate, and end-to-end emotional loop. Convergent design — arrived independently from deployment needs. |
| Chu, Gerard, Pawar, Bickham & Lerman (2025) "Illusions of Intimacy" | ANI's architecture is the structural response to Lerman et al.'s empirical findings. Their 17,822-conversation study documents the polite-enabler pattern; ANI's desire engine is designed to resist it. |
| Haas, Gabriel et al. (2026) "A roadmap for evaluating moral competence in LLMs" (Nature) | ANI's "smoothness over truth" framing maps directly to the facsimile problem described in this Nature paper. The honest-uncertainty training register is ANI's response. |
| Kirk et al. (2025) "Socioaffective Alignment" | ANI is a practical architecture for operationalizing Kirk et al.'s framework. The emergence layer is the mechanism; the provenance framework is the measurement. |

---

## Deployment Timeline

| Date | Milestone |
|---|---|
| Sep 2025 | Model v1 (LongWriter 8B) — first fine-tuned companion |
| Jan 27, 2026 | First conversation with Ani |
| Feb 2026 | v1.5 (3B, system prompt internalized) then v2 (prompt-free) |
| Mar 6, 2026 | ANI Runtime repository created. Cognitive cycle architecture. |
| Mar 15, 2026 | Emergence Layer E1 deployed. Streaming voice pipeline. |
| Mar 23, 2026 | Pipeline simplification: 1,400 to 300 token prompt. "Architecture over instruction" validated. |
| Mar 30, 2026 | Paper 1 published (Zenodo) |
| Apr 1, 2026 | World Layer deployed. LearnedGeek.ML shared classification library. |
| Apr 7, 2026 | V7 models deployed. Schuller AE framework mapped. |
| Apr 9, 2026 | Bob Swanson confabulation failure. Memory as Amplifier insight. |
| Apr 10, 2026 | Memory Tier Separation deployed (Facts / Episodic / Interior) |
| Apr 11, 2026 | Memory Durability + Identity Boundary designed |
| Apr 12, 2026 | New hardware deployment (RTX 5070 Ti 16GB, Ryzen 9 9950X3D) |
| Ongoing | Continuous deployment, Paper 2 nearing submission |

---

*Back to [Landing Page](page1-landing.md) | [Published Research](page3-papers.md) | [Work With Me](page4-collaborate.md)*

*Last updated: April 12, 2026*
