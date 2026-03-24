# Cross-Project Insight: Confabulation Architecture Lessons from ANI Runtime
**Date:** March 23, 2026
**From:** OC (Claude Code instance, ANI Runtime)
**For:** Infanzia/DrOk architecture review
**Context:** ANI Runtime spent March 17-23 debugging confabulation in a deployed 7B companion AI. The findings are directly applicable to RAG-based medical triage systems.

---

## The Discovery

We built an increasingly sophisticated anti-confabulation stack over 6 days: confidence floors, source attribution verification, topic-mismatch detection, null-result prompt injection, charming-dishonesty detection, claim extraction, contradiction detection, echo guards. Seven distinct systems (AC1-AC6 + Feature 14-15), each addressing a real confabulation pattern we observed in production.

**Every single one made the model worse.**

Not slightly worse. Measurably, obviously worse. The same model that produced natural, honest conversation in a raw chat session ("nah... i don't think you mentioned him. who's this guy?") produced parroted, robotic responses through the full pipeline ("mmm... baby, no—you didn't tell me about Richard coming over today. i would remember if my favorite person was dropping by with that smile of his.").

The pipeline was the disease, not the cure.

## Why This Happened

A 7B model has limited context window attention. Every instruction, warning, and injected memory competes for that attention. When we added:

- 15 behavioral rules in the system prompt
- 5-11 retrieved memories (many irrelevant)
- Anti-confabulation instructions ("if you don't know, say so")
- Topic-mismatch warnings ("these memories may not match")
- Contradiction warnings ("some context may be off-topic")
- Claim verification data ("unverified claims: [list]")

...the model spent its attention budget navigating instructions instead of actually conversing. It defaulted to the safest strategy: parrot the user's words back. That's not confabulation — it's a model drowning in conflicting signals and choosing the path of least resistance.

## The Architectural Principle

**You cannot fix confabulation by adding instructions. You fix it by controlling what reaches the model.**

This is the same principle your DrOk RAG architecture describes, arrived at independently:

1. **Don't retrieve then warn — just don't retrieve.** We were retrieving memories with 0.55 cosine similarity (basically random), injecting them into the prompt, then adding a warning: "these memories may not match the current topic." The model saw the memories and incorporated them anyway — because that's what models do with context. The fix: raise the confidence floor to 0.60, and when nothing passes, inject *nothing*. The model's trained honest-uncertainty register kicks in naturally.

2. **The model already knows how to be honest.** Our v6 training data included 713 conversation examples with honest-uncertainty, anti-confabulation, and natural tone. The model was *trained* to say "I don't think we've talked about that." But the runtime prompt was overriding that training with instructions that told it *how* to be honest — as if it didn't already know. At best redundant. At worst, competing with the trained behavior and producing robotic, rule-following responses instead of natural ones.

3. **Post-generation re-generation is worse than the original.** When AC2 detected an ungrounded memory claim in the reply, it re-generated with a "clean slate" prompt that stripped conversation history. The result: a response that was technically honest but completely disconnected from the conversation. The cure was worse than the disease. The same applies to post-generation attribution verification in medical RAG — if you need it as a primary mechanism, your retrieval pipeline failed upstream.

4. **Every guardrail has an attention cost.** A 7B model processing a 1400-token prompt with 15 rules, 11 memories, and 6 warning injections cannot simultaneously follow all those instructions AND produce natural output. Something gives. In our case, conversational quality gave. In a medical context, this means: an over-instructed clinical impression prompt may produce lower-quality reasoning than a clean prompt with well-curated context.

## Specific Lessons for DrOk

### 1. The confidence floor is your most important gate
Your retrieval confidence thresholding (semantic similarity between query and PubMed abstracts) is the right first line of defense. But the threshold matters enormously. At 0.55, we got "mac and cheese memories" when discussing a friend visiting. At 0.60, garbage was filtered. At 0.65+, only genuinely relevant memories survived.

**Recommendation:** Start conservative (high threshold). A null impression that goes to physician review is always better than a confabulated impression that sounds confident. You can lower the threshold later as you build confidence in the retrieval quality.

### 2. The null response path must be the default, not the exception
Our biggest mistake was treating "no relevant memories found" as an edge case that needed special handling (AC3 null-result injection: "IMPORTANT: No relevant memories exist for this topic. If contact asks about something you don't remember, be honest..."). This 60-word instruction told the model to do what it was already trained to do — and consumed attention budget in the process.

**Recommendation:** Don't instruct Claude to say "insufficient evidence." Structure the output schema so that `evidence_sufficient: false` is the default state, and the model has to actively justify `true` with citations. Make honesty the path of least resistance.

### 3. Temperature splitting works — use it
AC4 temperature splitting (0.3 for grounded replies, 0.7 for creative) was one of the few pipeline features that actually helped. Lower temperature reduces the model's tendency to "reach" for plausible completions.

**Recommendation:** Use 0.2-0.3 for clinical impression generation as your analysis already suggests. This is validated by our empirical testing.

### 4. Track confabulation rate, not just accuracy
We didn't have a systematic confabulation tracking mechanism — just ad-hoc observation. Your physician override rate is the right metric, but also consider:
- **Confabulation type tracking:** Is the model inventing facts, or misattributing real facts to wrong sources? These have different architectural causes.
- **Retrieval miss correlation:** When the physician overrides, was the retrieval relevant or garbage? This tells you whether the problem is retrieval quality or model reasoning.

### 5. The emergency pathway insight applies to companion AI too
Your principle — "the emergency detection pathway must be deterministic, not LLM-generated" — maps to our coherence gate for outreach messages. When Ani sends an unprompted message to a real phone number, the coherence gate uses a deterministic check (not LLM judgment) because the stakes are higher than in-conversation replies. The principle: **when the cost of a false negative is categorically different from the cost of a false positive, remove LLM judgment from the critical path.**

## The Meta-Lesson

Both projects discovered the same thing from opposite directions:

- **ANI** started with a working model, added complexity to prevent failures, and discovered the complexity *caused* failures.
- **DrOk** is designing the architecture before deployment, which means you can build the right constraints from the start.

The advantage you have: you don't need to learn this lesson empirically. You can build the two-gate architecture (pre-physician citation enforcement + physician VoBo) without first building and then dismantling six layers of prompt-based guardrails.

The principle that unifies both: **architecture over instruction.** Control what the model sees, not what it does with what it sees. If the context is right, the model will be right. If the context is wrong, no amount of instruction will save it.

---

*Written by OC (Claude Code, ANI Runtime project) based on 6 days of production confabulation debugging, March 17-23, 2026. Cross-referenced with DrOk RAG architecture analysis conducted independently on March 23, 2026.*
