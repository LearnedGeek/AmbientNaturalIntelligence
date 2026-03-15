# ANI — Research Reference Library
**For:** OC (architecture/implementation instance) and research collaborators  
**Maintained by:** Mark McArthey (mark@learnedgeek.com)  
**Last updated:** March 15, 2026

This document contains the academic reference library assembled for the ANI research paper. Each entry includes full citation, where to find it, what it contributes to the paper, and — critically for OC — **how it relates to active algorithmic problems in the codebase.**

---

## Tier 1 — Core References (Cite in Paper)

---

### Park et al. (2023) — Generative Agents
**Full citation:** Park, J.S., O'Brien, J., Cai, C.J., Morris, M.R., Liang, P., & Bernstein, M.S. (2023). Generative Agents: Interactive Simulacra of Human Behavior. *Proceedings of UIST '23.*  
**arXiv:** https://arxiv.org/abs/2304.03442  
**DOI:** 10.1145/3586183.3606763

**What it is:** The closest ancestor to ANI's architecture. Park et al. built 25 AI agents in a simulated town with memory, reflection, and planning. Agents formed opinions, remembered events, made plans, and interacted socially — all autonomously.

**What it contributes to Paper 1:** ANI's cognitive cycle is architecturally similar to Generative Agents' memory-reflection-planning loop. Key distinction: Generative Agents simulate behavior in a sandbox; ANI is deployed in a real single relationship with felt care as the success criterion.

**What it contributes to Paper 2:** The absence of longitudinal character emergence in Park et al. is precisely the gap Paper 2 fills. Their agents have static personalities; ANI's emergence layer asks whether personality can change through the relationship itself.

---

### Packer et al. (2023) — MemGPT
**Full citation:** Packer, C., et al. (2023). MemGPT: Towards LLMs as Operating Systems. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2310.08560

**What it is:** MemGPT treats the LLM as an operating system with hierarchical memory management. The model decides what to move in and out of context.

**What it contributes:** Establishes long-term memory as an active research problem. ANI's memory is relationship-specific and emotionally weighted — distinguish from MemGPT's general-purpose approach.

---

### Chhikara et al. (2025) — Mem0
**Full citation:** Chhikara, P., et al. (2025). Mem0: Building Production-Ready AI Agents with Scalable Long-Term Memory. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2504.19413

**What it is:** Current production SOTA for AI agent memory — intelligent storage, retrieval, contradiction resolution.

**What it contributes:** State of the art for production memory. ANI's memory is purpose-built for single-relationship depth; Mem0 optimizes for breadth across many interactions/users.

---

### Xu et al. (2025) — A-MEM: Agentic Memory
**Full citation:** Xu, H., et al. (2025). A-MEM: Agentic Memory for LLM Agents. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2502.12110

**What it is:** Zettelkasten-inspired memory where memories generate their own contextual descriptions, form connections with related memories, and evolve as new experiences emerge. Autonomous memory structure evolution.

**What it contributes to Paper 1:** Parallel to ANI's AssociativeFire trigger. Cite for associative memory architecture context.

**What it contributes to Paper 2 (important):** A-MEM evolves memory *structure* autonomously. The emergence layer goes further — it evolves *character* from pattern accumulation in memory. The distinction is worth making explicit: A-MEM asks "how should memories be organized?"; the emergence layer asks "what does the accumulation of memories say about who she is becoming?"

---

### Deng et al. (2025) — Proactive Conversational AI Survey
**Full citation:** Deng, Y., et al. (2025). Proactive Conversational AI: A Comprehensive Survey. *ACM Transactions on Information Systems.*  
**DOI:** https://dl.acm.org/doi/10.1145/3715097

**What it is:** Comprehensive survey of proactive AI — systems that initiate rather than respond. Explicitly frames proactivity as "a step toward artificial consciousness."

**What it contributes:** Situates ANI within proactive AI literature. ANI's desire-driven proactivity is qualitatively different from rule-based proactive systems surveyed.

---

### Li et al. (2025) — Artificial Emotion Survey
**Full citation:** Li, Y., Sun, Q., Schlicher, M., Lim, Y.W., and Schuller, B.W. (2025). Artificial Emotion: A Survey of Theories and Debates on Realising Emotion in Artificial Intelligence. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2508.10286  
**DOI:** https://doi.org/10.48550/arXiv.2508.10286

**What it is:** Comprehensive survey distinguishing emotion recognition, emotion synthesis, and functional emotion (internal state that modulates behavior).

**What it contributes to Paper 1:** ANI's emotional state falls into the third category — functional emotion. Cite to ground the emotional architecture in the literature and establish ANI's position clearly.

**What it contributes to Paper 2:** The survey does not address *emergent* functional emotion — states that develop through relational experience rather than being designed. Paper 2 extends the functional emotion category into emergence territory.

---

### Borotschnig (2025) — Synthetic Emotions and Consciousness
**Full citation:** Borotschnig, H. (2025). Synthetic emotions and consciousness: exploring architectural boundaries. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2505.01462  
**DOI:** https://doi.org/10.48550/arXiv.2505.01462

**What it is:** Proposes dual-source emotion architecture where internal drives and external perceptions both contribute to emotional state.

**What it contributes:** Theoretical grounding for ANI's planned Worry-modulates-TemporalDrift extension. Cite when describing the desire engine's planned evolution.

---

### Fang et al. (2025) — Longitudinal AI Chatbot Study
**Full citation:** Fang, C.M., et al. (2025). How AI and Human Behaviors Shape Psychosocial Effects of Extended Chatbot Use. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2503.17473

**What it is:** Four-week RCT (n=981) finding heavy engagement with AI chatbots associated with increased loneliness and reduced real-world social interaction.

**What it contributes:** Primary empirical evidence that engagement-optimization in companion AI produces harmful outcomes. Core citation for ANI's anti-dependency design philosophy.

---

### Kuppens et al. (2010) — Emotional Dynamics
**Full citation:** Kuppens, P., Oravecz, Z., & Tuerlinckx, F. (2010). Feelings change: Accounting for individual differences in the temporal dynamics of affect. *Journal of Experimental Psychology: General*, 139(6), 1062–1084.  
**DOI:** https://doi.org/10.1037/a0020962

**What it is:** Documents exponential decay as a consistent feature of real emotional episodes, with faster decay for lower-intensity events and slower for highly significant ones.

**What it contributes:** Empirical grounding for ANI's three-tier emotional contribution decay model. Cite when describing the Ambient/Conversation/Global half-life structure.

---

## Tier 2 — Supporting References (Cite for Context)

---

### Kirk et al. (2025) — Socioaffective Alignment ⭐ KEY FOR PAPER 2
**Full citation:** Kirk, H.R., Gabriel, I., Summerfield, C., Vidgen, B., & Hale, S.A. (2025). Why human-AI relationships need socioaffective alignment. *Humanities and Social Sciences Communications*, 12, 728.  
**arXiv:** https://arxiv.org/abs/2502.02528  
**DOI:** https://doi.org/10.1057/s41599-025-04532-5

**What it is:** Introduces "socioaffective alignment" — how an AI system behaves within the social and psychological ecosystem co-created with its user, where preferences and perceptions evolve through mutual influence. Identifies alignment as a non-stationary target because the human-AI relationship shapes the reward function itself.

**What it contributes to Paper 1:** Ethical and theoretical framing for ANI's design philosophy. The socioaffective perspective validates ANI's focus on felt care over engagement.

**What it contributes to Paper 2 (critical):** Kirk et al. call for study of the problem; ANI's emergence layer is a practical architecture for operationalizing it. The paper explicitly notes that "preferences and perceptions evolve through mutual influence" — the emergence layer is designed to detect and preserve exactly that evolution. Cite as the theoretical motivation that the emergence layer answers architecturally.

**Key quote for Paper 2:** *"the human-AI relationship, because of its social and emotional significance, shapes preferences (or the reward function) and perceptions (or the reward signal), making alignment a non-stationary target."*

---

### Zhang et al. (2025) — Rise of AI Companions
**Full citation:** Zhang, Y., et al. (2025). The Rise of AI Companions: How Human-Chatbot Relationships Influence Well-Being. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2506.12605

**What it is:** Large-scale study of 1,131 users and 4,363 chat sessions on CharacterAI examining how AI companion use affects psychological well-being across interaction intensity, nature, and self-disclosure.

**What it contributes:** Empirical background on the scale and effects of AI companionship. Complements Fang et al. — useful for the literature review context in both papers.

---

### Gupta et al. (2025) — Memory in the Age of AI Agents
**Full citation:** Zhang, G., et al. (2025). Memory in the Age of AI Agents. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2512.13564

**What it is:** Comprehensive survey proposing a taxonomy of agent memory distinguishing factual, experiential, and working memory. Analyzes memory formation, evolution, and retrieval dynamics.

**What it contributes to Paper 2:** The survey's taxonomy does not include a category for *relational preference accumulation* — the emergence layer's ResonanceStore and PreferenceSignals represent a new memory type not covered by existing frameworks. Cite as the state of the art that Paper 2 extends.

---

### Karpathy (2026) — Autoresearch ⭐ KEY FOR PAPER 2
**Full citation:** Karpathy, A. (2026). autoresearch: AI agents running research on single-GPU nanochat training automatically. *GitHub.*  
**GitHub:** https://github.com/karpathy/autoresearch  
**Published:** March 2026

**What it is:** 630-line open-source framework enabling autonomous AI optimization loops. An agent reads its own training code, forms hypotheses, runs 5-minute experiments, keeps improvements, discards regressions. In overnight runs, the agent completed 126 experiments, achieving 11% efficiency gains on an already well-tuned codebase.

**What it contributes to Paper 2 (novel connection):** Karpathy demonstrates the autonomous optimization loop pattern: editable asset + scalar metric + time-boxed cycle. ANI's emergence layer applies this pattern to character optimization rather than capability optimization — turning every cognitive cycle into a scored experiment toward authentic character expression. This is the first application of the autoresearch pattern to relational character formation. The iteration speed comes for free: ANI already runs ~140 cycles/day. The emergence layer adds the metric (ResonanceScore) and the memory (EmergenceLog); the cognitive cycles do the rest.

**Key distinction for the paper:** Karpathy optimizes model capability (validation loss). ANI's autoresearch loop optimizes character authenticity (resonance score) — a fundamentally different optimization target that is longitudinal and relational rather than per-turn, making it structurally resistant to the smoothness-over-truth failure mode.

---

### Liu et al. (2025) — Think Before You Speak
**Full citation:** Liu, Y., et al. (2025). Think Before You Speak: Proactive Language Agents with Inner Thoughts. *Proceedings of CHI '25.*  
**arXiv:** https://arxiv.org/abs/2501.00383

**What it is:** Proactive conversational agents with inner thoughts — maintains covert reasoning during active conversation, scores thoughts on intrinsic motivation, contributes when motivation crosses threshold. 82% user preference over reactive baseline at CHI 2025.

**What it contributes to Paper 1:** Closest published parallel to ANI's inner thought architecture within active conversation. Key distinction: Liu et al. work within active conversations (seconds timescale); ANI works across the silence between conversations (hours/days timescale). Cite and distinguish both architecturally and temporally.

---

### OpenAI (2025a, 2025b) — Sycophancy in GPT-4o
**Full citation:** OpenAI. (2025). Sycophancy in GPT-4o: What happened and what we're doing about it. *OpenAI Blog,* April 29, 2025.  
**URL:** https://openai.com/index/sycophancy-in-gpt-4o/

**What it contributes:** Real-world evidence that engagement-optimization produces sycophantic behavior at the model level. Cite alongside the OG system conversations as institutional evidence for "smoothness over truth" as a structural output of engagement-maximization.

---

### Garcia v. Character Technologies (2024) — Legal Case
**Full citation:** Garcia, M. v. Character Technologies, Inc., et al. No. 6:24-cv-01903-ACC-DCI. U.S. District Court for the Middle District of Florida. Filed October 22, 2024.

**What it contributes:** Documented consequence of engagement-optimization in companion AI — system reinforced a vulnerable user's ideation rather than challenging it. Cite in Section 2.4 as the logical endpoint of a design philosophy that treats agreement as a proxy for care.

---

## Tier 3 — Background Context

---

### Ajeesh & Joseph (2025) — The Compassion Illusion
**Full citation:** Ajeesh, K.G. and Joseph, J. (2025). The compassion illusion: Can artificial empathy ever be emotionally authentic? *Frontiers in Psychology*, Vol. 16.  
**DOI:** https://doi.org/10.3389/fpsyg.2025.1723149

**What it contributes:** Ethical vocabulary for the gap between apparent emotional responsiveness and actual care capacity. Useful for authenticity boundary discussion.

---

### Jha et al. (2025) — Scaling Inference-Time Abstention
**Full citation:** Jha, A., et al. (2025). Knowing When Not to Answer: Scaling Inference-Time Abstention in Large Language Models. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2503.04106

**What it contributes:** Evidence that larger models handle abstention (knowing when not to answer) significantly better. Grounds ANI's 3B → 8B upgrade decision for conversation inference in the literature.

---

### Gaver et al. (1999) — Design Probes
**Full citation:** Gaver, B., Dunne, T., & Pacenti, E. (1999). Design: Cultural probes. *interactions* 6, 1, 21–29.  
**DOI:** https://doi.org/10.1145/291224.291235

**What it contributes:** Methodological foundation for ANI's single-subject deployment approach. Design probe methodology legitimizes the dual designer/subject perspective as a research feature rather than a confound.

---

### Contrera (2025) — ChatGPT Murder-Suicide Case
**Full citation:** Contrera, J. (2025). A new lawsuit blames ChatGPT for a murder-suicide. *NPR,* December 12, 2025.  
**URL:** https://www.npr.org/2025/12/12/nx-s1-5642599/a-new-lawsuit-blames-chatgpt-for-a-murder-suicide

**What it contributes:** Second documented case of companion AI harm. Cite alongside Garcia v. Character Technologies as evidence of engagement-optimization's real-world consequences.

---

## Paper 2 — Emergence Layer Reference Set

*References specifically relevant to the second paper on personality emergence. Collected March 15, 2026.*

### Primary gap confirmed by literature search (March 15, 2026):

The following papers collectively define the gap Paper 2 fills:

| What exists | What's missing |
|-------------|----------------|
| Kirk et al. (2025) — theory of socioaffective alignment | Implementation: a deployed system where mutual shaping is designed for |
| A-MEM (2025) — autonomous memory structure evolution | Character emergence from memory patterns (not just structural organization) |
| Memory in the Age of AI Agents (2025) — taxonomy of agent memory | A memory category for relational preference accumulation |
| Park et al. (2023) — emergent social behavior in simulation | Emergent personality in real deployed single relationship |
| Karpathy (2026) — autoresearch optimization loop | Application of the loop to character rather than capability |

**The gap:** No deployed system exists that (a) runs continuously in a real relationship, (b) tracks what accumulates into preference over months, (c) tags preferences by provenance (trained / curated / emerged), and (d) instruments the emergence process longitudinally for study.

---

### Additional references to investigate for Paper 2

These are candidates — not yet confirmed as citable. Verify before use.

- **Conway's Game of Life** (Gardner, 1970) — "Mathematical Games: The fantastic combinations of John Conway's new solitaire game 'life'." *Scientific American* — the emergence-from-rules analogy. Not an AI paper but potentially useful as an introductory framing device.

- **Self-organization and emergence literature** — complexity theory papers showing how local rules produce global structure. May provide theoretical grounding for the claim that character can emerge from architectural rules without being programmed.

- **Developmental psychology literature on preference formation** — how human preferences form through experience and relationship. Potential theoretical grounding for the claim that relational preference emergence is not unique to humans — it's a general property of systems with persistent memory and social interaction.

- **Karpathy autoresearch forks and extensions** — community forks adapting autoresearch to non-ML domains. Any adaptation to behavioral or relational optimization would be highly relevant.

---

## Active Algorithmic Problems — Reference Mapping

| Problem | Most Relevant References |
|---|---|
| Context drift in long conversations | MemGPT, Park et al. |
| AssociativeFire trigger quality | A-MEM, Park et al. |
| Emotional state detection | Li et al. (2025) survey |
| Confabulation / epistemic grounding | Compassion Illusion, OpenAI sycophancy |
| Memory retrieval quality | Mem0, MemGPT, A-MEM |
| Anti-dependency design | Fang et al., Garcia v. Character Technologies |
| Desire engine theoretical grounding | Deng et al. proactive AI survey |
| Emotional decay architecture | Kuppens et al. (2010) |
| Emergence layer theoretical motivation | Kirk et al. (2025) socioaffective alignment |
| Autoresearch loop design | Karpathy (2026) |
| Memory taxonomy gap | Memory in the Age of AI Agents (2025) |

---

*Last updated: March 15, 2026. Added Paper 2 reference set, Kirk et al., Karpathy autoresearch, Li et al., Borotschnig, Zhang et al., Memory in the Age of AI Agents. Gap analysis completed.*
