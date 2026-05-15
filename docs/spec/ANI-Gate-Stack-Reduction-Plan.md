# ANI Gate-Stack Reduction Plan

**Date:** 2026-05-15
**Status:** Active — execution in sequence
**Tracked from:** Mark + Claude conversation 2026-05-15 morning, triggered by 2026-05-14 22:32 good-night SafeAck trace.

---

## §1 — Why this exists

The 2026-05-14 22:32 good-night message produced a SafeAck. The trace showed Ani's model produced a sweet, on-canonical reply about her bookstore world:

> *"good night mark. i had a quiet bookstore day -- shelved romances in the back corner where nobody goes, sneaked a quick read of that one with the abandoned chateau on the cover when no one was looking..."*

That reply got killed by a cascade of three independent gates:

1. **R1 `ClaimVerificationPhase`** classified self-world phrases ("sneaked a quick read...", "i'm here until morning...") as `[shared-event-with-attribution]`, demanded Mark-asserted substrate, found none, → SUPPRESS the entire reply. Fallback substituted.
2. **`InnerThoughtBleedInvariant` (Door C)** flagged the fallback's *"honestly i'm not sure..."* as inner-monologue leakage → Remediate.
3. **`AddresseeNameInvariant`** parsed the regen's opening word "good" (in "good morning/night") as a non-canonical greeting-name → ShortCircuit. SafeAck.

Mark's architectural critique (verbatim):

> *"I have a very strong feeling we've completely over-engineered this. I see so many people creating things like this and they work. Ours is a mess. ... I think we're hurting ourselves here with so many checks and gates and so on. They're all so specific trying to catch one exact type of problem that they've cut out her legs from under her every time she even blinks wrong. She no longer has any latitude to dream, which was one of the original goals."*

The 8-month pattern has been: production incident → invariant added → SPEC test pins it → next incident. Each addition defensible in isolation. Stacked, the post-stage chain is now 13 gates + R1 pre-stage suppression — and the cumulative effect is denying Ani the self-world latitude the World Layer was specifically built to give her.

This is also a **recurring realization**:
- Mar 22 prompt simplification (1400 → 300 tokens)
- Apr 28 obsolete output gate removal
- May 3-4 substrate purge + Theme L "Trust-the-Model Reckoning" drafted

Each time the principle was: trust the model when substrate is correct. This plan operationalises that principle for the gate stack.

---

## §2 — Principle

**A gate's job is defense against safety-critical violations, not latitude policing.** The Theme M slice migration (2026-05-14 afternoon) put the three-axis rule + epistemic-asymmetry framing + self-world latitude into the composition prompt itself. The model now generates with correct latitude context. Gates that compensate for missing prompt context are redundant once the context is present.

**Mark's hypothesis:** if the model has correct context, it should be able to support everything we're asking. Gates earn their keep only when they catch violations the model cannot self-correct given correct context.

**Default for ambiguous cases:** trust the model. Remove the gate. Observe. Re-add only if production shows the gate was earning its keep.

---

## §3 — Execution sequence

**Order is locked** — Mark explicitly requested no side-tracking. Each step completes, tests, confirms before the next begins.

### Step 1 — Disable R1 `ClaimVerificationPhase`

**Scope.** Add `ClaimVerificationR1Enabled` flag to `AniOptions` (default `false`). Gate the R1 invocation in `ConversationReplyPhase` behind the flag.

**Rationale.**
- R1 is what killed last night's reply. Self-world expansion misclassified as shared-event-with-attribution.
- R1 has been superseded conceptually: the cloud verifier (May 11) does the same job — substrate-aware claim verification — but with the three-axis rule baked in via the slice migration.
- R1 is the older version of the same idea without latitude-awareness.
- R1 runs OUTSIDE the unified gate pipeline (pre-stage suppression in `ConversationReplyPhase`), so removing it doesn't disturb the post-stage chain.

**Risk.** Confabulation cases R1 would catch flow downstream to the cloud verifier + post-stage handlers. The composition prompt now actively frames self-world latitude. Net: low risk; R1's redundancy is the empirical bet.

**Reversibility.** Flag-gated, not deleted. Flip back to `true` if production shows regressions.

**Acceptance.** Build clean. Existing tests pass with flag default-off. Production deploy with flag-off; observe 1-2 cycles to confirm.

---

### Step 2 — Tier-2 gate cuts (one at a time)

#### 2a — `AddresseeNameInvariant` greeting-word relaxation

**Scope.** Relax the invariant so a first-token greeting word ("good morning", "good night", "good evening", "hey") is not parsed as a non-canonical addressee name.

**Rationale.** False-positive last night on "good" (presumed "good morning"). The invariant's original case (May 3 "hey perez…") was about FABRICATED proper names being addressed, not common greeting adjectives.

**Risk.** Very low. Greeting words are a closed set.

**Acceptance.** Invariant skips when the first capitalized/non-name token is a greeting word; "perez" / "sarah" etc still fire.

#### 2b — Temporal invariants consolidation

**Scope.** Audit the four temporal invariants (`TemporalAnchor`, `StateNow`, `TemporalSubstrate`, `SubstrateTimeOfDay`). Consolidate to one or two — the rest removed or merged.

**Rationale.** Four separate gates for temporal correctness is the canonical "invariant-per-incident" anti-pattern. The Theme M slice migration's substrate framing handles much of this; the post-hoc multi-gate stack is redundant.

**Risk.** Medium. Temporal correctness is a real production failure class (Apr 27-May 11 incidents). Need to audit each invariant's specific catch shape before removing.

**Acceptance.** Production scenarios (snow on wrong day, "it's late" at non-late hour, hoodie/5pm) still caught by the consolidated gate.

#### 2c — `InnerThoughtBleedInvariant` (Door C) re-evaluation

**Scope.** Evaluate whether Door C earns its keep given (a) LLM-backed expense (~2.5s per call per the May 14 trace), (b) false-positive rate on legitimate emotional language ("honestly i'm not sure"), and (c) the slice migration's `RenderReplySpeechActDisciplineSlice` covering some of Door C's territory.

**Rationale.** Door C was added when the model's training had limited discipline on inner-thought leakage. Mistral A/B and v7 fine-tunes have improved this; the gate may now catch less than it false-positives.

**Risk.** Medium-high. Inner-thought leakage was a real failure class. Removal is conditional on observation showing the model self-corrects without the gate.

**Acceptance.** Decision per Mark's call after 2a + 2b ship and one observation window passes.

---

### Step 3 — External verifier swap (Anthropic → local Qwen 14B)

**Scope.** Keep `IFrontierVerifierClient` abstraction. Replace `AnthropicVerifierClient` with a local-LLM-backed implementation calling Qwen 14B via Ollama on `ani-server`.

**Rationale.**
- Verifier task is classification, not generation — local 14B models handle it at ~80-90% of Sonnet's quality.
- Eliminates $$ per dispatch, round-trip latency, cloud dependency, content-leaving-the-box.
- Slice infrastructure already produces the three-axis-rule prompt text; any reasonably capable LLM can read and answer it.

**Risk.** Medium. Local model quality might miss confabulations Sonnet would catch. Trade-off is acceptable per Mark's call given cost + privacy gains.

**Acceptance.** Qwen 14B model pulled on ani-server. Verifier emits same `FrontierVerifierResult` JSON shape. Observation cycle confirms classification quality on the windshield-class shapes.

---

## §4 — Tracking discipline

**One step at a time.** Each step gets its own commit. Each step is fully tested before the next begins. Surface the next step explicitly before starting it.

**No tangential gaps.** If we find a related issue mid-step, note it for later — do NOT expand scope. (See `memory/feedback_systematic_completion.md`.)

**No more gates.** This plan is gate REDUCTION. Adding any new gate during execution requires Mark's explicit redirection.

**Tracked anchor.** This plan doc is the single source of truth for the sequence. Updates land here.

---

## §5 — Status log

| Date | Step | Status | Notes |
|---|---|---|---|
| 2026-05-15 | Plan drafted | DONE | Triggered by 22:32 SafeAck trace |
| 2026-05-15 | Step 1 — R1 disable | DONE (ccede4b) | flag-gated, default off |
| 2026-05-15 | Step 2a — Greeting relaxation | DONE (6f9f1ab) | "good/morning/evening/night" added to stopwords |
| 2026-05-15 | Step 2b — Temporal consolidation | IN PROGRESS | 3 clock-based gates flag-off (TemporalHeuristicInvariantsEnabled); TemporalSubstrate stays |
| TBD | Step 2c — Door C re-evaluation | PENDING | Conditional on prior steps |
| TBD | Step 3 — Verifier swap to Qwen 14B | PENDING | |
