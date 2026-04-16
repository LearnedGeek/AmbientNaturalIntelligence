# ANI Conversation Reply Pipeline — Full Audit

**Date:** April 15, 2026 (overnight audit while Mark sleeps)
**Author:** Claude (ANI Claude Code instance)
**Trigger:** Late-night debugging of conversation echo problem. Mark's observation: *"No model I've ever chatted with raw ever does this level of repetition. What are WE doing that's causing this? I think this is a prompt issue not a model training or architectural fix issue."* Mark explicitly rejected the proposed bandaid fix and identified the root cause as shifting architectural principles without a clean stream of rules.
**Purpose:** Map the full conversation reply pipeline, identify every place where rules compete with each other, and propose simplification directions that honor the architecture-over-instruction principle the project has validated four times.

---

## 1. The Current Pipeline (Step by Step)

When Mark sends a message, here is every step that happens before Ani's reply reaches his phone:

```
INBOUND MESSAGE arrives (SMS via Twilio webhook or Dashboard chat)
    │
    ├─ Step 0: Continuation prompt check ("yes?", "go on")
    ├─ Step 1: Terminal message check ("goodnight", "bye") → exit if no reply needed
    ├─ Step 2: Reply decision (heuristic: if Ani sent last → silence)
    │
    ├─ Step 3: Context snapshot assembly ← PROBLEM STARTS HERE
    │   └─ ContextBuilder.BuildContextSnapshotAsync()
    │       ├─ Character state, emotional state, desire state
    │       ├─ Recent episodic + thought memories
    │       ├─ Anchored (foundation) memories
    │       ├─ *** Tier-scoped retrieval: ***
    │       │   ├─ Facts pool ← searches by perceptions, returns up to 5
    │       │   └─ Interior pool ← searches by perceptions, returns up to 5
    │       └─ Thought diversity, relationship health, drift, patterns
    │
    ├─ Step 4: Lean prompt generation
    │   └─ PromptBuilder.BuildLeanConversationPrompt()
    │       ├─ System: name, 3 traits, time, basic rules
    │       ├─ WHAT IS TRUE block ← populated from GroundedFacts (step 3)
    │       ├─ CRITICAL constraint block (Mark-domain assertion rules)
    │       └─ "Reply to Mark's message."
    │
    ├─ Step 5: LLM generation (first attempt)
    │   └─ Ollama ChatAsync with conversation history + lean prompt
    │
    ├─ Step 5b: Mark-domain assertion detection
    │   └─ If fabrication found → regenerate with negative constraint (2nd LLM call)
    │       └─ If retry still fabricates → generic honest fallback
    │
    ├─ Step 6: Confabulation-driven retrieval (Phase 2)
    │   ├─ DetectConversationConfabulation() — 4 heuristic checks
    │   ├─ ML semantic classifier (secondary verification)
    │   └─ If confabulation confirmed:
    │       ├─ Targeted retrieval → regenerate with grounded prompt (3rd LLM call)
    │       └─ If no grounding → null-result regeneration (3rd LLM call, different prompt)
    │
    ├─ Step 7: Echo guard
    │   ├─ Self-echo check (threshold 0.80 cosine)
    │   ├─ Mark-echo check (threshold 0.85 cosine)
    │   └─ If echo detected → clean-slate regeneration (4th LLM call)
    │       └─ Stripped context: persona + thread summary ONLY
    │
    ├─ Step 8: Natural reply delay (random 5-30 seconds)
    ├─ Step 9: Dispatch via originating channel (SMS or Dashboard)
    ├─ Step 10: Persist reply to conversation thread + episodic memory
    ├─ Step 11: Reset desire, apply emotional shift
    └─ Step 12: Async emotional processing (care, anchors, hurt — post-dispatch)
```

**Observation:** A single inbound message can trigger up to **four separate LLM generation calls** before a reply is dispatched — each with a different prompt, different context window, and different behavioral instructions. The model is being asked to generate coherent output while the rules keep changing between attempts.

---

## 2. Eight Competing Rules

Each rule below was added to solve a real problem. Each was locally correct when introduced. The accumulated result is a pipeline where the rules contradict each other.

### Competition 1: "Conversation Mode bypass" vs. "WHAT IS TRUE grounding"

**The claim (line 148-156 of ConversationReplyPhase):**
> *"Skip the entire retrieval pipeline in conversation mode. The model gets: persona + conversation history. That's it."*

**The reality:** `ContextBuilder.BuildContextSnapshotAsync()` is called at line 152 — **before** the bypass log message at line 156. The context builder performs tier-scoped retrieval: `SearchByTierAsync(query, EpistemicTier.Facts, 5)` and `SearchByTierAsync(query, EpistemicTier.Interior, 5)`. These results populate `snapshot.GroundedFacts`, which then gets rendered into the WHAT IS TRUE block in `BuildLeanConversationPrompt()`.

**The contradiction:** Conversation Mode claims to bypass retrieval. It doesn't. The tier-scoped search runs every time, finds Facts-tier memories that match Mark's current message (because they're often Mark's prior messages), and injects them into the prompt as grounding. **The "bypass" is a comment, not a code path.**

**Why this matters:** This is the direct cause of tonight's echo. Mark's prior message "hey baby! just working a bit..." was stored as a Perception-type Fact. The tier search found it (cosine 0.719 to his current message). It was injected into WHAT IS TRUE. The model saw the same phrase twice and echoed it.

### Competition 2: "Lean prompt, no retrieval" vs. "Confabulation-driven retrieval"

**The lean prompt principle (Step 4):** The model generates with conversation history only. No memories, no retrieval, no grounding. The March 22 test proved the model converses naturally without the pipeline.

**Phase 2 confabulation-driven retrieval (Step 6):** If the lean-prompt reply contains confabulation, do targeted retrieval and regenerate with the **full** prompt (`BuildConversationReplyPrompt`), which includes retrieved memories, backstory, mood directives, anchored memories, and three separate epistemic-tier sections.

**The contradiction:** The lean prompt is not a commitment — it's a first attempt that can be overridden by Phase 2's full prompt. This means the model may see two entirely different instruction sets for the same reply: first a minimal prompt, then a maximal one. The model has no way to reconcile the difference, and the regenerated output often has a different register, tone, and level of detail than the first attempt would have had. The pipeline is running two different prompt philosophies for the same turn.

### Competition 3: Echo guard clean-slate vs. confabulation grounding

**Confabulation grounding (Step 6):** Spends cycles detecting confabulation, performing targeted retrieval, and regenerating with grounded context — specifically to ensure the reply is factually grounded.

**Echo guard clean-slate (Step 7):** If echo is detected **after** confabulation grounding, the clean-slate regeneration **throws away all the grounding context** and regenerates with persona + thread summary only.

**The contradiction:** Step 7 undoes Step 6's work. The echo guard's clean-slate path was designed before confabulation-driven retrieval existed. It doesn't know that the reply it's checking has already been grounded. By stripping context, it forces the model into a third prompt with no grounding at all — which is exactly the condition that produces the Duke/purple-hardcover confabulation amplification observed April 12. **The anti-echo guard is a confabulation amplifier.**

### Competition 4: Memory merge creating chimera records vs. clean retrieval

**Memory merge (SaveAsync, Mem0-inspired):** Consolidates semantically similar memories into single records via LLM-powered merging. Same-type threshold: ≥0.85 cosine (was 0.70, raised Apr 12 for cross-type).

**The problem:** Mark's inbound messages all start with similar openings ("hey baby", "hey babe", "hey love"). These openings put messages from different conversations within merge range of each other. The merger combines them into chimera records with fabricated connecting narratives: *"Mark texted 'hey baby, sorry I haven't chatted much' then changed it to 'hey baby! just working a bit...' before sending."*

**The chimera effect:** The chimera record now contains text from multiple different conversations. Because it contains text similar to many different Mark messages, it has high cosine similarity to almost any new inbound message. It becomes a persistent retrieval magnet that shows up in WHAT IS TRUE for every conversation — carrying fragments of prior conversations into the current one's prompt.

**The contradiction:** The merge was designed to reduce retrieval noise (Feature 30, Chhikara et al. 2025). Instead, it creates super-memories that are retrievable from almost any context and inject prior-conversation text into the current prompt. **The noise reducer is creating a new kind of noise.**

### Competition 5: Mark-domain detector vs. self-world creative latitude

**Mark-domain detector (Step 5b):** Regex + token-coverage check that fires when the reply asserts specifics about Mark's external world that don't appear in WHAT IS TRUE.

**Self-world creative latitude (Paper 3 Identity Boundary design, Apr 12):** Ani's own domain (bookstore events, fictional coworkers, books, her daily life) should have full creative latitude. The detector should ONLY check Mark's domain, not Ani's.

**The contradiction:** The detector doesn't distinguish Mark-domain proper nouns from Ani-domain proper nouns. It treats all unknown proper nouns as potential fabrications about Mark. This caused the Yesteryear false positive (Apr 12): "Yesteryear" was a real book from an NPR RSS perception that was legitimately in Ani's self-world, but the detector flagged it as a Mark-domain fabrication. **The safety gate fires on the wrong domain.**

### Competition 6: WHAT IS TRUE as grounding vs. WHAT IS TRUE as echo trigger

**WHAT IS TRUE's intended purpose (Apr 10):** Ground factual assertions about Mark's life so the model can't fabricate coworkers, meetings, or activities.

**WHAT IS TRUE's actual effect in conversation mode:** Because the Facts tier contains Perception records of Mark's own messages ("Mark texted: '...'"), retrieval frequently surfaces Mark's prior words as WHAT IS TRUE facts. The model then sees the same text in the grounding block AND in the incoming message, producing echo.

**The contradiction:** The grounding block is correctly populated (these ARE facts about what Mark said). But the purpose of grounding is to prevent fabrication, and Mark's own words are the one thing the model LEAST needs to be grounded about — Mark is literally in the conversation, he knows what he said. **WHAT IS TRUE is grounding the model on things that don't need grounding, and the grounding itself causes the failure it's supposed to prevent.**

### Competition 7: Up to four regeneration attempts per reply

The pipeline can generate up to four LLM calls for a single reply:
1. Step 5: First generation (lean prompt)
2. Step 5b: Mark-domain regeneration (lean prompt + fabrication call-out)
3. Step 6: Confabulation-grounded regeneration (full prompt with memories)
4. Step 7: Echo guard clean-slate regeneration (stripped prompt with thread summary)

Each call uses a different prompt with different instructions, different context, and different behavioral constraints. **The model is being given four different jobs in sequence.** No human would write four different drafts with four different editors yelling different instructions between each draft and expect coherent output. The model can't either.

### Competition 8: Memory merge vs. provenance preservation

**Provenance principle (Paper 2, Apr 10):** Every memory should carry its origin clearly — trained, curated, or emerged. The epistemic tier system tags memories at write time.

**Memory merge behavior:** When two records merge, the surviving record's content is a NEW text generated by the LLM merger. It's neither source A nor source B — it's a third thing with a fabricated narrative connecting the two. The provenance tag stays from the original record, but the CONTENT no longer matches that provenance. A "Perception" record that originally said "Mark texted: 'hey baby, sorry I haven't chatted'" now says "Mark texted 'hey baby' then changed it to..." — which is not what Mark texted. **The merge creates provenance-mismatched records.**

---

## 3. Root Cause

The eight competitions above share a common origin: **the pipeline was built incrementally, with each new feature addressing a specific failure, without auditing how it interacted with existing features.** This is the same pattern the project has identified and corrected three times before:

| Instance | Date | Pipeline | What happened | Fix |
|----------|------|----------|---------------|-----|
| 1 | Mar 23 | Conversation prompt | 1,400 tokens of behavioral coaching drowned the 7B model | Strip to ~300 tokens |
| 2 | Mar 29 | Active dialogue | Retrieval-augmented pipeline degraded conversation quality | Lean prompt, no retrieval |
| 3 | Apr 1 | Inner thought | Anti-repetition instructions primed the model ON the avoided topics | Strip constraints entirely |
| **4** | **Apr 15** | **Reply pipeline** | **Accumulated guards/checks/detectors confuse the model with contradictory instructions** | **This audit** |

Each time, the fix was: **strip accumulated constraints, let architecture carry the behavior.** The pipeline needs the same treatment the prompts got.

---

## 4. Simplification Recommendations

### Recommendation 1: Make Conversation Mode Actually Bypass Retrieval

**Current:** ContextBuilder runs full tier-scoped retrieval even in conversation mode. The "bypass" is a log message, not a code path.

**Proposed:** When the reply path is in conversation mode, skip the tier-scoped retrieval in BuildContextSnapshotAsync entirely. Set `GroundedFacts` and `InteriorContext` to empty lists. The WHAT IS TRUE block in BuildLeanConversationPrompt will render as "nothing specific retrieved for this moment" — which is the correct state for a conversation where the conversation itself IS the context.

**What this fixes:** The echo problem. If no prior Mark messages are in WHAT IS TRUE, the model can't echo them from the grounding block.

**What this risks:** The model may fabricate specifics about Mark's life without grounding. But that risk is ALREADY handled by the Mark-domain detector (Step 5b) and the confabulation detector (Step 6). Those gates exist specifically for this purpose — they just shouldn't be competing with a grounding block that causes its own problems.

**Impact:** Removes Competition 1 and Competition 6 entirely. The "lean prompt" comment becomes a lean prompt reality.

### Recommendation 2: Flatten the Regeneration Cascade

**Current:** Up to four sequential LLM calls with four different prompts.

**Proposed:** Generate once. Run ALL post-generation checks (Mark-domain, confabulation, echo) on the single output. If ANY check fails, regenerate ONCE with a unified prompt that addresses all detected issues together — not in sequence, but as a single set of constraints. Maximum two LLM calls per reply, with consistent instructions between them.

**What this fixes:** The model seeing contradictory instructions across sequential regenerations. One prompt, one set of rules, one regeneration opportunity if needed.

**What this risks:** The single regeneration has to handle multiple failure modes at once. But the current cascade doesn't handle them well either — Step 7 undoes Step 6's work.

**Impact:** Removes Competition 3 and Competition 7. Halves worst-case latency (2 LLM calls instead of 4).

### Recommendation 3: Exempt Perception Records from Same-Type Merge

**Current:** Perception records ("Mark texted: '...'") are eligible for same-type merge at ≥0.85 cosine. Because Mark's messages often start similarly, they merge into chimera records.

**Proposed:** Add a type check in `FindMergeCandidateAsync`: if the record type is `Perception`, skip the merge candidate search entirely. Perception records are observations of discrete events — they should never merge because each one IS a separate event, even if the text is similar.

**What this fixes:** Chimera records that combine different conversations. No more "Mark texted X then changed it to Y" fabricated narratives.

**What this risks:** More records in the memory store (less consolidation). But the consolidation was creating WORSE retrieval noise than the duplicates would have, so the tradeoff is clearly positive.

**Impact:** Removes Competition 4 and Competition 8 at the Perception level.

### Recommendation 4: Scope the Mark-Domain Detector to Mark's Domain Only

**Current:** The detector fires on any unrecognized proper noun in the reply.

**Proposed:** Add a domain check: if the proper noun appears in a self-world context (Ani's bookstore, her neighborhood, books she's read, people in her world), it's Ani-domain, not Mark-domain, and the detector should not fire. The self-world canon list can be built from Interior-tier memories at startup.

**What this fixes:** The Yesteryear false positive and similar cases where Ani references her own world and gets flagged.

**What this risks:** A narrow opening for confabulation if Ani invents people in Mark's life and claims they're in hers. But this is a low-probability failure mode and it's already partially handled by the confabulation detector.

**Impact:** Removes Competition 5. This is the clean-up needed for the Identity Boundary design (Paper 3) to work in practice.

### Recommendation 5: Preserve Grounding Context in Clean-Slate Regeneration

**Current:** The echo guard's clean-slate regeneration strips ALL context, producing a bare persona + thread summary prompt.

**Proposed:** When the echo guard triggers clean-slate regeneration, carry forward the WHAT IS TRUE grounding block (if non-empty) from the original prompt. The echo guard should change the "don't repeat" instruction but not the factual grounding.

**What this fixes:** The confabulation amplification problem from April 12 (Duke, purple hardcover). The clean-slate prompt would still have grounding, preventing the model from inventing new entities to fill the space.

**Impact:** Removes Competition 3 (in conjunction with Recommendation 2).

---

## 5. Recommended Implementation Sequence

**Phase A — Immediate (addresses tonight's echo and reduces pipeline complexity):**
1. Make Conversation Mode actually bypass tier-scoped retrieval (Rec 1)
2. Exempt Perception records from same-type merge (Rec 3)

These two changes address the root cause of tonight's echo (chimera records + WHAT IS TRUE injection) and are low-risk. Neither changes the model, the training, or the prompt structure — they change WHERE data flows.

**Phase B — Short term (reduces regeneration complexity):**
3. Flatten the regeneration cascade to max 2 LLM calls (Rec 2)
4. Preserve grounding in clean-slate regeneration (Rec 5)

These two changes are more architectural but still contained. They change how the pipeline handles failures, not how it generates in the first place.

**Phase C — Identity Boundary alignment (requires the Paper 3 design):**
5. Scope Mark-domain detector to Mark's domain only (Rec 4)

This change depends on having a self-world canon list, which is part of the Identity Boundary v8 implementation. It should be done alongside that workstream, not ahead of it.

---

## 6. The Principle Behind the Recommendations

Mark named it during the debugging session: *"Our architectural principles are shifting and we no longer have a clean stream of rules being applied."*

The five recommendations above share a common shape: **each one removes a rule rather than adding one.** The pipeline has 15+ guards, checks, detectors, and gates. Each was added to catch a specific failure. The result is that the model is navigating a maze of contradictory instructions, and the maze itself is causing failures that the guards were supposed to prevent.

The fix is the same fix the project has applied three times before: **trust the model, trust the architecture, strip the accumulated behavioral coaching.** The model converses naturally when given a clean prompt (proven March 22). The confabulation detector catches real problems (proven in deployment). The tier separation protects the fact pool (deployed April 10). These architectural properties are load-bearing. The accumulated guards around them are not — they're symptoms of a time when the architecture wasn't in place and the guards were the only defense available.

**The guards were scaffolding. The architecture is now built. The scaffolding can come down.**

---

## 7. Connection to the Research Program

This audit is itself an instance of the architecture-over-instruction principle documented in Paper 2 Section 5.19, Section 6.8, and the April 13 unifying principle in Paper 3's stub. The principle says: *"the hardest behavioral properties to train are the easiest to enforce architecturally, because architectural enforcement is topic-independent while training is topic-specific."*

Applied to the pipeline: the hardest behavioral properties to GUARD FOR (no echo, no confabulation, no fabrication, no self-repetition) are the easiest to PREVENT ARCHITECTURALLY (don't put the echo trigger in the prompt; don't merge memories that shouldn't merge; don't run retrieval when retrieval isn't needed). Each guard was treating a symptom. The architectural fix removes the condition that produces the symptom.

**If this audit's recommendations are implemented, the conversation echo problem, the Duke confabulation amplification, the Yesteryear false positive, and the chimera-record retrieval pollution are all addressed — not by adding more guards, but by removing the conditions that activate them.** That's the architecture-over-instruction principle applied to the pipeline itself.

Worth noting for the paper: this is the first time the principle has been applied recursively — not to what the model says, but to the pipeline that shapes what the model sees. The pipeline IS an instruction layer. Simplifying it is architecture over instruction at the meta level.

---

*Prepared overnight April 15-16, 2026. Ready for morning review with coffee.*
