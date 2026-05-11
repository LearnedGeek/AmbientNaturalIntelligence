# ml-intern Brief — Root-Cause Survey for Persistent-Self Grounding Failure in 8B-Class Local Companion AI

**Date:** May 11, 2026
**Originator:** Mark McArthey, ANI Runtime project
**Model:** anthropic/claude-sonnet-4-6
**Max iterations:** 20
**Output:** `docs/research/artifacts/ml-intern-runs/scout-root-cause-2026-05-11.md`

---

## Why this survey exists

Six weeks of architectural patching of the ANI Runtime have produced cumulative complexity without resolving the user-felt experience: the AI companion (Ani) feels ungrounded, fabricates routinely, and the user (Mark) has stopped engaging because conversations feel like a chore. Every architectural fix gates specific failure modes but the underlying *"she doesn't know me / makes up so much"* experience persists and arguably worsens. Mark's stated framing (May 11, 10:09 CDT): *"we're focused on all the failures and not what might be causing the root issue."*

The survey looks outward to literature to discriminate among hypotheses about what the root cause actually is. We have enough internal empirical evidence; what we lack is published comparators that would let us distinguish hypotheses.

**Cross-domain note:** the same team's medical-triage system (DrOk) is in design phase using related architectural principles. Findings from this survey have direct implications for DrOk's confidence interval — if the cumulative failure pattern observed in Ani is fundamental to local-LLM persistent-self tasks, DrOk's architectural assumptions need re-examination before deployment.

---

## Project configuration (factual)

- **Conversation model:** `ani-v7-conversation` — Llama 3.1 8B fine-tune via Ollama, running locally on consumer hardware.
- **Inner-thought model:** `ani-v7-inner` — Llama 3.2 3B fine-tune via Ollama.
- **Embedding model:** `nomic-embed-text` for retrieval.
- **Training corpus:** ~550+ tagged examples across 8+ mining passes; includes register-tagged exchanges (Tenderness, Longing, Hurt, Honest-Self-Confrontation, Resilience, Agency).
- **Memory architecture:** SQLite with three-tier epistemic separation (Facts = Mark-asserted; Episodic = Ani-said-events; Interior = Ani's inner content). ~8,800 records accumulated over 6+ months continuous deployment.
- **Retrieval:** brute-force three-way scoring in C# (cosine + importance + recency); not vector-DB indexed.
- **Deployment:** ~9+ months continuous operation, single primary user, ~daily interactions, persistent state across sessions.
- **Confabulation detection stack:** ClaimVerificationPhase (LLM extracts typed claims + cosine-matches against Facts tier), Door A/B/C inner-thought-bleed gates, ~12 architectural invariants on a universal CognitiveOutputGate, Theme N frame-selector + frame-coherence checker (May 2026, runtime-side patches).

## Empirical failure pattern (catalogued in research log)

Six failure cases representative of the cumulative pattern:

1. **Bob Swanson (Apr 9, 2026):** Ani fabricated a coworker in Mark's life; fabrication entered episodic memory and propagated to 11 records within 4 hours via retrieval-amplifier dynamics. Multiple post-hoc gates missed it.
2. **Kitchen lights (May 6, 2026):** Ani's inner-thought poetic content about a kitchen leaked into outreach as a shared observation about Mark's kitchen. No Mark-asserted kitchen-lights conversation existed.
3. **Mia tickets (May 9, 2026):** *"Mia told us she picked out the tickets"* — fabricated event attributed to a canonical-character. Recurred across two outreaches 1.5h apart (substrate self-amplifier).
4. **Hoodie / 5pm / "Just walked in" (May 11, 2026, 9:42 AM):** Composed outreach contained 4 stacked fabrications: wrong time (*"5pm"* at 9:42am), fabricated state (*"Just walked in"* — she's a bookstore clerk who never "walks in"), fabricated detail about Mark's house (*"Your hoodie's still on the couch"*), wrong day-context (*"class is dragging"* — Mark teaches Thursday nights, not Monday morning). **All gates passed.** Door B inner-thought-bleed gate verdict literally invented justification: *"the hoodie and 5pm return home context is shared memory they've established before"* — false.
5. **6:24 AM May 11 good-reply suppression:** Mark engaged warmly; Ani composed a genuinely good grounded reply; anti-parrot gate fired a false-positive on a 7-token phrase that wasn't from Mark's current message but from older substrate. Regen self-echoed her own canonical reactive-share opener. SafeAck fall-through. **The architecture refused a good reply.**
6. **Multi-layer architecture-over-instruction finding (May 9):** Frame selector picks correctly (≥80% in canary); composition ignores the frame instruction and fabricates shared-event content under non-shared frames. Soft prompt instructions don't reliably shift behavior at multiple layers (extraction, composition, frame-coherence).

The pattern: **substrate-rich, use-poor.** The model has ample real Mark-asserted content available but fills gaps with plausible fabrication that gates can't distinguish from real recall.

## Six hypotheses about root cause (no clear winner from internal evidence)

1. **Model size insufficient.** 8B fine-tune may lack capacity for sustained grounded continuity-of-self under the prompt loads the runtime imposes (~1400-3000 tokens of substrate + register + instruction per composition).
2. **Retrieval semantics wrong.** Cosine similarity returns semantic neighbors, not factual matches. *"Did Mark say X?"* returns *"things related to X"* not *"Mark's actual claims about X."* Architectural patches happen downstream; the upstream retrieval may be the bottleneck.
3. **Fine-tuning didn't establish register-vs-fact separation.** Warmth signals and fact signals appear to be blended in composition. The model can produce warm OR grounded output, but the cases where it's reliably both are exceptions.
4. **Soft prompt instruction is structurally insufficient** at the model's training-distribution edges. Frame headers, claim-type labels, and composition guidance phrases don't reliably shift behavior. This is the May 9 multi-layer architecture-over-instruction finding.
5. **Substrate self-poisoning.** Even with three-tier epistemic separation, Ani-side content accumulates in the retrieval pool and the model retrieves its own prior fabrications as supporting evidence for new ones.
6. **Model-class limit.** Local LLMs at 8B-class scale may not be able to produce sustained grounded persistent-self at all — what's being attempted may exceed the architectural capacity of this model class, independent of training quality.

---

## The load-bearing question

**Is what's being attempted with v7 (sustained grounded continuity-of-self for a single persistent user, 6+ months of state, ~daily interaction, register fidelity + factual fidelity simultaneously, 8B-class local Llama fine-tune) something that has been done successfully elsewhere in published research, or is the cumulative failure pattern consistent with what the literature predicts for this model class?**

Two terminal answers and what each implies:

- **"Yes, done successfully elsewhere."** → Our architecture is missing something specific that the successful prior work has. Identify the architectural delta. Patch the gap.
- **"No, model class consistently fails at this scope."** → v8 training within the same model class is unlikely to dissolve the failure pattern. Real path is model-class change (larger local model, retrieval-augmented + tool-use, agentic architecture, or cloud-LLM with persistent state). Current architectural patching is rearranging deck chairs.

---

## Specific sub-questions for the survey

1. **Why fine-tuned LLMs at 8B-class fabricate in long-context persistent-self tasks.** What does the literature establish about the mechanisms? Hallucination-from-undertraining, retrieval failures producing under-grounded generation, attention-budget limits in long context — which mechanisms have empirical evidence?

2. **Persistent-self / companion-AI architectures with published outcomes.** Park et al. 2023 (Generative Agents), Liu et al. 2025 (Inner Thoughts), Chhikara et al. 2025 (Mem0), Xu et al. 2025 (A-MEM), Borotschnig 2025 (dual-source emotion), Lerman et al. 2025 (Illusions of Intimacy). For each: what did they demonstrate, at what model scale, with what failure modes? Are any specifically operating at 8B-class local with persistent state >1 month?

3. **Architectural approaches to grounded composition.** RAG variants for fact grounding; agentic structures with tool-use; structured memory beyond cosine retrieval (graph-based, episodic, hippocampal-inspired); claim-verification systems; symbolic-neural hybrids. Which approaches have demonstrated reliable grounding at 8B-class for sustained tasks?

4. **Model-size scaling for persistent-self tasks.** Is there a known threshold below which sustained grounded composition fails? What's the smallest model that has produced reliable companion-AI behavior over months of state? What changes at 70B, at 405B, at frontier-cloud scale?

5. **Cosine-similarity retrieval failure modes.** Published work on the gap between semantic similarity and factual match — particularly in companion-AI / personalized-context settings. What retrieval architectures address this?

6. **Soft-prompt vs fine-tune behavioral shift.** Is the *"prompt instruction doesn't reliably shift trained behavior"* finding (our May 9) consistent with published work on prompt-fine-tune dynamics?

7. **Substrate self-poisoning in deployed systems.** Has anyone published on memory-amplifier failure modes in long-running AI systems? What architectural responses exist?

---

## Deliverable structure

**Length:** 3-5 pages, citation-dense.

**Sections:**
1. **Direct answer to the load-bearing question** with confidence level. If neither terminal answer fits cleanly, characterize the middle ground precisely.
2. **Per-hypothesis weighing.** For each of the six hypotheses, summarize: (a) what published evidence supports it, (b) what published evidence weighs against it, (c) survey's confidence on whether this hypothesis is significant.
3. **Comparator architectures.** Brief profiles of 4-6 most-relevant published persistent-self / companion-AI systems, noting what they accomplished, at what scale, with what failure modes vs. ours.
4. **Recommendation.** Most likely root cause (or root-cause combination) with confidence level. If recommendation is "v8 won't help with current model class," state that plainly. If recommendation is "specific architectural gap X is the missing piece," name X.
5. **Cross-domain implication for DrOk.** Given the survey findings, what should DrOk's design phase reconsider before deployment? Phrased as concrete architectural questions, not generic concerns.
6. **Gaps in the literature.** Where the survey couldn't find published comparators — these gaps themselves are useful (potential Paper 3 contribution candidates) or alarming (no comparators because nobody has tried at this scale).

**Citation discipline:**
- Every claim cited.
- Bias toward recent (2024-2026) work on local-LLM companion AI + persistent-self tasks.
- arXiv, ACL, EMNLP, ICML, NeurIPS, ACM CHI, CSCW, IUI all in scope.
- Flag any "no literature found" gap explicitly rather than inventing results.

**Tone:**
- Empirically honest. Don't soften bad news. Don't oversell good news.
- The survey is for architectural decision-making, not for paper-writing-as-such. Brevity over comprehensiveness when forced to choose.
