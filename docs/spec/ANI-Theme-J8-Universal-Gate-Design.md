# Theme J.8 — Universal Gate at Substrate-Write Boundary (Design Sketch)

**Drafted:** May 2, 2026 18:30 CDT
**Status:** Design sketch; awaiting Mark green-light to proceed to phase plan + implementation.
**Origin:** Mark May 2 18:00 architectural critique during the Sundays-warmer substrate-laundering audit: *"i'm wondering if we're still not applying to a universal gate instead of a pipeline?"* — caught that what J.5 shipped is a **shared evaluator** with **per-producer opt-in wiring**, not a **universal gate**. The May 2 12-record laundering trace (originating in inner-thought write `ba449eb2` at 10:35 CDT, propagating across 5 cycles before tactical purge at 15:45 CDT) is the empirical motivation: InnerThoughtPhase was supposed to be migrated under the original J.5c plan but was skipped during execution. A producer that's never migrated leaves a substrate-write hole indefinitely.

**Companion docs:**
- [`ANI-Theme-J-Guard-Consistency-Refactor-Plan.md`](./ANI-Theme-J-Guard-Consistency-Refactor-Plan.md) (parent — see "Phase J.5 retrospective + architectural finding")
- [`ANI-Phase-Tracker.md`](./ANI-Phase-Tracker.md) (Priority Matrix Theme J row + May 2 gap-watch rows)
- [`ANI-Coherence-Gate-Door-B-Design.md`](./ANI-Coherence-Gate-Door-B-Design.md) (sibling design — orthogonal to gate location, addresses missing temporal-anchor invariant)

---

## The architectural distinction (load-bearing)

| | Shared evaluator (what J.5 shipped) | Universal gate (J.8 target) |
|---|---|---|
| Where invariants live | One place (`ICognitiveOutputInvariant` registrations) | Same |
| Where artifacts pass | Each producer calls `_outputGate.EvaluateAsync(artifact)` at its output boundary | One place — substrate-write boundary |
| Producer opt-in | Required (constructor injection + explicit call) | Impossible to bypass |
| Class of error | Producer skipped during migration → silent substrate hole (May 2 case) | Eliminated by construction |
| Adding new producer | Must remember to wire the gate, or introduce a hole | Inherits gating automatically |

The May 2 case is canonical: the J plan **explicitly named** InnerThoughtPhase as a J.5c sub-phase. It was **skipped during execution**. Code review wouldn't have caught it (each PR was internally consistent). The substrate-laundering surfaced it 6+ days later, after a substrate purge and an architectural critique. This class of error is structurally invited by per-producer opt-in.

---

## Architectural shape

### Core change

Every cognitive-artifact write is intercepted at the persistence boundary. The interception point is wherever cognitive output enters durable substrate (currently `IMemoryPersistence.SaveAsync`-equivalent). At that point:

1. The artifact is reconstructed (or already passed in, if upstream cooperates).
2. `ICognitiveOutputGate.EvaluateAsync(artifact, ct)` runs.
3. On Pass: write proceeds.
4. On Fail: write is suppressed (or remediation per gate hint, depending on artifact kind — some kinds may degrade-and-write, others suppress entirely).

### Producer-side simplification

Once the universal gate is in place, the explicit `EvaluateAsync` calls in J.5a–g become redundant — they're called twice (once at producer, once at write). The redundant producer-side calls get **deleted as part of J.6** (which already plans to delete redundant code post-J.5). The DELETE sweep is the simplification that confirms J.8 is correct: if removing the producer-side calls causes any test or behavior regression, J.8's interception isn't actually universal yet.

### Type-conditional dispatch via `AppliesTo` (already in place)

`ICognitiveOutputInvariant.AppliesTo(CognitiveArtifact)` is already the predicate that decides which invariants run on which artifacts. J.8 doesn't change this — it relies on it harder. Examples:

- `InnerThoughtBleedInvariant` (Door C) — currently `AppliesTo` returns true only for `Dispatch` sinks on contact-facing producers. Under J.8, this predicate IS what prevents Door C from accidentally firing on inner-thought writes (where interior content is *supposed* to be present).
- `ConfabulationInvariant` — currently applies to several producer kinds. Under J.8, the predicate decides whether to spend an LMKit call on each write.

**Load-bearing implication:** `AppliesTo` correctness becomes *more* load-bearing under J.8 because there's no producer-level gate to also act as a filter. An overly-permissive `AppliesTo` would cause noisy gate-fire on benign writes.

---

## Concerns and mitigations

### Concern 1 — Runtime cost (Ollama / LMKit calls per write)

Inner-thought cycles save several memory records per cycle (inner thought, world experience, sometimes reflection). If `ConfabulationInvariant`'s `AppliesTo` returned true for all of them and each triggered a separate LMKit call, that's 3-5 extra LMKit calls per cycle. At a current ~5min cycle cadence, that's manageable but non-trivial.

**Mitigations:**
- `AppliesTo` predicates should be **conservative for the LMKit-backed invariants**. Inner thoughts about Ani's own world (world-experience writes) probably should NOT run `ConfabulationInvariant` — they're inherently inventive and the model is supposed to elaborate.
- Inner-thought writes containing **Mark-attributed claims** ARE the danger zone. A cheaper pre-filter (regex / heuristic for *"Mark said"* / *"you said"* / *"yesterday X"* patterns) can gate the LMKit call so it only fires when the artifact contains an attribution to Mark. Falls under the same "conservative AppliesTo" principle.
- The runtime cost is empirically measurable post-J.8 — log gate-call counts per cycle, compare against baseline, tune AppliesTo if needed.

### Concern 2 — Producer-specific context plumbing

Some invariants need context the persistence boundary doesn't naturally have:
- `AntiParrotInvariant` needs the contact's recent words (currently the producer populates `ContactRecentMessages` on the artifact).
- `InnerThoughtBleedInvariant` needs prior Ani messages (currently `PriorAniMessages`).

**Mitigation:** producers continue to populate the `CognitiveArtifact` with the context they have. The artifact travels through the persistence boundary with all its context fields. The gate evaluates against what's on the artifact. Producers that don't populate context get evaluated with empty context — the invariants either skip (if context is required) or run on whatever's there. This recreates *some* opt-in pressure ("populate the right context fields"), but at a much weaker level than "remember to call EvaluateAsync." Forgetting context fields is recoverable (gate runs with reduced fidelity); forgetting `EvaluateAsync` entirely is not.

### Concern 3 — Producer-specific remediation

Currently each producer decides what to do on gate Fail (suppress dispatch, retry composition, log warning, etc.). Under J.8, that producer-specific behavior either:
- (a) Moves into the persistence-boundary handler — write is suppressed, producer learns via return value.
- (b) Stays at the producer — producer reads the gate result on the artifact post-persistence-call and decides what to do.

**Recommendation: (b).** The persistence boundary handles the substrate-protection concern (write yes/no); the producer handles the dispatch concern (do we still send the message even though the write was rejected? probably yes for some kinds, no for others). Cleaner separation than (a).

### Concern 4 — World Layer + canonical content

Mark's character-seed content (Sarah, Kevin, the bookstore, etc.) and World-Layer-Phase-1c world-experience writes are CANONICAL — they're supposed to be inventive, are intentionally at high creative latitude, and are NOT confabulation. The `ConfabulationInvariant` would currently mis-classify them.

**Mitigation:** `AppliesTo` returns false for `CognitiveProducerKind.WorldExperience` on `ConfabulationInvariant`. Already the right predicate — just needs to be carefully verified for J.8.

### Concern 5 — Anchored memory writes

Foundation memories (Feature 16) are atemporal canonical facts. They DON'T pass through cognitive-output paths (they're seeded). Anchored writes should bypass the gate entirely.

**Mitigation:** the persistence boundary distinguishes anchored-tier writes from cognitive-artifact writes. Anchored writes don't trigger the gate path.

---

## Phased implementation sketch

| Phase | Goal | Effort |
|---|---|---|
| **J.8.0** | Inventory every existing memory-write call site. Classify each as cognitive-artifact-write vs canonical-write vs metadata-write. Identify the chokepoint(s) — one or several. | 0.5 day |
| **J.8.1** | Define / extend `CognitiveArtifact` to be the unit ALL cognitive-artifact writes pass through. May require small refactors at producer write call-sites that currently bypass `CognitiveArtifact` (write a `MemoryRecord` directly without producing an artifact). | 1–2 days |
| **J.8.2** | Implement the persistence-boundary gate call. Behind a feature flag (`UniversalGateEnabled`, default off) so we can compare gate-fire patterns before flipping. | 1 day |
| **J.8.3** | **Observation window with flag OFF + producer-side gates ON.** Log what J.8.2's gate WOULD have caught vs what producer-side gates DID catch. Identify gaps + duplications. ~1 week. | 1 week observation |
| **J.8.4** | Flag flip: `UniversalGateEnabled=true`. Producer-side `EvaluateAsync` calls remain temporarily. ~1 week observation. | 1 week observation |
| **J.8.5** | Delete redundant producer-side `EvaluateAsync` calls. This IS the simplification. If any test/behavior regresses on deletion, J.8.4 wasn't actually universal — go back. | 0.5 day code + verification |
| **J.8.6** | Process integration. Document the rule: cognitive-artifact writes go through the persistence boundary, no producer-side gate calls. Add to feature-plan template. | 0.5 day |

**Total:** ~3 days code + 2 weeks observation + 1 day verification/process. Smaller than original J.5 because it's collapsing rather than expanding.

**Dependencies:**
- Theme J.5 stable (currently on observation window, opens at next deploy).
- Recommended companion: ship **J.5h tactical InnerThoughtPhase opt-in** in parallel with J.8.0 design, so the immediate substrate hole closes while the architectural work proceeds. Explicit naming: J.5h is interim, J.8 is principled.

---

## Acceptance criteria

1. Every memory write that originates from a cognitive producer passes through the gate (verifiable via instrumentation: every persistence call without a corresponding gate evaluation is logged as a violation).
2. Removing the producer-side `EvaluateAsync` calls causes zero behavioral regression (test suite + 1-week observation).
3. Producers can no longer introduce substrate-write holes by skipping migration. New producers automatically inherit gating.
4. Substrate-laundering trace pattern (originating confab → next-cycle retrieval → reinforcement) does not recur for at least one new failure-class instance after J.8 ships.

---

## Paper 3 contribution shape

This is the *recursive* finding — the architecture-over-instruction project's own architectural refactor was, on first ship, only partially universalised. Mark's May 2 critique caught it. The finding is:

> **"Shared evaluator vs universal gate: the architectural difference between 'one place invariants live' and 'one place artifacts pass.' Companion-AI substrate-integrity refactors that stop at shared-evaluator are vulnerable to producer-level omission, recurring as 'why didn't the gate fire?' findings months later. The principled architecture is a write-boundary chokepoint with type-conditional invariant dispatch via AppliesTo predicates."**

Strong contribution because (a) it names a *class* of mistake, not just a fix; (b) it generalizes — the same distinction applies to safety-rail architectures in non-companion AI systems; (c) it has empirical backing — the specific producer that was skipped (InnerThoughtPhase) became the substrate-laundering origin within days.

Worth referencing the May 2 12-record laundering trace as the canonical case study in the contribution.

---

## Status Log

| Date | Note |
|------|------|
| 2026-05-02 18:30 CDT | Drafted by Claude after Mark's architectural critique. Awaiting Mark green-light to escalate to phase plan + implementation. **J.5h tactical opt-in (InnerThoughtPhase migration) recommended in parallel** so the empirical substrate hole closes while J.8 is being designed. Both ship; J.5h is interim, J.8 is principled. |
