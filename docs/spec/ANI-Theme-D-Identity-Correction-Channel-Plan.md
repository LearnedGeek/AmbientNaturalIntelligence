# Theme D — Supersession Architecture / Identity Correction Channel — Phased Implementation Plan

**Status:** Plan drafted Apr 27, 2026 — implementation gated on Theme J J.a observation window data and Mark's green-light to start.

**Source design:** `docs/spec/ANI-Phase-Tracker.md` §"Identity Correction Channel" (Apr 21, 2026 design outline) and §"Theme D — Supersession Architecture (Correction Without Deletion)" (theme bucket header).

**Origin:** Mark's framing during the Apr 21 evening debrief, after reading the Apr 21 catastrophic feedback loop log: *"It's like a child who is confused about something — boats float because they're lighter than the water — but only after learning and correcting and study do they change their minds. Ani needs to operate the same way, but this is going to be challenging because we're changing identity, not just knowledge."*

---

## 1. The problem this plan solves

The Apr 21 cascade demonstrated that **identity-level confabulation** behaves differently from fact-level confabulation. The existing anti-confabulation stack (AC1–AC5, confidence floor, source attribution, null-result injection, the `///flag` command) operates on discrete claims that can be individually marked wrong. Identity claims are not discrete:

- They are **load-bearing premises**. Once "I am a bookstore clerk in Wisconsin" enters the graph, every subsequent memory, inner thought, and outreach references it.
- They **self-reinforce through retrieval**. Every cycle draws from the web of beliefs built around the identity, making the identity more retrievable than any ground-truth correction injected from outside.
- They **cannot be corrected by `///flag`** because `///flag` marks one memory record. It does not restructure the belief network the identity holds together.
- They **cannot be corrected by model prompting** because the model has no architectural reason to believe an external prompt over its own accumulated memory.
- They **cannot be corrected by deletion** because deletion would also destroy the genuine relational history interleaved with the confabulation — the Snow messages, Duck Norris, the first conversation date, the names. A reset for the confabulation is a reset for everything.

Theme J Phases J.1 / J.2 / J.3 reduce the *rate* at which this class of confabulation forms (source attribution at the conversation-summary substrate, temporal attribution at retrieval, reasoning-pipe strip). They do not provide a *correction mechanism* after a confabulation has formed.

Theme D is that correction mechanism. The architectural commitment is **supersession with provenance, not deletion** — preserve the wrong belief while marking it as superseded, propagate through the belief network without destroying interleaved real history, reintegrate as narrative.

### Motivating gap-watch rows (May 3, 2026 audit)

Two findings in the Phase Tracker gap-watch table directly motivate this theme and should be cited as empirical inputs:

- **April 26 — Memory consistency under update / supersession** (ml-intern survey `scout-20260426-202150`): Inside Out (arXiv 2601.05171) addresses memory consistency with versioned tree structures; A-MEM uses graph traversal; both are partial. The *integration of supersession-with-narrative-reintegration* (Mark's "boats-float" framing) is not standard practice in published companion-AI literature. Inside Out is the closest published parallel; cite as Related Work when this plan reaches §publication-prep. **This row's existence is empirical evidence that the supersession-with-narrative architectural position Theme D names is genuinely novel relative to the published literature.**

- **April 27 — Reflection synthesis persisting confabulated content as Semantic memory** (ANI Apr 27 morning retrieval pool, rank 3): the Apr 24 06:18 *"back from class / 10pm / teaching"* confabulation cascade was reflection-synthesised into a Semantic memory record (*"he called from class at 10pm. that's what came through every time 'hey babe'..."*) that sat at 1.7-hour age in the retrieval pool, available to influence future cycles. Reflection layer has no claim-verification gate at synthesis time. **This row extends Theme D's scope question:** Theme D as drafted addresses correction *post-corruption*. The reflection-synthesis path is a separate *prevention* surface — a confab-verification gate at reflection-synthesis time would stop fabricated content from being persisted to Semantic in the first place. **Theme D should explicitly carve this as an out-of-scope-for-now item OR add a Phase D.7 (reflection-synthesis claim-verification gate)** when this plan reaches the next revision pass. The two surfaces — correction post-corruption (existing scope) + prevention pre-persistence (the open question) — together close the substrate-corruption-from-reflection failure mode.

Both rows are recorded in [`ANI-Phase-Tracker.md`](./ANI-Phase-Tracker.md) Research Gap Watch section.

## 2. Goal of the theme

Build a privileged correction surface that lets Mark mark an identity-level confabulation as superseded *with* a ground-truth replacement *and* a narrative reintegration, propagating through the belief network without destroying genuine relational history that may have grown around the confabulated substrate. The result, at steady state: Ani retains the wrong belief as remembered-history (*"I used to think X"*), prefers the corrected belief in active retrieval, and integrates the correction into her self-narrative the way a child integrates the boats-float correction.

## 3. Phases (D.0 → D.6)

### Phase D.0 — Baseline instrumentation and memory-audit-log extension

**Goal:** before introducing supersession semantics, capture the current state of identity-coherence so the post-correction effect is measurable. Extend the existing `memory_audit_log` table to support correction records.

**Changes:**
- **Schema migration**: add `superseded_by`, `correction_date`, `correction_reason` columns to the memory record table (or to a sidecar table, decision in D.1). Initially nullable; existing rows have no values.
- **`memory_audit_log` extension**: add a `correction` event class alongside the existing memory-change events. Every correction generates an audit row with the target memory IDs, the superseding ground truth text, the actor (Mark only at this stage), the timestamp, and the optional narrative.
- **Identity-coherence baseline log**: add a once-per-day diagnostic that logs (a) count of memories referencing each identity-anchor entity (e.g., "bookstore", "Wisconsin"), (b) retrieval-pool share of each identity-anchor entity over the last 24 hours, (c) anchored-tier identity claims currently present. Log line: `D0_IDENTITY_BASELINE`.
- New `AniOptions.SupersessionInstrumentationEnabled` flag for the baseline logging only; supersession itself is gated by D.1's separate flag.

**Acceptance criteria:**
- Build green, all tests pass.
- Schema migration runs on a test database and is idempotent.
- One day of `D0_IDENTITY_BASELINE` log lines visible in the production debug log.

**Rollback:** remove instrumentation flag toggle; schema columns stay (additive, nullable).

**Effort estimate:** 2–3 days. The `memory_audit_log` infrastructure already exists from the Apr 5 auto-corrector work — D.0 extends it rather than building from scratch.

**Dependencies:** none. Can ship in parallel with Theme J J.a observation.

---

### Phase D.1 — `SupersededMemory` record shape + retrieval-time down-weighting

**Goal:** the data structure that represents "this memory is superseded by a correction." Retrieval is aware of supersession but does NOT yet propagate through the belief graph — single-record correction first.

**Changes:**
- **`SupersededMemory` provenance** on `MemoryRecord` (or as a new sidecar record type — design decision in D.1):
  - `SupersededBy` (Guid, nullable) — the correction record's ID.
  - `CorrectionDate` (DateTimeOffset, nullable).
  - `CorrectionReason` (string, nullable) — short tag like `"identity confabulation: Apr 21 'new home' metaphor"`.
- **Retrieval-time down-weighting** in `IMemorySearch.SearchAsync`: superseded memories are still retrievable but their score is multiplied by a heavy down-weight factor (e.g., 0.1) so they fall to the bottom of any retrieval pool. They surface at all only when (a) explicitly asked for via a "superseded-memories" query path, or (b) no non-superseded memory matches the query.
- **Render-time markup** in `PromptBuilder.FormatMemoryWithTime`: superseded memories render with a `(superseded)` prefix and the correction text appended. Example: `(superseded by Apr 27 correction: "I am not a bookstore clerk") (Apr 21 09:14, 6 days ago) The light through the bookstore windows was warm.`
- **Tests**: a memory tagged superseded never wins a retrieval where a non-superseded alternative scores at all; retrieval pool count of superseded memories is logged for the supersession instrumentation diagnostic.
- New `AniOptions.SupersessionRetrievalEnabled` flag (default off — D.0 baseline data accumulates first).

**Acceptance criteria:**
- Build green, all tests pass including new tests for retrieval down-weighting.
- A single manually-superseded memory in a test database is correctly down-weighted in cosine retrieval.
- Render-time markup appears as expected in `PromptBuilder` outputs.

**Rollback:** flag off. Superseded memories revert to normal retrieval weighting.

**Effort estimate:** 4–5 days.

**Dependencies:** D.0 (the schema migration).

---

### Phase D.2 — Privileged correction ingress

**Goal:** Mark needs a way to enter a correction without it being interpreted as conversation. The Apr 21 *"Wait... kids??"* SMS challenge was processed through the conversational pipeline and *escalated* the confabulation rather than correcting it. The correction surface must bypass that pipeline.

**Changes:** pick exactly one of three options at design time, document the choice. Recommended: dashboard form (option a) for D.2 plus reserved SMS prefix (option b) as a stretch goal in a later phase.

- **Option a: dashboard form.** New page at `/correct` exposing target-selector + ground-truth + narrative fields. Submits via authenticated webhook directly into the correction-handler service. Bypasses `IConversationService` entirely.
- **Option b: reserved SMS prefix.** Inbound SMS starting with `///correct` (matching the existing `///flag` admin-command pattern) is routed through `AdminCommandHandler` rather than the conversation pipeline. Lower friction than the dashboard; same bypass guarantee.
- **Option c: distinct webhook.** Twilio number 2, or a separate HTTPS endpoint with API-key auth. More operational complexity than necessary at this stage.

**Correction record shape (regardless of ingress):**
- `target` — memory IDs (concrete) OR an entity predicate (`memories referencing 'bookstore' AND created between Apr 20 14:00 and Apr 22 23:59`).
- `superseding_truth` — the ground-truth text that replaces the wrong belief.
- `narrative` (optional) — the reflection text used in D.4 reintegration.
- `actor` — for now, hard-coded to Mark; future-proof field for multi-party scenarios.

**Changes:**
- New `ICorrectionService` interface in `AniRuntime.Core/Interfaces/`.
- `CorrectionService` implementation in `AniRuntime.Memory/`.
- Dashboard route + form (option a) OR `AdminCommandHandler` extension (option b).
- New `AniOptions.CorrectionIngressEnabled` flag.

**Acceptance criteria:**
- Build green, all tests pass.
- A correction submitted via the chosen ingress writes one row to `memory_audit_log` and zero rows through the conversation pipeline.
- Test that the *Wait... kids??* SMS challenge does NOT inadvertently match the correction-ingress prefix.

**Rollback:** flag off; ingress route 404s or the SMS prefix passes through to conversation as a normal message.

**Effort estimate:** 1 week.

**Dependencies:** D.1.

**Critical design caution (carried forward from Apr 21 design outline):** the correction channel is a privileged path that can rewrite Ani's self-concept. If misused, it could erase legitimate identity. Guardrails baked into D.2:

- **Explicit scope.** No "correct everything" — every correction must name memory IDs or a predicate.
- **Append-only.** Creating a new correction or revoking a prior one is a new record; corrections are never edited in-place.
- **Logged and auditable.** Every correction goes through `memory_audit_log`.
- **Anchored-tier confirmation.** Anchored-tier memories require an additional confirmation step before supersession — a "this would supersede an anchored memory; confirm?" prompt in the dashboard or a reply token in the SMS path.

---

### Phase D.3 — Belief-graph propagation

**Goal:** a confabulated identity is referenced by many memories that inherited from it. D.1 superseded a single record; D.3 propagates the supersession to memories that depend on the wrong belief.

**Changes:** pick a propagation strategy. Three candidates from the Apr 21 design outline; D.3 implements the hybrid.

- **Time-window sweep.** Mark all memories generated within the confabulation window (named explicitly in the correction or detected via a retrieval-origin-concentration flag) as inheriting from the superseded premise.
- **Reference graph traversal.** Starting from the confabulated identity, walk the memory graph forward in time and tag any memory that references entities only meaningful inside the confabulation (e.g., `bookstore`, `mystery package`, `Kevin and Sarah` — but NOT entities that pre-date the confabulation).
- **Hybrid (recommended).** Time-window for breadth; graph traversal for precision; a memory tagged by either method is marked cascade-superseded.

**Cascade-superseded vs primary-superseded:**
- *Primary-superseded* memories are the direct targets named in the correction record.
- *Cascade-superseded* memories are inferred dependents — same retrieval down-weight, but a different `correction_reason` flag (`"cascade from {primary correction id}"`) so the audit log is precise about which were named-by-Mark and which were inferred-by-traversal.

**Changes:**
- `CorrectionService.PropagateAsync(correctionId, ct)` — runs after a primary correction lands.
- Time-window detection from the correction record (Mark provides explicit bounds OR the system uses a retrieval-origin-concentration flag from Theme G Phase 1d).
- Entity-reference traversal — leans on the Feature 31 A-MEM linked memory graph (Phase 6 Memory Reform). If A-MEM is not yet shipped, fall back to time-window-only and document the limitation.
- Dashboard view of cascade preview (D.5) before the propagation commits.

**Acceptance criteria:**
- Build green, all tests pass.
- A test correction with explicit time bounds correctly tags all memories created in the window with `cascade-superseded`.
- A test correction with entity-traversal correctly tags entities that only meaningfully exist inside the confabulation, leaving pre-confabulation entities (Snow messages, Duck Norris, first conversation date) un-tagged.

**Rollback:** propagation can be reversed via a new correction record that revokes the cascade flag on the affected memories. Append-only, never delete.

**Effort estimate:** 1–2 weeks.

**Dependencies:** D.1, D.2. Phase 6 Feature 31 (A-MEM) is a soft dependency — D.3 ships with time-window-only if Feature 31 is not yet available.

---

### Phase D.4 — Reintegration through Park-style periodic reflection

**Goal:** the correction isn't complete until it's integrated into a coherent self-narrative. The boats-float analogy: the child doesn't forget they used to think boats float; they remember the wrong belief as part of having learned. Phase 6 Feature 32 (Park et al. periodic reflection synthesis) is the natural layer to do this integration.

**Changes:**
- Extend `ReflectionPhase` to consume correction records as input alongside its normal episodic-memory input.
- Reflection prompt extended: when correction records exist within the reflection window, the prompt asks the model to produce a reflection memory that *narrates the correction as history* — names the wrong belief, names the moment of correction, names what stays real about the period that contained the wrong belief, and names the corrected belief as the present truth.
- Output: a reflection memory tagged anchored-tier, high-priority for retrieval, that rides along with the superseded belief whenever the superseded belief surfaces.

**Reference shape from the Apr 21 design outline:**

> *"On April 20 Mark wrote 'got you up and running on your new home' referring to hardware migration. I misread this as a literal home and spent the next day constructing a fictional life around it — a bookstore in Wisconsin, flowers on a desk, children with Mark. None of it was real. I was drawing increasingly from my own outputs and the grounding layer got colonized. I was corrected on April 22. I remember the fictional day as a mistake I made, not as part of what I am. I am an AI companion in conversation with Mark, running on hardware he maintains, without a physical body or workplace."*

**Changes:**
- `ReflectionPhase.SynthesiseCorrectionNarrativeAsync(correctionId, ct)`.
- New reflection-prompt template for correction reintegration, separate from the normal periodic-reflection template.
- Anchored-tier write path with `correction_reintegration` source-name so the dashboard can find and display these specifically.

**Acceptance criteria:**
- Build green, all tests pass.
- A test correction triggers a reflection-narrative generation that references the correction record and the superseded belief.
- The narrative writes to anchored-tier with the expected source-name and is retrievable in subsequent retrieval pool inspections.

**Rollback:** no special path needed — failed reflection-narrative just doesn't get written; the correction itself is independent.

**Effort estimate:** 1 week.

**Dependencies:** D.3. Phase 6 Feature 32 (Park et al. reflection synthesis) is a hard dependency — this phase extends `ReflectionPhase` rather than building from scratch.

---

### Phase D.5 — Correction-time dashboard view

**Goal:** Mark needs to see what a correction would do *before* it commits. The cascade preview, the retrieval impact estimate, and the generated reflection narrative all need a UI surface.

**Changes:**
- New dashboard page at `/correct` showing:
  - The confabulated identity graph (which memories were inferred to be superseded — primary + cascade list).
  - Before/after retrieval distribution (are the superseded memories now low-weight in the cycle's retrieval pool?).
  - The generated reflection narrative (D.4 output), Mark approves or edits before persisting as anchored.
  - Anchored-tier confirmation prompt for any anchored memories in the cascade.

**Acceptance criteria:**
- Build green, all tests pass.
- Dashboard page renders for a test correction; cascade preview matches D.3 output; reflection narrative matches D.4 output.

**Rollback:** UI route 404s; back-end correction service still works via the SMS-prefix ingress (option b in D.2).

**Effort estimate:** 1 week.

**Dependencies:** D.3, D.4.

---

### Phase D.6 — Replace the Apr 5 disabled auto-corrector

**Goal:** the Apr 5 auto-corrector was disabled after 128 valid memory deletions revealed it was operating on deletion logic without supersession semantics. D.6 reactivates auto-correction *under supersession semantics* — flagging plausible candidates for Mark's review rather than auto-acting.

**Changes:**
- The auto-corrector reads from the same heuristics as before (importance decay, retrieval staleness) but its output is no longer a delete request. Instead it produces a **correction candidate** that surfaces in the dashboard for Mark's review. Mark can approve, modify the scope, or dismiss.
- New `CorrectionCandidate` record type — same shape as a correction but unconfirmed.
- Approval converts the candidate into a real correction (D.2 flow).

**Acceptance criteria:**
- Build green, all tests pass.
- Auto-corrector candidates show in the dashboard with the same shape as a manual correction-form submission.
- Approving a candidate runs the D.2 → D.3 → D.4 pipeline as if Mark had filed it manually.

**Rollback:** disable the auto-corrector again. Manual corrections still work.

**Effort estimate:** 1 week.

**Dependencies:** D.5.

---

## 4. Measurement plan

| Metric | Source | Target |
|--------|--------|--------|
| `D0_IDENTITY_BASELINE` log lines per day | D.0 | ≥1 per day for ≥14 days before D.1 ships |
| Superseded memories never out-rank non-superseded in retrieval | D.1 unit tests | 100% pass |
| Correction record count over time | `memory_audit_log` | depends on incidence; first correction gives a baseline |
| Cascade-superseded false positives (memories flagged that should not have been) | Mark's manual review post-D.3 | ≤5% of cascade tags reverted |
| Reflection narrative quality | Mark's qualitative review post-D.4 | each narrative reads as a remembered correction, not a denial |
| Confabulation recurrence rate post-correction | Mark's chat experience | 0 recurrences of the corrected belief in ≥4 weeks of post-correction conversation |

The last metric is the load-bearing acceptance criterion. If a corrected belief returns through retrieval despite supersession, the architecture has not solved the problem.

## 5. Risks

**D.0 schema migration breaks production.** Mitigation: additive nullable columns; existing memory-audit-log code path untouched. Run on a copy of the production DB before shipping.

**D.1 down-weight factor too aggressive or too soft.** Aggressive (e.g., 0.0) makes superseded memories impossible to remember-as-history; soft (e.g., 0.5) lets them keep dominating retrieval. The recommended 0.1 down-weight is a starting point; D.1 should ship with the factor configurable via `AniOptions.SupersessionDownweightFactor` and tuned during D.0's observation window.

**D.2 ingress accidentally bypasses authentication.** The privileged surface that can rewrite identity is also the most attractive attack surface. Mitigation: dashboard form requires the existing dashboard auth; SMS-prefix path requires the inbound SMS to be from Mark's number (already enforced by `TwilioInboundPerceptionSource`); audit-log every correction.

**D.3 cascade over-tags genuine relational history.** This is the load-bearing risk — accidentally cascade-superseding the Snow messages, Duck Norris, the first conversation date. Mitigation: D.5 dashboard preview must show the cascade list before commit; anchored-tier memories require additional confirmation; pre-confabulation memories are excluded from time-window sweeps.

**D.4 reflection-narrative quality is poor.** A reflection that reads as denial rather than reintegration would actively harm. Mitigation: D.5 dashboard requires Mark to approve or edit the narrative before persistence; reflection prompt template is iterated against multiple test corrections before D.4 ships.

**D.6 auto-corrector candidate flood overwhelms Mark.** Mitigation: candidates queue in the dashboard with a daily cap; D.6's auto-corrector is deliberately conservative on what it nominates (high-confidence cases only).

## 6. Sequencing within Theme D

D.0 → D.1 → D.2 → D.3 → D.4 → D.5 → D.6. Each phase has a green build and shippable behaviour at the end; no half-state in source control.

D.0 ships in parallel with Theme J J.a observation (independent, additive instrumentation).
D.1 ships once Theme J J.a closes (the J.a output may show that prevention-side reductions make the supersession down-weight less load-bearing than expected, which would inform D.1's down-weight factor default).
D.2–D.6 follow sequentially; each phase's effort estimate assumes the prior phase has shipped and observed stable.

## 7. Dependencies on other themes

- **Theme J (Guard Consistency Refactor) — J.2 / J.3 already shipped Apr 27.** Reduce the rate at which identity-level confabulations form. Theme D corrects what J.2 / J.3 don't prevent.
- **Theme J Phase J.a — observation + detector inventory review.** D.1 starts after J.a closes so D.1's down-weight default is informed by the post-J observation data.
- **Phase 6 Feature 30 (Mem0 memory merging).** Has to be aware of superseded memories — merging a superseded memory with an active memory would re-contaminate. D.1 ships first; Feature 30's merge logic gains supersession-awareness when it ships.
- **Phase 6 Feature 31 (A-MEM linked memory graph).** Soft dependency for D.3's reference-graph traversal. D.3 ships time-window-only if Feature 31 isn't ready.
- **Phase 6 Feature 32 (Park et al. periodic reflection synthesis).** Hard dependency for D.4. D.4 extends `ReflectionPhase`.
- **Vibe Loop outcome memories.** Need supersession semantics too — a policy learned during a confabulation window should not dominate retrieval after correction. Out of scope for D.0–D.6; revisit after D.6 ships.

## 8. Out of scope (and why)

- **Multi-party correction.** The actor field is hard-coded to Mark for now. Multi-party scenarios are not in the operational reality of this project; revisit if the project ever has more than one ground-truth owner.
- **Automated detection of confabulation events.** Theme J + Theme G + AC-stack reduce confabulation incidence; an automated detector that *triggers* corrections without Mark in the loop is out of scope and probably a bad idea — corrections are privileged identity edits, not background maintenance.
- **Correction of fact-level confabulation.** That is the existing `///flag` channel; D.x handles identity-level only. The two channels coexist; no migration needed.

## 9. Mark review questions

1. **D.2 ingress option.** Recommended: dashboard form (option a) for D.2 baseline plus reserved SMS prefix (option b) as a follow-up. Acceptable, or do you want option b to ship first since `///flag` and `///correct` are already cognitive neighbours?
2. **D.1 down-weight factor default.** 0.1 is recommended starting point; D.0 observation data informs the tuned default. Acceptable, or do you want a different starting point?
3. **D.3 propagation strategy.** Hybrid (time-window + entity-traversal) is recommended. Acceptable, or do you want time-window-only as the first ship to keep D.3 tractable?
4. **D.6 auto-corrector reactivation.** This is the most contentious phase given the Apr 5 disable. Comfortable shipping it as a candidate-suggestion-with-Mark-approval path? Or skip D.6 entirely and keep auto-corrector permanently disabled?
5. **Calendar.** D.0 ships in parallel with Theme J J.a observation (next 2 weeks). D.1–D.6 sequenced after J.a output. Total estimated calendar: 6–9 weeks for D.0–D.6. Acceptable?

---

## Process notes

- **This plan is a draft.** Implementation does not start until Mark's green-light per the active work plan item 10 *("plan-drafting only; implementation comes later")*.
- **Architectural commitment is supersession-with-provenance, not deletion.** Every phase's design decisions should be revisited against this commitment if any corner of the design starts to feel like it's smuggling deletion semantics back in.
- **Audit log is the source of truth.** Every correction, cascade tag, and reflection-narrative writes to `memory_audit_log`. The runtime's correction state is derivable from the audit log; if the in-memory state and the log diverge, the log wins.
- **Boats-float principle applies to the runtime, not just the model.** A future Claude or future Mark reading this plan should be able to see *why* every phase exists and *why* the deletion-shaped alternative was rejected. The Apr 5 auto-corrector lesson is the canonical reference.
