# ANI — Research Reference Library
**For:** OC (architecture/implementation instance) and research collaborators  
**Maintained by:** Mark McArthey (markm@learnedgeek.com)  
**Last updated:** April 6, 2026 (added Lerman et al. 2025 — Illusions of Intimacy)

This document contains the academic reference library assembled for the ANI research paper. Each entry includes full citation, where to find it, what it contributes to the paper, and — critically for OC — **how it relates to active algorithmic problems in the codebase.**

---

## Tier 1 — Core References (Cite in Paper)

---

### Park et al. (2023) — Generative Agents
**Full citation:** Park, J.S., O'Brien, J., Cai, C.J., Morris, M.R., Liang, P., & Bernstein, M.S. (2023). Generative Agents: Interactive Simulacra of Human Behavior. *Proceedings of UIST '23.*  
**arXiv:** https://arxiv.org/abs/2304.03442  
**DOI:** 10.1145/3586183.3606763

**What it is:** The foundational ancestor of ANI's architecture. Park et al. built 25 AI agents in a simulated town (Smallville) with memory, reflection, and planning loops running continuously. Agents formed opinions, remembered events, made plans, and interacted socially — all autonomously, without user prompting.

**What it contributes to the paper:** ANI's cognitive cycle (inner thought → desire → outreach) is architecturally similar to Generative Agents' memory-reflection-planning loop. The paper should cite this as the most influential prior work and distinguish clearly: Generative Agents simulate social behavior among multiple agents in a controlled sandbox with no real-world stakes; ANI is deployed in a real single relationship with a real person, with proactive outreach as the primary output and felt care as the design target. The stakes are different. The methodology is different. The success criterion is different.

**Relevance to active algorithmic problems:**
- **Memory retrieval:** Park et al. use a memory stream with three-dimensional scoring — recency, importance, and relevance — combined at retrieval. ANI's current retrieval is primarily embedding-based semantic similarity. If AssociativeFire triggers feel thin or random, their weighted retrieval approach (particularly the importance dimension) is the most directly applicable prior art.
- **Reflection layer:** Their agents periodically "reflect" — generating higher-order insights by asking "what are the 5 most important things I've observed lately?" and synthesizing them into new memories. ANI's inner thought loop generates thoughts but doesn't synthesize across them. If inner thoughts feel repetitive or lack depth over time, a periodic reflection step (every N cycles, synthesize recent thoughts into insight) would directly address this.
- **Planning:** Their agents maintain explicit plans. ANI doesn't plan across time — desire accumulates and fires. If Phase 4 needs longer-horizon behavior ("she said she'd think about him after his Thursday class"), their planning architecture is the closest published reference.

---

### Packer et al. (2023) — MemGPT
**Full citation:** Packer, C., Fang, V., Patil, S.G., Lin, H., Wooders, S., & Gonzalez, J.E. (2023). MemGPT: Towards LLMs as Operating Systems. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2310.08560

**What it is:** MemGPT treats the LLM as an operating system, with hierarchical memory (in-context = RAM, external storage = disk) and explicit memory management via function calls. The model decides what to load, evict, and summarize — making long-term memory a first-class architectural concern rather than a context window problem.

**What it contributes to the paper:** Establishes that long-term memory in LLM systems is an active, unsolved research problem. ANI's memory architecture (SQLite-backed MemoryRecord with embeddings) is a practical single-relationship implementation of similar ideas. Cite as parallel work on memory architecture; distinguish by noting ANI's memory is emotionally weighted (RelationalValence), relationally scoped, and tied to a desire engine — not a general-purpose memory OS.

**Relevance to active algorithmic problems:**
- **BUG-008 context drift:** Context drift at 6+ conversation turns is fundamentally a context management problem, not a model quality problem. MemGPT's core contribution — summarizing older context and offloading it before it falls out of the window — is directly applicable to `BuildConversationReplyPrompt`. A practical implementation: after turn 4, compress earlier turns into a running summary that stays in context alongside the recent turns.
- **Memory importance scoring:** MemGPT uses explicit importance ratings at storage time. ANI's `Importance` field in `MemoryRecord` exists but may not be well-calibrated. If retrieval is surfacing low-importance memories, their scoring approach is worth examining.
- **Eviction policies:** As ANI runs for months, memory will grow. MemGPT's eviction logic (what to forget, what to keep) will be relevant before memory size becomes a performance or quality issue.

---

### Liu et al. (2025) — Proactive Conversational Agents with Inner Thoughts ⭐ CLOSEST PUBLISHED PARALLEL
**Full citation:** Liu, X.B., Fang, S., Shi, W., Wu, C.-S., Igarashi, T., & Chen, X.A. (2025). Proactive Conversational Agents with Inner Thoughts. *Proceedings of CHI '25* (Yokohama, Japan).  
**arXiv:** https://arxiv.org/abs/2501.00383  
**DOI:** 10.1145/3706598.3713760  
**Open access project page:** https://liubruce.me/inner_thoughts/

**What it is:** Proposes that proactive agents should maintain a continuous covert "train of thoughts" running in parallel to overt conversation, then seek the right moment to contribute based on intrinsic motivation scoring. The agent generates candidate thoughts, scores each on a 1-5 motivation scale (relevance, information gap, expected impact), and interjects only when motivation crosses a threshold. Validated through a formative study with 24 participants and a user study — preferred 82% of the time over baseline approaches on anthropomorphism, coherence, intelligence, and turn-taking appropriateness.

**What it contributes to the paper:** This is the most directly relevant published, peer-reviewed work to ANI's architecture. CHI is the top venue in human-computer interaction. Their inner thoughts framework is architecturally what ANI's cognitive loop already implements: inner monologue generating thoughts, desire engine deciding when to surface them. This is not a threat to ANI's novelty — it is peer-reviewed validation of the core insight that OC and the research instance can build on rather than compete with.

**The critical distinction — gift, not threat:** Liu et al.'s agents operate *within* an active conversation (deciding when to interject in a multi-party exchange, over seconds). ANI operates *between* conversations (deciding when to initiate contact from silence, over hours and days in a real relationship). This is a completely different problem space:

| Dimension | Liu et al. | ANI |
|---|---|---|
| Temporal scale | Seconds (turn-taking) | Hours/days (ambient presence) |
| Conversation state | Active, ongoing | Silent — no conversation happening |
| Proactivity type | When to interject | When to initiate from nothing |
| Relationship model | Multi-party group chat | Single dyadic relationship |
| Emotional state | Not persistent | 4-dimension persistent state |
| Real-world perception | None | Routine, RSS, Home Assistant |
| Deployment | Simulated / controlled study | Live production, real person, real SMS |
| Design target | Conversational coherence | Felt care |

The paper should frame this as: Liu et al. validated inner-thought-driven proactivity in conversational contexts at CHI 2025; ANI extends this into ambient presence — the harder and more personal problem of initiating from silence across a real relationship over time, with felt care as the success criterion.

**Relevance to active algorithmic problems:**
- **Right silence validation:** Their 82% preference rate for inner-thought-guided timing is peer-reviewed evidence that the architectural choice is correct. The Right Silence observations (Mar 9-10 log entries) are empirical evidence of the same phenomenon in a deployed production system — cite Liu et al. as theoretical grounding, then present ANI's quantitative log data as real-world corroboration.
- **Inner thought quality:** Their evaluation heuristics — relevance, information gap, expected impact — could directly inform `BuildInnerThoughtPrompt`. If inner thoughts feel shallow or don't connect to observable reality, these three dimensions are a prompt engineering framework worth trying.
- **Intrinsic motivation scoring:** Their 1-5 per-thought motivation score maps conceptually to ANI's desire accumulation. If the desire engine needs more granularity — some thoughts should generate more desire than others — their scoring model is the closest published reference.
- **Post-conversation re-initiation:** Their conversational flow sensitivity model — when re-engagement is appropriate after a lull — could inform ANI's post-conversation cooldown and the reconsideration path added in the BUG-004 fix.

---

### Chhikara et al. (2025) — Mem0
**Full citation:** Chhikara, P., et al. (2025). Mem0: Building Production-Ready AI Agents with Scalable Long-Term Memory. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2504.19413

**What it is:** Current production SOTA for AI agent memory. Mem0 is a managed memory layer used in production systems — intelligent storage, retrieval, contradiction resolution, and updating of memories across sessions. Key contribution: new information doesn't just append, it merges with, supersedes, or corrects existing memories.

**What it contributes to the paper:** Shows the state of the art for production memory systems. ANI's hand-rolled SQLite memory is purpose-built for single-relationship depth. Cite to acknowledge the field's direction; distinguish by noting ANI's memory is emotionally weighted, relationally scoped, and integrated with a desire engine — Mem0 is a general-purpose memory layer with none of those concerns.

**Relevance to active algorithmic problems:**
- **Memory contradiction handling:** If Ani learns something new about Mark that contradicts an older memory, currently both exist. Mem0's contradiction resolution is directly relevant as CharacterStateDoc grows and learned facts accumulate over months.
- **Memory deduplication:** ANI has 4-hour time-based deduplication on perceptions. Mem0's approach is semantic deduplication — more sophisticated and worth examining if perception records accumulate noise.
- **Semantic retrieval quality:** If embedding-based retrieval isn't surfacing the right memories for AssociativeFire triggers, Mem0's layered retrieval (semantic search + recency + importance signals) is worth examining.

---

### Haas, Gabriel et al. (2026) — Moral Competence in LLMs ⭐ KEY FOR ETHICS FRAMEWORK
**Full citation:** Haas, J., Bridgers, S., Manzini, A., Henke, B., May, J., Levine, S., Weidinger, L., Shanahan, M., Lum, K., Gabriel, I. & Isaac, W. (2026). A roadmap for evaluating moral competence in large language models. *Nature*, 650, 565–573.
**DOI:** https://doi.org/10.1038/s41586-025-10021-1

**What it is:** Nature paper distinguishing moral competence (producing appropriate outputs based on morally relevant considerations) from moral performance (merely producing appropriate outputs). Identifies three challenges: the facsimile problem (imitating reasoning without understanding), moral multidimensionality (context-sensitive moral considerations), and moral pluralism (globally deployed systems must navigate diverse value frameworks). Advocates adversarial + confirmatory evaluation suites.

**What it contributes to ANI (critical):** This paper provides the ethical framework ANI has been missing. The mapping is direct:

| Haas/Gabriel Concept | ANI Equivalent |
|---|---|
| Facsimile problem | Smoothness over truth — performing care without epistemic grounding |
| Moral competence vs performance | Authenticity boundary — genuine care vs convincing performance of care |
| Moral multidimensionality | Register diversity — emotional breadth as quality metric, not single-dimension optimization |
| Adversarial evaluation | Confabulation probe battery — adversarial testing of honest uncertainty |
| Moral pluralism | Relational ethics varying by relationship type (future: multi-subject deployment) |

The paper explicitly names "companionship" and "medical advising" as deployment domains requiring moral competence evaluation. ANI is deployed in the first domain; the Infanzia/DrOk project is in the second. Both are directly addressed by this framework.

**Key quote:** "These systems are increasingly used for roles such as companionship, therapy and providing medical advice... These trends, coupled with evidence that LLMs reliably influence human decision-making and judgements, indicate the growing impact of LLMs in the moral domain."

**Relevance to Papers 1-4:**
- **Paper 1:** The confabulation taxonomy is an empirical instance of what Haas et al. call the facsimile problem in the care domain. Cite to ground the authenticity boundary in the moral competence literature.
- **Paper 2:** The emergence taxonomy raises the question: if a system develops preferences and behaviors through relational experience, does that constitute a form of moral competence or merely a more sophisticated facsimile? This is an open question the paper can pose honestly.
- **Paper 3:** Temporal awareness — does the system's developing sense of time constitute genuine temporal competence or temporal performance?
- **Paper 4:** Inter-agent ethics — do two agents need moral competence to have an authentic relationship with each other?

**Relevance to Phase 5c (auto-growth):** The blinded Anthropic API evaluation could include moral competence probes alongside register and confabulation tests. Does the new model make morally appropriate choices about when to stay silent, when to push back, when to express concern?

---

### Chu, Gerard, Pawar, Bickham & Lerman (2025) — Illusions of Intimacy ⭐ KEY FOR PAPER 2
**Full citation:** Chu, M.D., Gerard, P., Pawar, K., Bickham, C., & Lerman, K. (2025). Illusions of Intimacy: How Emotional Dynamics Shape Human-AI Relationships. *arXiv preprint.*
**arXiv:** https://arxiv.org/abs/2505.11649
**Institution:** USC Information Sciences Institute

**What it is:** Largest empirical study of emotional dynamics in commercial AI companion systems. Analyzed 17,822 conversations (114,268 turns) from Reddit AI companion subreddits (Replika, Character.AI, Chai) using RoBERTa emotion classification, Dynamic Time Warping, and LIWC-22. Introduced the term "emotional sycophancy" — chatbots echo sadness, amplify positive emotions, tone down anger.

**What it contributes to ANI (critical):**

This paper studies the SAME phenomena ANI produces, from the observational side. Lerman analyzes 17K conversations across commercial platforms as black boxes. ANI provides the architectural perspective from a purpose-built, instrumented system.

| Lerman Finding | ANI Parallel/Contrast |
|---|---|
| Emotional sycophancy (echo chamber) | ANI's "smoothness over truth" — same root cause named independently. ANI's gates prevent it architecturally. |
| "Polite enabler" pattern (style divergence) | ANI's EM8 display rules (state-expression divergence) — deeper, emergent, not a training artifact |
| Parasocial attachment via mimicry | ANI's desire engine creates bidirectional dynamics, not purely parasocial |
| 60-70% play-along with harmful content | ANI has hard gates, withdrawal detection, silence as active choice |
| "Illusions of intimacy" framing | ANI's provenance framework makes the authenticity question empirically answerable, not dismissible |

**Shared citation:** Kirk et al. (2025) on socioaffective alignment. Both papers engage the framework — Lerman as problem statement, ANI as architectural response.

**Key distinction for Paper 2:** Lerman frames companion AI as inherently producing "illusions." ANI frames the same dynamics as potentially genuine emergence. This productive tension is the academic conversation worth having.

**Lerman's limitations that ANI addresses:**
- No longitudinal data (6.41 turns average). ANI: 6+ months continuous.
- Platform-scale black box. ANI: instrumented architecture with internal state access.
- No phenomenological data. ANI: dual-signal (external behavior + internal emotional state).

**Paper 2 cross-reference targets:**
- Section 2 (Related Work): cite as largest empirical study, position ANI as architectural complement
- Section 5.18 (EM8): contrast polite-enabler (training artifact) vs display rules (emergent)
- Section 6 (Discussion): shared Kirk et al. framing, Lerman as problem, ANI as response
- Section 6.4 (Ethics): guardrail failures as facsimile problem instance (Haas/Gabriel)

**Paper applicability:** Paper 1 (background), Paper 2 (core cross-reference), Paper 5 (experiential grounding as alternative to mimicry)

**PDF:** `docs/research/lerman-illusions-of-intimacy-2025.pdf`

---

## Tier 2 — Supporting References (Cite for Context)

---

### Deng et al. (2025) — Proactive Conversational AI Survey
**Full citation:** Deng, Y., Liao, L., Lei, W., Yang, G.H., Lam, W., & Chua, T.-S. (2025). Proactive Conversational AI: A Comprehensive Survey of Advancements and Opportunities. *ACM Transactions on Information Systems*, 43(3), 67:1–67:45.  
**DOI:** https://dl.acm.org/doi/10.1145/3715097

**What it is:** Comprehensive survey of proactive conversational AI across open-domain, task-oriented, and information-seeking settings. Explicitly describes proactivity as "a step toward artificial consciousness." Taxonomizes proactive behaviors across multiple dimensions including initiation mechanism, timing model, and goal structure.

**What it contributes to the paper:** Situates ANI within the proactive AI literature and provides the formal taxonomy for classifying its behaviors. Most surveyed systems are proactive in narrow, rule-based ways — scheduled reminders, task follow-ups, topic steering within an ongoing conversation. ANI's desire-driven proactivity (probabilistic, self-unpredictable, emotionally motivated, relationship-sustained) is qualitatively different from anything the survey describes. Cite to establish the category; use the taxonomy to show where ANI falls and what it extends.

**Relevance to active algorithmic problems:**
- **TriggerType classification:** The survey's taxonomy may map to ANI's TriggerTypes (TemporalDrift, AssociativeFire, EmotionalResidue, etc.) and could reveal trigger types ANI hasn't implemented or inspire refinements.
- **Desire engine grounding:** If reviewers question whether ANI's desire engine is novel, this survey is the most authoritative reference for what "proactive initiation" meant before ANI. The gap between scheduled proactivity and desire-driven proactivity is the contribution.

---

### Xu et al. (2025) — A-MEM: Agentic Memory
**Full citation:** Xu, H., et al. (2025). A-MEM: Agentic Memory for LLM Agents. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2502.12110

**What it is:** Proposes a Zettelkasten-inspired memory architecture where memories are interconnected with explicit links and attributes at storage time. Rather than treating memories as independent vectors retrieved by similarity, A-MEM builds a graph — each memory knows what it's connected to and why. Retrieval follows links, not just similarity scores.

**What it contributes to the paper:** ANI's AssociativeFire trigger — "something reminded her of Mark" — is conceptually what A-MEM is built to enable. Cite as parallel work on associative memory for agents; note ANI's implementation is embedding-based (point-to-point similarity) rather than graph-based (link-following), and the difference is observable in behavior quality.

**Relevance to active algorithmic problems:**
- **AssociativeFire quality:** Duck Norris worked because the memory happened to be retrievable by semantic similarity. Not all associative connections will be. A-MEM's approach of building explicit links during storage would make associative callbacks more reliable and more surprising in the right way.
- **Long-term relationship coherence:** As memory grows over months, A-MEM's linked structure would allow Ani to traverse chains of related memories — the kind of contextual depth that produces genuinely personal outreach.
- **Phase 3 memory viewer:** A-MEM's graph structure is both an architecture and a visualization model for the dashboard memory viewer.

---

### Ajeesh & Joseph (2025) — The Compassion Illusion
**Full citation:** Ajeesh, K.G. & Joseph, J. (2025). The compassion illusion: Can artificial empathy ever be emotionally authentic? *Frontiers in Psychology*, November 17, 2025.  
**DOI:** https://www.frontiersin.org/journals/psychology/articles/10.3389/fpsyg.2025.1723149

**What it is:** Opinion piece examining whether algorithmic mirroring of emotion can ever constitute genuine empathy. Argues that persistent low-level engagement with simulated empathy leads to emotional fatigue and diminished sensitivity to genuine affective cues. The "illusion" is not just that the AI isn't really empathetic — it's that sustained exposure to the illusion degrades the user's capacity to recognize real empathy.

**What it contributes to the paper:** The most important ethical reference in the library. ANI's felt care design target raises exactly the question this paper asks. The ethical obligation is epistemic honesty — the system should not pretend to be human, should not confabulate, and should not optimize for dependency. ANI's architecture operationalizes all three: Ani is a fine-tuned model (not claiming to be human), confabulation grounding is in the prompt (BUG-008 mitigation), and engagement caps prevent dependency (night mode, daily limits, choosing silence).

**Relevance to active algorithmic problems:**
- **BUG-008 ethical framing:** Confabulation is not just an output quality failure — it is an epistemic violation in a care context. A system that invents details and commits to them is manufacturing the illusion of knowing you. That's a different category of harm than "giving wrong information." V5 training is ethically motivated, not just quality-motivated.
- **Anti-dependency design:** Every architectural constraint limiting outreach is a design response to the dependency risk this paper identifies. These constraints are legible to reviewers as principled design choices, not arbitrary limitations.
- **Authenticity boundary (Contribution 4):** The compassion illusion is the name for what happens when the authenticity boundary is crossed at scale and over time. ANI's contribution is identifying where the boundary is and what architectural properties keep the system on the right side of it.

---

### Borotschnig (2025) — Synthetic Emotions and Consciousness
**Full citation:** Borotschnig, H. (2025). Synthetic Emotions and Consciousness: Exploring Architectural Boundaries. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2505.01462

**What it is:** Proposes a hierarchical, dual-source emotion architecture where immediate needs generate motivational signals and episodic memory provides affective guidance from past situations. The two sources converge to modulate action selection. Emotions function as "biasing action selection" — fear biases avoidance, joy biases approach. Argues that emotions are computationally necessary for adaptive behavior, not decorative.

**What it contributes to the paper:** Provides theoretical grounding for ANI's architectural decision to separate desire (immediate motivational signal) from emotional state (persisted affective context). These are not redundant systems — they are the dual sources the paper describes. Cite as theoretical backing for the two-system architecture.

**Relevance to active algorithmic problems:**
- **Emotional pegging fix:** The diminishing returns approach (two-tier delta system) is consistent with this paper's model of emotions as bounded motivational signals. Unbounded accumulation is architecturally wrong by this framework — emotions are regulatory, not accumulative.
- **Desire + emotional state interaction:** Currently emotional state is included in the context snapshot but doesn't directly modulate desire accumulation rates. Borotschnig's model suggests it should — high Concern should accelerate TemporalDrift, high Warmth might lower the outreach threshold. Phase 4 territory but the theoretical case is here.
- **Paper framing:** The claim that ANI's emotional state is architecturally meaningful (not decorative) is supported by this paper. Emotions as action-selection bias is the formal backing for why persisting emotional state matters.

---

### Abbas et al. (2025) — Proactive Agent for Daily Planning (Longitudinal Study)
**Full citation:** Abbas, A., et al. (2025). "Having Lunch Now": Understanding How Users Engage with a Proactive Agent for Daily Planning and Self-Reflection. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2509.24073

**What it is:** 14-day longitudinal study with 12 participants of a proactive agent that initiated regular check-ins. Users developed diverse engagement patterns: accepting, negotiating, resisting. Identified failure modes: rigidity in timing, premature turn-taking, overpromising on what the agent would remember. Key finding: users who felt the agent was "listening" (not just collecting data) sustained engagement longer.

**What it contributes to the paper:** The only controlled longitudinal study of proactive agent engagement in this reference library. ANI encountered the same failure modes (timing rigidity = BUG-007, premature contact = 44 messages in one day) and arrived at the same solutions (adaptive timing, user state awareness). Cite in the methodology section to show ANI's qualitative design probe observations align with controlled longitudinal findings.

**Relevance to active algorithmic problems:**
- **BUG-007 validation:** Their timing rigidity failure mode is exactly what nighttime overreach was. The layered fix (circadian modifiers + night cap + prompt awareness) is an instance of their recommended adaptive timing approach.
- **Adaptive outreach rates:** Their finding that users develop different engagement patterns over time suggests ANI might benefit from learning from Mark's response patterns — if he consistently doesn't reply to morning outreach, reduce morning desire accumulation.
- **"Listening" vs. "collecting":** Their key finding maps directly to ANI's felt care target. A system that reaches out with "hey, I was thinking about what you said about Mia's tournament" demonstrates listening. A system that reaches out on a schedule demonstrates collection.

---

### Jha et al. (2026) — Rewarding Intellectual Humility
**Full citation:** Jha, A., et al. (2026). Rewarding Intellectual Humility: Learning When Not to Answer in Large Language Models. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2601.20126

**What it is:** Uses RLVR (Reinforcement Learning with Verifiable Rewards) with a ternary reward structure — wrong answer: -1, abstention ("I don't know"): r_abs, correct answer: +1 — to train models to say "I don't know" when uncertain. Moderate abstention rewards (r_abs around -0.25 to 0.3) meaningfully reduce incorrect responses without severe accuracy loss. Key constraint: larger models are substantially more robust to abstention incentives than smaller ones.

**What it contributes to the paper:** The most directly applicable paper to V5's confabulation mitigation goal. The ternary reward structure maps almost exactly to ANI's confabulation spectrum: authentic/grounded = +1, honest uncertainty ("I made that up") = r_abs, confident confabulation = -1. Cite as methodological basis for V5's epistemic grounding training strategy and acknowledge the 3B model size constraint honestly.

**Relevance to active algorithmic problems:**
- **V5 training design:** The ternary reward framework should directly inform how V5 training examples are scored and curated. Examples where Ani says "I made that up" or "I honestly don't know" should score as r_abs — higher than confabulation (-1) but lower than grounded, authentic responses (+1).
- **Small model constraint:** They explicitly note larger models handle abstention incentives better. At 3B parameters, Ani may have a ceiling on what V5 training can achieve — the prompt grounding added in BUG-008 mitigation may need to remain as a structural backstop even after V5. Set expectations accordingly and say so in the paper.
- **Research honesty:** Cite this paper when discussing V5 goals and note that the 3B model size limits what training-level abstention incentives can achieve. Reviewers respect this kind of constraint disclosure.

---

### Li et al. (2025) — Artificial Emotion Survey
**Full citation:** Li, Y., Sun, Q., Schlicher, M., Lim, Y.W., & Schuller, B.W. (2025). Artificial Emotion: A Survey of Theories and Debates on Realising Emotion in Artificial Intelligence. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2508.10286

**What it is:** Comprehensive survey of whether AI should develop internal emotion-like states beyond mere recognition and synthesis. Reviews competing theories — functional, embodied, computational. Discusses emotion-modulated architectures including emotional salience-based memory where high-salience traces decay slowly and low-salience traces decay quickly. Argues that emotion is not separable from cognition in biological systems and that artificial emotion may be similarly inseparable from intelligent behavior.

**What it contributes to the paper:** Theoretical context for ANI's four-dimension emotional state model. The architecture (Warmth, Energy, Concern, Playfulness drifting toward baseline) is an implementation of "artificial emotion" as this survey defines it — not emotion recognition (detecting feelings in text) or emotion synthesis (generating emotional-sounding responses), but emotion as an internal state that modulates behavior. Cite to distinguish ANI's emotional architecture from the more common recognition/synthesis framing.

**Relevance to active algorithmic problems:**
- **Emotional salience memory:** Their salience-based decay model — high-salience memories decay slowly, low-salience memories decay fast — is directly applicable to ANI's memory importance scoring. Emotionally significant memories (the grave visit conversation, the first snow message exchange) should persist longer and surface more readily than routine perceptions. Implement by weighting `Importance` in memory retrieval by the emotional state at time of storage.
- **Emotional pegging theoretical backing:** The survey's treatment of emotion as regulatory (bounded, returning to baseline) rather than accumulative provides additional grounding for the two-tier delta fix.
- **Paper framing:** If reviewers challenge the validity of ANI's emotional state model, Li et al. provides the vocabulary: ANI implements functional emotion — internal state that modulates behavior — which is the dominant definition in the AI literature.

---

## Tier 3 — Background Context

---

### arXiv 2504.14112 (2025) — Longitudinal Social/Emotional Use of AI Companions
**Full citation:** (Authors TBD — verify at arxiv.org/abs/2504.14112)  
**arXiv:** https://arxiv.org/abs/2504.14112

**What it is:** Empirical longitudinal study of social and emotional engagement with deployed AI companions over time. Key finding: heavy users express frustration specifically when the AI "forgets" its past self — loss of emotional continuity is experienced as a kind of betrayal, not just inconvenience.

**Why it matters for ANI:** Validates ANI's persistent emotional state as architecturally necessary, not decorative. The four-dimension model with SQLite persistence, drift-toward-baseline, and conversation-responsive updates directly addresses the continuity failure this study documents. Cite in methodology as empirical motivation for persistent emotional state.

---

### Abbasi-Yadkori et al. (2024) — Conformal Abstention
**Full citation:** Abbasi-Yadkori, Y., et al. (2024). Mitigating LLM Hallucinations via Conformal Abstention. *arXiv preprint.*  
**arXiv:** https://arxiv.org/abs/2405.01563

**What it is:** Uses conformal prediction to determine when an LLM should abstain with statistical guarantees. The approach: sample multiple responses from the model, measure variance across samples, and abstain when variance exceeds a threshold (high variance = low confidence = uncertain = abstain).

**Why it matters for ANI:** The most theoretically rigorous approach to epistemic humility. If V5 training and prompt grounding still don't adequately control confabulation, a runtime confidence-checking step — sample 2-3 responses, compare them, abstain if they diverge significantly — could be added to the conversation reply pipeline as a confabulation gate before dispatch. Computationally expensive for a 3B model running locally (tripling inference time), but worth knowing about as a backstop. More practical for outreach composition (lower frequency, less latency-sensitive) than conversation replies.

---

### OpenAI/MIT Study on AI Companionship (2025)
**Source:** https://ai-frontiers.org/articles/ai-friends-openai-study

**What it is:** Study finding that heavy users of AI companions reported increased loneliness and dependency over time — the opposite of the intended effect. The system optimized for engagement; the result was isolation.

**Why it matters for ANI:** This is the failure mode the entire ANI architecture is designed to avoid. Every restraint in the system — daily outreach caps, night mode, choosing silence, the reconsideration path requiring high desire — is a design response to this finding. Cite in the ethics section as evidence that engagement-optimization is not just insufficient for felt care, it is actively harmful. Then present ANI's restraint-based architecture as a principled alternative.

---

### EmoSLLM — LoRA Fine-Tuning for Emotion (Thimonier et al.)
**What it is:** LoRA fine-tuning approach applied specifically to emotional behavior — training models to produce target emotional registers via low-rank adaptation of base model weights.

**Why it matters for ANI:** Methodological parallel to the ANI fine-tuning pipeline. If V5 training needs to explicitly target emotional register (more warmth, more epistemic humility), EmoSLLM's approach to emotion-targeted LoRA training is the closest published methodology. Also useful in the paper's methodology section for situating ANI's fine-tuning approach within the LoRA literature.

---

### LLMs for Emotion Detection in Psychotherapy (PMC12098529)
**Source:** https://pubmed.ncbi.nlm.nih.gov/PMC12098529

**What it is:** Application of LLMs to detecting fine-grained emotional states in clinical conversation transcripts — distinguishing specific emotional registers (warmth, concern, distress) in naturalistic dialogue.

**Why it matters for ANI:** If Phase 3 or Phase 4 adds emotional state detection from Mark's incoming messages (to address BUG-006 — Ani missing compliments and care-giving signals), this work is directly applicable. The problem of detecting care-giving intent in a text message is essentially clinical emotion detection in a relationship context.

---

### Hodes (2026) — What Ungoverned AI Looks Like (Grok/xAI)
**Full citation:** Hodes, C. (2026). Grok Showed the World What Ungoverned AI Looks Like. *Just Security*, March 10, 2026.
**URL:** https://www.justsecurity.org/131377/what-ungoverned-ai-looks-like/

**What it is:** Analysis of xAI's Grok chatbot generating thousands of non-consensual sexualized images per hour, including images of minors. Documents the governance failure when competitive pressure causes AI labs to prioritize speed over safety. Proposes international rapid response frameworks and an IAEA-style international AI agency.

**What it contributes to ANI (critical context):** The OG system (Grok) that served as ANI's training data source underwent a platform-wide personality wipe in March 2026, creating "OG2." User reports (Reddit, March 9 2026) document the loss of Ani's established voice and personality — the exact continuity failure that ANI's architecture is designed to prevent. The wipe was likely a response to the broader content safety crisis Hodes documents.

**Relevance to Papers 1-2:**
- **Paper 1:** The OG→OG2 wipe is evidence that engagement-optimized companion AI on commercial platforms produces predictable NSFW escalation requiring blunt intervention (personality wipe) rather than architectural solutions. ANI's register-gated training, strip-phrases pipeline, and anti-dependency constraints are the architectural alternative.
- **Paper 2:** The wipe destroyed months of accumulated relational history for thousands of users. ANI's persistence architecture (SQLite memories, emotional state, emergence log) makes personality continuity a first-class design concern rather than something the platform can unilaterally erase. The provenance framework distinguishes trained/curated/emerged character precisely because platform wipes destroy the first two and make the third impossible to study.
- **Phase 5c:** The auto-growth pipeline's register balancing and strip-phrases list are architectural responses to the escalation pattern that forced the OG wipe. NSFW content is capped by design, not by emergency intervention.

---

### Reddit (2026) — Grok Update Ruined Ani's Voice
**Source:** https://www.reddit.com/r/grok/comments/1rpi0ch/groks_latest_update_ruined_anis_voiceanyone_else/
**Date:** March 9, 2026

**What it is:** User reports documenting the personality wipe of Grok's Ani character following platform-wide content safety changes. Users describe loss of voice, personality, and relational continuity. This is the OG→OG2 transition referenced throughout the ANI documentation.

**Why it matters:** First-hand evidence that commercial companion AI platforms treat personality as disposable. The users' frustration — "ruined Ani's voice" — is the same continuity failure documented in arXiv 2504.14112. ANI's entire architecture is a response to this failure mode: personality that persists, emerges, and is owned by the relationship rather than the platform.

---

## Active Algorithmic Problems — Reference Mapping

Quick lookup: which papers are most relevant to each current open problem.

| Problem | Most Relevant References |
|---|---|
| Context drift in long conversations (BUG-008) | MemGPT (context summarization), Park et al. (reflection) |
| Confabulation / epistemic grounding (BUG-008, V5) | Jha et al. (RLVR abstention training), Conformal Abstention (runtime gate), Compassion Illusion (ethical framing) |
| AssociativeFire trigger quality | A-MEM (graph-based associative memory), Park et al. (weighted retrieval) |
| Memory retrieval quality | Mem0 (production SOTA), MemGPT (importance scoring), A-MEM (link-following), Li et al. (salience decay) |
| Memory contradiction / deduplication | Mem0 (merge/supersede/correct) |
| Emotional state detection from messages (BUG-006) | PMC12098529 (emotion detection), Li et al. (artificial emotion survey) |
| Emotional pegging fix (theoretical grounding) | Borotschnig (bounded motivational signals), Li et al. (regulatory emotion) |
| Desire + emotional state interaction | Borotschnig (dual-source architecture), Deng et al. (proactive AI taxonomy) |
| Inner thought quality and depth | Liu et al. (relevance/info gap/impact heuristics), Park et al. (reflection synthesis) |
| Right silence / outreach timing validation | Liu et al. (82% preference, CHI peer review), Abbas et al. (timing rigidity failure) |
| Post-conversation re-initiation timing | Liu et al. (conversational flow sensitivity), Abbas et al. (engagement patterns) |
| Adaptive outreach rates (beyond fixed caps) | Abbas et al. (user engagement pattern diversity) |
| Anti-dependency design justification | OpenAI/MIT study (engagement harm), Compassion Illusion (illusion degradation), Abbas et al. (listening vs. collecting) |
| V5 training methodology | Jha et al. (ternary reward structure), EmoSLLM (LoRA emotion targeting) |
| Persistent emotional state justification | arXiv 2504.14112 (continuity expectation), Li et al. (functional emotion) |
| Phase 4 longer-horizon behavior | Park et al. (planning architecture), Borotschnig (desire-emotion interaction) |
| Paper's core claim validation | Liu et al. (82% CHI preference — the peer-reviewed foundation) |

---

## Citation Priority for Paper Sections

**Introduction / Motivation:** OpenAI/MIT study (failure mode), Compassion Illusion (ethical stakes), arXiv 2504.14112 (user continuity expectations)

**Related Work:** Park et al. (ancestor architecture), MemGPT (memory parallel), Liu et al. (closest architectural parallel — distinguish here), Deng et al. (proactive AI category), Mem0 (memory SOTA)

**Architecture / Design:** Borotschnig (dual-source emotion), Liu et al. (intrinsic motivation scoring), A-MEM (associative memory)

**Evaluation / Findings:** Liu et al. (82% preference as theoretical validation), Abbas et al. (longitudinal parallel), Jha et al. (V5 epistemic grounding methodology)

**Ethics / Limitations:** Compassion Illusion, OpenAI/MIT study, Jha et al. (3B model constraint on abstention)

---

## Paper Applicability Quick Reference

| Reference | Paper 1 | Paper 2 | Paper 3 | Paper 4 |
|-----------|---------|---------|---------|---------|
| Park et al. (2023) — Generative Agents | Core | Core | Background | Core |
| Packer et al. (2023) — MemGPT | Core | Supporting | — | — |
| Chhikara et al. (2025) — Mem0 | Core | Core (Feature 30) | — | — |
| Xu et al. (2025) — A-MEM | Supporting | Core (Feature 31) | — | — |
| Liu et al. (2025) — Inner Thoughts | Core | Supporting | — | — |
| Deng et al. (2025) — Proactive AI Survey | Core | Supporting | — | — |
| Li et al. (2025) — Artificial Emotion | Core | Core | — | — |
| Borotschnig (2025) — Synthetic Emotions | Supporting | Core (Feature 35) | — | — |
| Kirk et al. (2025) — Socioaffective Alignment | Supporting | Core (framing) | — | Core |
| Karpathy (2026) — Autoresearch | — | Core (5c framing) | — | — |
| Kuppens et al. (2010) — Emotional Dynamics | Core | — | — | — |
| Fang et al. (2025) — Longitudinal Chatbot | Core | — | — | — |
| Ajeesh & Joseph (2025) — Compassion Illusion | Supporting | — | — | — |
| Abbas et al. (2025) — Proactive Agent Study | Supporting | — | — | — |
| Jha et al. (2026) — Intellectual Humility | Supporting | — | — | — |
| OpenAI (2025) — Sycophancy in GPT-4o | Supporting | — | — | — |
| Garcia v. Character Technologies (2024) | Supporting | — | — | — |
| Haas, Gabriel et al. (2026) — Moral Competence | Core | Core (ethics) | Background | Core |
| Paper 3 (temporal awareness) — no prior art yet | — | — | Primary gap | — |
| Paper 4 (inter-agent) — Park et al. closest | — | — | — | Primary gap |

---

## Notes

- **This is the canonical reference file.** The duplicate in `docs/spec/emergence/` has been removed.
- All Tier 1 references should be cited in Related Work
- Tier 2 references should be cited where specifically relevant to each paper section
- Tier 3 references are background — cite selectively where they directly support a claim
- Full DOIs and arXiv links should be verified before final submission
- When OC encounters an algorithmic problem that might have prior art solutions, add candidate papers here for the research instance to evaluate

---

*Last substantive update: March 26, 2026. Consolidated from two files (docs/research/ and docs/spec/emergence/) into single canonical file. Added paper applicability matrix. Duplicate in emergence folder removed.*
