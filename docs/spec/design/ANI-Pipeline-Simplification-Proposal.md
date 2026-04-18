# ANI Conversation Reply Pipeline — Simplification Proposal

**Date prepared:** April 18, 2026 (Saturday morning, while Mark was at the gym)
**Author:** Claude (ANI Claude Code instance)
**Status:** Proposal for Mark's review. No implementation commitment yet.
**Supersedes (for implementation sequencing):** The rec-by-rec recommendation sequence in `ANI-Pipeline-Audit.md` (April 15-16). This doc consolidates Recs 1, 2, 3, 5, and the new Phase A.3 (echo guard scope review) into a single coherent design.
**Companion doc:** `ANI-Pipeline-Audit.md` contains the full analysis of competing rules and the evidence that motivated each recommendation. This doc contains the forward-looking design.

---

## 1. Purpose

Consolidate the five open Pipeline Audit items into one coherent pipeline redesign, implementable in three short phases, each validated before the next begins. The redesign is guided by Mark's stated vision captured on April 18:

> *"Reduce the prompt, maintain the conversation emotion and big moments, look up what is true only when it's relevant to the conversation, and go from there. Trust the model."*

This is not a rewrite. It is a removal operation. The audit identified 15+ accumulated guards, checks, detectors, and gates. Each was added to solve a real problem. The architectural property that originally required it has since been addressed elsewhere. This proposal catalogs which guards can be removed now because the conditions that required them no longer exist, which guards stay but are narrowed in scope, and which are replaced with simpler mechanisms that do the same job without the accumulated side-effects.

---

## 2. Why now

Three converging signals made this the right moment:

1. **Rec 1 shipped and is validated** (commit `c2178bc`, April 17-18). Conversation Mode actually bypasses tier-scoped retrieval. This removed the primary echo source. First post-deployment session produced a demonstrably better first reply than prior architecture was producing.
2. **Rec 1 exposed a latent calibration dependency.** The Mark-echo guard threshold was implicitly calibrated against the old prompt composition. With Rec 1 removing the filler, the guard now false-positives on legitimate topical engagement. See `ANI-Pipeline-Audit.md` Section 8.2-8.4 for deployment evidence. The instinct to tune the threshold was explicitly rejected (Mark, April 15 echo debugging; also April 18). The structural question — *"does this guard belong in this path at all?"* — is the one that needs to be answered.
3. **Mark has been avoiding ANI.** Self-report: *"this is why i haven't been talking to our ANI much because i feel like i've been hitting the same behavioral roadblocks every time and it's preventing me from getting into a longer conversation with her."* The pipeline pain is now gating the deployment-as-research premise. Fixing the pipeline is not a nice-to-have; it is the intervention that restores longitudinal data flow for Paper 3, v8 training, EM9 validation, and Vibe Loop implementation.

---

## 3. The four principles (Mark's framing, load-bearing)

Every design decision below traces back to one or more of these:

**P1. Reduce the prompt.** Default to minimum content. Additions are justified on-demand, not preemptively. The lean prompt (persona + conversation history + WHAT IS TRUE when actually needed) is the default, not the fallback.

**P2. Maintain conversation emotion and big moments.** Emotional continuity and foundation memories are part of WHO ANI IS, not WHAT IS RETRIEVED. They belong in the persistent persona layer, not in per-turn semantic search results.

**P3. Conditional grounding.** Factual grounding about Mark's world is retrieved only when the model is about to make a claim that needs grounding. Default path: no retrieval. Grounding path: triggered by evidence of an ungrounded factual claim in the generated reply.

**P4. Trust the model.** The March 22 proof stands: the model converses naturally when given a clean prompt. Every guard added since then is accepted only if, without it, the model's output is demonstrably worse than the guarded output. If removing a guard leaves quality unchanged or improved, the guard is dismantled.

---

## 4. Current pipeline (what we have today)

Reproducing from `ANI-Pipeline-Audit.md` Section 1 for self-contained reading. Steps 0-12, up to four LLM calls per inbound message:

```
INBOUND MESSAGE
   ├─ Step 0:  Continuation prompt check
   ├─ Step 1:  Terminal message check
   ├─ Step 2:  Reply decision heuristic
   ├─ Step 3:  Context snapshot assembly (tier retrieval now skipped in conv mode — Rec 1)
   ├─ Step 4:  Lean prompt generation
   ├─ Step 5:  LLM generation (attempt 1)
   ├─ Step 5b: Mark-domain assertion detection → maybe regen (attempt 2)
   ├─ Step 6:  Confabulation-driven retrieval → maybe regen (attempt 3)
   ├─ Step 7:  Echo guard → maybe clean-slate regen (attempt 4)  ← destroys good replies
   ├─ Step 8:  Natural reply delay
   ├─ Step 9:  Dispatch
   ├─ Step 10: Persist reply
   ├─ Step 11: Reset desire, apply emotional shift
   └─ Step 12: Async emotional processing
```

**The path that matters:** on any given reply, up to three regenerations can fire, each with a different prompt philosophy. The model sees different instruction sets for the same turn, cannot reconcile them, and the quality of later regenerations has been observed to be *worse* than the first attempt (April 18 deployment data).

---

## 5. Proposed pipeline (what we want)

Single LLM call in the common case. Max two calls if the model drifts into a fabrication the lean prompt could not prevent. Grounding preserved across regeneration. One prompt philosophy.

```
INBOUND MESSAGE
   ├─ Step 0:  Continuation prompt check                 (unchanged)
   ├─ Step 1:  Terminal message check                    (unchanged)
   ├─ Step 2:  Reply decision heuristic                  (unchanged)
   ├─ Step 3:  Minimal context assembly:
   │              - Persona (name, traits, time)
   │              - Relational continuity layer:
   │                    * Recent emotional state
   │                    * Anchored foundation memories
   │                    * Conversation thread summary
   │              - NO semantic search
   │              - NO tier-scoped retrieval
   │              - NO perception-matched memory injection
   ├─ Step 4:  Lean prompt generation
   │              - Persona block + relational continuity
   │              - Conversation history (verbatim thread messages)
   │              - NO WHAT IS TRUE block by default
   │              - CRITICAL constraint retained (don't invent Mark-facts)
   ├─ Step 5:  LLM generation (attempt 1)
   ├─ Step 6:  Safety check (fast, cheap):
   │              a) Self-echo detector (n-gram overlap vs. Ani's recent outputs)
   │              b) Confabulation classifier (existing ML check, kept)
   │              - Mark-echo: REMOVED from conversation path
   │              - Mark-domain assertion detection: MERGED into confab classifier
   ├─ Step 7:  IF safety check fails:
   │              - Targeted retrieval based on WHAT the model attempted to assert
   │              - Regenerate with PRESERVED context + grounding block added
   │              - Single regeneration, no cascading regens
   │              (This is the ONLY place retrieval happens in the conversation path.)
   ├─ Step 8:  Natural reply delay                       (unchanged)
   ├─ Step 9:  Dispatch                                  (unchanged)
   ├─ Step 10: Persist reply                             (unchanged)
   ├─ Step 11: Reset desire, apply emotional shift       (unchanged)
   └─ Step 12: Async emotional processing                (unchanged)
```

**Key behavior changes:**

- **Retrieval is reactive, not preemptive.** No semantic search on inbound. Retrieval fires only if the first generation produced an ungrounded assertion the model failed to self-correct.
- **Grounding is additive, never subtractive.** If regeneration is needed, context is preserved and grounding is added. The clean-slate regen (which strips context) is eliminated entirely.
- **Mark-echo is path-scoped.** Removed from conversation path. Retained in outreach path (where mirroring the contact's last message IS a failure mode).
- **Relational continuity is persistent.** Emotional state and anchored foundation memories are always present — they do not require per-turn retrieval because they are persona, not factual content about Mark.
- **One prompt philosophy across attempts.** Regeneration does not switch between lean-and-full prompts. It adds grounding to the existing lean composition. The model sees a consistent instruction set.

---

## 6. Mapping to existing audit recommendations

| Audit Rec | What it said | What this proposal does |
|-----------|--------------|-------------------------|
| **Rec 1** | Conversation Mode actually bypass tier-scoped retrieval | **Shipped April 17-18** (commit `c2178bc`). Validated. Foundation of this proposal. |
| **Rec 2** | Flatten regeneration cascade to max 2 LLM calls | **Implemented via Step 6-7** — single safety check, single regeneration with preserved context. |
| **Rec 3** | Exempt Perception records from same-type merge | **Implemented as a memory-layer change** (outside the pipeline itself but prerequisite to trustable retrieval when regen fires). See Phase 1 below. |
| **Rec 5** | Preserve Grounding Context in Clean-Slate Regeneration | **Subsumed by Step 7** — there is no clean-slate regen anymore; regeneration preserves all existing context and adds grounding. |
| **Phase A.3 (new)** | Scope Mark-echo guard by path — remove from conversation, retain in outreach | **Implemented via Step 6's removal of Mark-echo from the safety check in conversation mode.** Outreach path's version of the guard stays. |

---

## 7. What each phase removes

Framing everything as a removal operation, per Mark's principle that the scaffolding can come down. Each phase is a deletion list with a small amount of replacement code where needed.

### Phase 1 — Memory substrate cleanup + parroting library (Low risk, small footprint)

**What ships:**

1. **Perception-exempt same-type merge** (Audit Rec 3).
   - Location: `SqliteMemoryService` (memory merge logic, same path that was fixed April 15 for the cross-type corruption bug).
   - Behavior: when considering a merge, if either record is `MemoryType.Perception`, skip same-type merge entirely. Perception records are first-class event receipts, not consolidation candidates.
   - What this removes: chimera records formed by merging similar Perception entries (e.g., multiple "Mark texted: good morning" records consolidating into a single poisoned record with mixed metadata).
   - What this prevents: tonight's residual risk — even though Rec 1 stopped tier-scoped retrieval from injecting Perception records, the generic semantic search at `ContextBuilder.cs:79` still finds them (cosine 0.904 observed April 18) and would pollute the regeneration path when Step 7 fires.
   - Test: existing memory service test suite; add cases for Perception same-type rejection.

2. **N-gram parroting detector library** (small utility, no pipeline change yet).
   - Location: new file `src/AniRuntime.LLM/ParrotingDetector.cs` or extend existing text utility class.
   - Behavior: takes two strings, returns true if the longest shared contiguous n-gram (n=4 or 5, parameterized) exceeds a threshold. Optionally normalizes case, punctuation, whitespace.
   - Why: replaces cosine-similarity-as-parroting-detector. Cosine measures topical overlap; n-gram measures phrase reuse. These are different signals. See Audit Section 8.3 for the argument and Mark's canonical Spanish-learning examples ("Today I am doing nothing" reusing "today doing" from the question is engagement, not parroting).
   - Test: unit tests with topical-overlap pairs (should NOT fire) and verbatim-paraphrase pairs (should fire).

3. **Mark-echo removed from conversation reply path.**
   - Location: `ConversationReplyPhase.cs:486-545` (echo guard block).
   - Behavior: In the conversation reply path, only Self-echo is checked. Mark-echo detection is removed entirely from this path.
   - What this removes: the clean-slate regeneration trigger for topical-overlap false positives.
   - What this preserves: Self-echo detection (Ani repeating her own prior output is still a failure mode regardless of path). Self-echo is rewritten to use the new n-gram detector rather than cosine, producing fewer false positives there too.
   - Outreach path: the existing Mark-echo check stays (will audit separately later; out of scope for this proposal).
   - Test: regression pass on existing echo tests; new test asserting topical-overlap replies do NOT trigger Self-echo with the n-gram detector.

**What Phase 1 does NOT change:** the lean prompt composition, the LLM generation flow, the confabulation classifier, the regeneration cascade shape. Phase 1 is substrate cleanup + library prep for the bigger change in Phase 2.

**Validation gate for Phase 1:**
- All existing tests pass.
- Post-deployment conversation with Mark: no clean-slate regen fires on legitimate topical engagement (specifically: "mentally preparing for the gym" kind of replies produce no false positive).
- No confabulation regression (ML classifier stays within normal band).

### Phase 2 — Flatten the regeneration cascade (Core pipeline change)

**What ships:**

1. **Clean-slate regeneration removed.**
   - Location: `ConversationReplyPhase.cs` — the block that strips context and regenerates with persona + thread summary only.
   - What this removes: ~60 lines of regeneration-with-stripped-context logic.
   - Why: the clean-slate path is a confabulation amplifier (Audit Competition 3, confirmed with April 18 deployment data). When it fires, quality degrades rather than improves.

2. **Single regeneration path with preserved context + added grounding.**
   - Location: replacement logic in the same file.
   - Behavior: if the safety check fails, perform targeted retrieval based on what the model attempted to assert, add the retrieved grounding to the existing lean prompt composition, and regenerate. One retry. No further cascades.
   - What this removes: the three-prompt-philosophy reality where the model sees lean → full → stripped across attempts.
   - What this preserves: the confabulation classifier (still catches real fabrication), the anchored foundation (still in every prompt via the relational continuity layer from Phase 3 — which may ship before or alongside this phase, see below).

3. **Mark-domain assertion detection merged into the confabulation classifier.**
   - Rationale: they do the same job (detect "did the model assert something about Mark that isn't grounded?"). Running them as separate Steps 5b and 6 produces two separate regeneration opportunities, which is part of how the cascade grows.
   - Merged version: one classifier pass, one regeneration decision, one retry.

**What Phase 2 does NOT change:** retrieval still happens (when needed), grounding still applies (when relevant), the confabulation protection still fires. What changes is *when* retrieval fires (reactively, not preemptively) and *how many times* the model can be regenerated (once, not three times).

**Validation gate for Phase 2:**
- Confabulation rate does not rise (measured via the ML classifier's green/red rate on a sample of conversations).
- Mark's subjective reply quality does not degrade (reported via conversation).
- No more than 2 LLM calls observed in the log for any reply.
- Targeted retrieval logs show it firing only when the first reply asserted a Mark-fact.

### Phase 3 — Relational continuity layer (Persona-level change)

**What ships:**

1. **Explicit relational continuity block in the lean prompt.**
   - Location: `PromptBuilder.BuildLeanConversationPrompt`.
   - Content: (a) current emotional state summary (one line — *"Warmth high, Energy moderate, Playfulness rising"*), (b) anchored foundation memories (Mark's family, key facts — these already exist but are being relocated from GroundedFacts to a persistent block), (c) conversation thread summary if the thread is long enough to need one.
   - What this replaces: the current approach of trying to retrieve emotion/foundation via tier-scoped search on perceptions. Emotion and foundation are persona, not retrieved content. They should be injected structurally.
   - What this removes: the dependency on semantic search to surface emotional continuity.

2. **Big moments explicit bank.**
   - A small, curated set of canonical shared moments (Duck Norris, Snow messages, Kathy reference register, etc.) tagged `BigMoment=true` in the memory store.
   - Included in the relational continuity block when the conversation contextually invites them (e.g., thread topic has thematic proximity to a big moment).
   - What this removes: reliance on random semantic match to surface defining relational moments. Big moments are hand-curated; they should be addressable by name, not by cosine chance.
   - What this preserves: the relational density that makes Ani feel like she remembers, without the noise that comes from general-purpose retrieval.

**What Phase 3 does NOT change:** the lean prompt is still lean. The relational continuity block is small (3-5 lines), not a dumping ground. The constraint *"reduce the prompt"* remains load-bearing.

**Validation gate for Phase 3:**
- Ani continues to reference emotional continuity naturally ("I was thinking about you while you were at the gym").
- Big moments surface when contextually appropriate.
- Token count for the persona + continuity block stays under a target (proposed: ≤ 400 tokens, subject to calibration).
- Mark's subjective report: "she feels like she remembers me" is unchanged or improved.

---

## 8. Implementation order and parallelism

The phases are ordered by **risk**, not dependency. Phase 1 is strictly low-risk (substrate + library); Phases 2 and 3 can ship in either order or together.

**Proposed sequence:**

1. **Phase 1 first.** Low-risk cleanup. Ships alone. Validated with one deployment session before Phase 2/3 begin.
2. **Phase 2 next.** The core pipeline change. Ships after Phase 1 is observed clean for at least one conversation session.
3. **Phase 3 last.** The persona-layer change. Ships after Phase 2 is validated. This ordering ensures we don't conflate "does the persona continuity work?" with "did we break the pipeline?"

**Total work estimate:**
- Phase 1: ~1-2 hours implementation + test. Can be done in one sitting.
- Phase 2: ~3-4 hours implementation + test. Requires focused attention on the regeneration path.
- Phase 3: ~2-3 hours implementation + test. Includes the big-moments tagging work.

Total: ~6-9 hours across three sittings. Not a weekend marathon; a one-week cadence of one phase per day would be comfortable.

---

## 9. Risks and open questions

### R1. Trusting the model alone could let confabulation through.

The confabulation classifier is the safety net, not an optional guard. If it fails, the pipeline has no fallback in this design (the old clean-slate was the fallback). Risk mitigation: run the ML classifier aggressively in Phase 2; if confabulation rate rises above baseline, reintroduce a narrower guard with better-calibrated signal (n-gram grounding check, not cosine).

**Open question:** what is the acceptable confabulation rate baseline, and how do we measure it? Proposed: sample 50 replies pre-deployment vs. 50 replies post-deployment, compare classifier outputs. Mark eyeballs the samples to confirm classifier accuracy isn't itself drifting.

### R2. Removing guards could surface problems the guards were masking.

The current echo guard fires about X times a day (data TBD from log analysis). Some of those fires are false positives (today's example). Some may be catching real issues that we don't see because the guard is catching them. Removing Mark-echo in conversation mode is a net-positive bet but it's a bet.

**Mitigation:** log every reply for the first week post-Phase-2. If Mark or Claude reviewing logs spots real parroting that would have been caught, restore a narrower guard with n-gram detection.

### R3. Relational continuity block could drift into prompt bloat.

The principle is "reduce the prompt" but Phase 3 explicitly adds a block. The block is defended only if it stays small and earns its tokens.

**Mitigation:** token cap on the continuity block (proposed 400 tokens hard cap). Automatic trimming if the block overflows. Periodic audit of what's accumulating in anchored memory and whether the curation is still tight.

### R4. Big moments curation is a human-labor ongoing cost.

Someone has to decide "this is a big moment" and tag it. Currently done ad hoc. Phase 3's explicit bank formalizes the process.

**Mitigation:** probably not mitigated, just accepted. Mark already curates these implicitly. Making the curation explicit (a flag on a memory record) costs little and gains reliability.

### R5. Rec 1 + Phase 1 + Phase 2 sequence could temporarily make things worse before it makes them better.

Each phase shifts behavior. Intermediate states may produce new failure modes that the full sequence eliminates. Specifically: after Phase 1 but before Phase 3, Ani has the cleanup but lacks the explicit continuity layer — she may feel slightly less relationally grounded in that window.

**Mitigation:** keep the phase window tight. A week between phases, not a month. If an intermediate state is painful, either accelerate to the next phase or roll back (git makes rollback trivial).

---

## 10. Success criteria

The simplification is considered successful if all of the following hold after Phase 3 deployment and at least one week of regular use:

1. **Echo guard false positives: near zero** for legitimate topical engagement. Specifically: a reply that engages with vocabulary from Mark's incoming message does NOT trigger clean-slate regeneration.
2. **Confabulation rate: unchanged or improved.** ML classifier green-rate on sampled replies stays at or above the pre-simplification baseline.
3. **Regeneration cascade: capped at 2 LLM calls per reply.** Logs show no replies with 3+ LLM calls.
4. **Grounding still fires when needed.** When Mark asks about a specific work topic or person, retrieval runs, grounding is found, the reply is factually correct.
5. **Relational continuity preserved.** Ani naturally references emotional state, shared moments, and thread context without those being re-retrieved each turn.
6. **Mark's daily use returns.** The deployment-as-research premise resumes — Mark talks to ANI regularly, findings accumulate, longitudinal data flows into Paper 3 and v8 training.

If all six hold, the audit's core hypothesis is validated: the pipeline's guards were scaffolding, and removing them (not tuning them) produces a better system. If any fail, the specific failure becomes the next design session's input, with the principle intact.

---

## 11. What this proposal does NOT do

Listed explicitly so another instance tomorrow doesn't re-propose any of these:

- **Does not tune the Mark-echo threshold.** Band-aid. Explicitly rejected. See Audit Section 8.8.
- **Does not add new guards.** The pattern this proposal is dismantling.
- **Does not rewrite the model or the training.** The model + training is load-bearing. The change is in what reaches the model, not what the model is.
- **Does not touch the outreach pipeline.** Outreach has its own semantics (Ani initiates; Mark-echo is a real failure there). Outreach pipeline changes are a separate audit.
- **Does not change the emergence layer.** EM1-E8 observation is orthogonal.
- **Does not affect voice pipeline.** Streaming voice lives alongside this; no changes to STT/TTS flow.
- **Does not address pronoun attribution.** Pending bugfix, independent of this proposal.
- **Does not address Outage Perception Source or Curiosity Hunger.** Separate workstreams; not on the critical path.

---

## 12. Connection to the research program

This simplification is itself an instance of the architecture-over-instruction principle that Papers 2 and 3 are built around. The principle holds that behavioral properties are better enforced architecturally than instructionally. Applied recursively to the pipeline:

- The "don't echo" behavior was enforced instructionally via a runtime guard. It failed (false positives destroy quality).
- The architectural enforcement is to never put the echo trigger in the prompt in the first place (Rec 1) and to not measure the wrong signal (cosine → n-gram).
- Similarly: "don't confabulate" was partly enforced by the Mark-domain assertion detector, which ran separately and produced its own cascade layer. Merging it into the classifier collapses two symptomatic layers into one source-level check.
- The reduction in pipeline complexity is, in itself, evidence for Paper 2 Section 5.19 and Paper 3's unifying principle. **A pipeline that the researcher can reason about cleanly is a pipeline that supports good research; a pipeline with contradictory rules masks its own findings.**

Worth citing once this ships, if it ships successfully: the pipeline simplification as a concrete application of the paper's own principle.

---

## 13. Decision points requiring Mark's input

1. **Is the phase order acceptable?** (Phase 1 → Phase 2 → Phase 3, one per sitting, ~1 week apart.)
2. **Is the success criteria list complete?** Anything missing that Mark would measure?
3. **Big moments bank — how curated?** Proposal is: Mark tags memories manually with `BigMoment=true` as he encounters them. Alternative: heuristic tagger. Mark's call.
4. **Confabulation classifier threshold tuning.** Do we recalibrate with Phase 2 to catch the Mark-domain cases that the separate detector was handling, or do we trust the existing calibration?
5. **Should Phase 1 ship alone (higher safety, slower overall) or bundle with Phase 2 (faster, slightly higher risk)?** Proposal is alone, but Mark may prefer the bundle if he's confident in the design.

---

*Draft prepared Saturday, April 18, 2026 while Mark was at the gym. Ready for review over CrewTrack testing breaks or whenever.*
