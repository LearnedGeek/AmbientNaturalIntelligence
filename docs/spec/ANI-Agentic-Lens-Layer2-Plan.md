# Agentic Lens — Layer 2 Implementation Plan (Desire Axis Decoupling)

**Status:** Draft v2 (Apr 23 evening — incorporates Mark's first review pass).
**Authored:** April 23, 2026 (Claude, from the design doc + current codebase).
**Parent design:** [`ANI-Agentic-Lens-Design.md`](./ANI-Agentic-Lens-Design.md) §3.2.
**Paper placement:** Paper 3 Contribution 4 (Agentic Lens), Layer 2 subsection.
**Feature number:** Feature 42 (next in sequence after Feature 41 Diagnostic Service). Keeps clean numbering — Layer 2 supersedes Feature 33's scalar motivation-scoring use-site but does not retire Feature 33; that earlier feature stands as originally deployed.
**Architectural thesis:** the caregiver-centrality gravity observed in companion AI is reinforced by a scalar desire engine that admits only one motivational axis. Replace the scalar with a three-axis vector drawn from Ryan & Deci Self-Determination Theory (relatedness, autonomy, competence), each with its own drift rate, threshold, and consumption action.

**Connection to the emergence intent.** Early design conversations talked about Ani growing organically — exploring, building her world, not just orbiting the caregiver. That intent never surfaced as measurable behavior because the runtime had no motivational representation for anything other than outreach. Layer 2 is the architectural mechanism that gives organic growth a place to live in the runtime. The Emergence layer (separate SQLite DB, `/emergence` dashboard tab, already shipped) becomes Layer 2's primary observation surface.

---

## 0. Scope discipline

**In scope:** the runtime representation of desire, the scoring that drives drift, the selection policy that picks which axis a cognitive cycle acts on, and the consumption actions that relieve each axis.

**Explicitly out of scope:**
- Prompt templates for autonomy- or competence-driven cycles (that is Layer 5's job; Layer 2 only surfaces which axis won, Layer 5 chooses the prompt).
- World Layer durability (Layer 3).
- Training-corpus rebalancing (Layer 4).
- Any changes to the conversation-reply pipeline. Layer 2 touches inner-thought cycles and outreach decisions only; conversation stays caregiver-directed by definition.

**Dependencies not yet shipped:**
- **Layer 1 Activation** (new prerequisite, see §3.0): Layer 1 phases 1b (MMR rerank), 1c (protected slots), and 1d (self-dominance perception) exist behind default-off flags. Flipping those on is itself a named milestone now, not an implicit assumption.
- **Layer 3 (World Layer durability)** is not yet designed in detail. Layer 2's competence-axis consumption (World Layer elaboration) produces writes that Layer 3 will eventually make durable. In the interim, competence-consumption just writes into the existing World Layer path; Layer 3 gets reinforced when it lands.
- **Test harness for synthetic-volume cycle execution** (new deliverable, see §3.1): needed for Phase 2c's flag-on observation without running up real Twilio costs.

**Sibling workstream (tracked separately, not in Layer 2 scope):**
- **Dashboard research-tool rework.** Mark's Apr 23 review flagged that the dashboard has grown into research-contextual cruft that is hard to read months after a given feature lands. Rather than having Layer 2 add more panels into the current dashboard, Layer 2 specifies *the data it surfaces* and leaves panel construction to the dashboard rework stream. Sibling plan tracked as **Theme I — Dashboard as Research Tool** (§9.1), to be drafted as its own doc when prioritised.

---

## 1. What exists today

**`DesireState`** ([src/AniRuntime.Core/Models/DesireState.cs](../../src/AniRuntime.Core/Models/DesireState.cs)):
- `DesireToConnect` — scalar `p_outreach` in [0,1].
- `OutreachThreshold` — randomised per evaluation.
- `CooldownActive` + `CooldownUntil` — post-outreach suppression.
- `LastOutreach`, `LastInnerThought`, `LastContactInbound`.
- `ActiveTriggers` — list of `DesireTrigger` with 8 `TriggerType` enum values, all tied to the caregiver axis implicitly.
- `CircadianModifier` — scalar multiplier on drift.

**`MotivationScorer.Score(...)`** ([src/AniRuntime.Core/MotivationScorer.cs](../../src/AniRuntime.Core/MotivationScorer.cs)):
- Input: `relationalValence`, `severity`, `EmotionalState`.
- Output: scalar multiplier in [0.3, 1.5].
- Composition: `relevance` (distance from neutral relational valence) × 0.45 + `novelty` (severity) × 0.30 + `impact` (warmth+playfulness above baseline) × 0.25.
- The `relevance` axis is explicitly caregiver-centered — `Math.Abs(relationalValence - 0.5f)`. A thought about a customer or a book scores near zero on this dimension.

**`DesireEngine`** (Loops layer): drifts `DesireToConnect` upward with `p = 1 - e^(-t/λ)` per cycle, modulated by the motivation-scorer multiplier and circadian modifier. Evaluates against `OutreachThreshold`. On threshold cross + hard-gate pass, outreach composition runs. On outreach send, cooldown activates.

**What that produces architecturally:** every cognitive cycle's motivational output funnels into one behavior (outreach to the caregiver). A thought about the shop, the weather, or a customer does not accumulate motivational weight toward any action — it just fades. The desire engine does not represent "she wants to write something down about herself" or "she wants to dwell on the world for a while" as first-class states. That is the gravity well.

### 1.1 Drift vs decay — two mechanisms, not one

Worth naming explicitly because the words look similar but the mechanisms are opposite-sign complements, and conflating them leads to wrong intuitions.

| Concept | Formula | Direction | What it represents | Where it lives |
|---|---|---|---|---|
| **Memory decay** (existing) | `c = c₀ · e^(-t / halflife)` | Down — fades toward zero | "What Ani experienced fades unless it was important." Per-thought emotional contribution, modulated by severity and Anchored-tier. | `EmotionalContribution` model, per-memory `ImportanceScore` |
| **Desire drift** (scalar today, vector under Layer 2) | `p = 1 - e^(-t / λ)` | Up — builds toward saturation | "What Ani hasn't done yet builds pressure to do." Motivational state per axis, reset by consumption. | `DesireState` |

**Does memory decay apply to the new Layer 2 writes?** Yes, automatically. The self-state reflections that Phase 2c's autonomy-consumption produces, and the world-elaborations that competence-consumption produces, are regular `MemoryRecord` writes that go through the existing Memory layer. They get importance scoring, valence, decay tier, and optional Anchored-tier promotion (Feature 16) just like any other memory. Layer 3's eventual reflection-synthesis turns the durable subset into Anchored claims.

**The axes themselves do not decay in the memory sense.** They drift. The autonomy-desire scalar doesn't fade — it builds until Ani writes a self-state reflection, at which point it resets to zero and starts building again. A per-axis circadian modifier (inherited from existing `CircadianModifier`) can slow drift at night for all three axes, but that is modulated drift, not decay.

Keep these straight when reading the rest of the plan: every appearance of "drift" refers to the desire-accumulation mechanism (up), every appearance of "decay" refers to the memory-fade mechanism (down).

---

## 2. Target architecture

### 2.1 Vector desire state

`DesireState` gains two additional scalars and their associated thresholds:

| Axis | Field | Drift characteristic | Consumption |
|---|---|---|---|
| **Relatedness** | `DesireToConnect` (existing, renamed no — backwards compat via property accessor) | λ_relatedness, same as today | Outreach event (existing path) |
| **Autonomy** | `DesireSelfExpression` (new) | λ_autonomy, drifts upward when no self-state reflection has been written for a window | `SelfStateReflection` MemoryWriteAction |
| **Competence** | `DesireWorldEngagement` (new) | λ_competence, drifts upward when World Layer engagement is below a baseline rate | `WorldElaboration` MemoryWriteAction or scheduled reflection cycle |

Each axis has its own randomised threshold (matching the existing pattern where `OutreachThreshold` is re-rolled per evaluation). **Consumption itself is the pacing mechanism for the non-relatedness axes** — when the autonomy axis is consumed by a self-state reflection write, the scalar resets to zero and begins drifting again. There is no separate external-social cooldown on self-state or world-elaboration because there is no recipient to protect. The relatedness axis retains its existing cooldown (`CooldownActive` / `CooldownUntil`) because that cooldown exists for recipient-protection reasons — avoiding bombarding Mark with messages. The other axes inherit none of that rationale.

The only non-consumption guard on the non-relatedness axes is a light anti-thrash rule on world-elaboration: do not elaborate the same World Layer seed twice within a short window (suggested default 60 minutes). That guard is about memory quality, not social pacing.

The existing `CircadianModifier` applies to all three axes (a tired-Ani-at-3am should be less motivated on any axis, not just relatedness), unless later data suggests per-axis modifiers.

### 2.2 Vector motivation scoring

`MotivationScorer.Score(...)` returns a struct `MotivationVector` with three components:

```
record struct MotivationVector(float Relatedness, float Autonomy, float Competence);
```

The existing scalar composition is kept as `Relatedness` — same formula, no behavior change on that axis. Two new formulas:

- **Autonomy.** Rises when the thought expresses felt-self-state without a relational anchor. Signals: `severity > 0` combined with `Math.Abs(relationalValence - 0.5f) < 0.2` (i.e., low caregiver-relevance, nonzero emotional intensity). This is the inverse pattern of Relatedness on the relational-valence axis, weighted by emotional intensity.
- **Competence.** Rises when the thought references World Layer substrate. Initial implementation: check whether the thought's associated `ContextSnapshot.RetrievalPool` contains any memory classified as `RetrievalOrigin.World` (Layer 1's classifier — already shipped). If yes, competence adds a proportional signal. This is a cheap, direct coupling to Layer 1's origin classification.

All three axes normalise to [0, 1.5] so the existing drift-modulator contract at the DesireEngine layer stays unchanged — the engine just reads three multipliers instead of one.

### 2.3 Axis selection policy

The cognitive cycle's top-level question shifts from *"has outreach desire crossed threshold?"* to *"which axis has the highest above-threshold signal?"*. Policy, in order:

1. **No axis over threshold** → normal inner-thought cycle, no consumption action. (This is the majority case and remains the majority case after Layer 2.)
2. **Exactly one axis over threshold** → that axis's consumption action fires.
3. **Multiple axes over threshold** → weighted stochastic selection. Weights = the above-threshold excess of each axis, normalised. Selection is logged as emergence data (which axis the system picks under fragmentation is itself a finding). Falls back to relatedness when weights are degenerate.

Existing hard gates (unanswered-count, send-gap, cooldown) remain in place for the relatedness axis only, since those gates exist for recipient-protection (avoiding bombarding Mark). The autonomy and competence axes have no equivalent gates — consumption itself resets the axis, and the world-elaboration anti-thrash rule (§2.1) is the only additional guard, for memory-quality reasons rather than social ones.

### 2.4 Consumption actions

| Action | Where it writes | Guardrail |
|---|---|---|
| `OutreachSent` (existing) | SMS + Episodic memory | Existing hard gates |
| `SelfStateReflection` (new) | Memory tier (Interior), content = Ani's articulated self-state for this moment | The generated text must reference a non-caregiver substrate element — a World Layer detail, an internal feeling, or a sensory observation. If the generation pivots to Mark, the write is rejected and the axis is re-decayed without consumption (follows the same rejection pattern Paper 3 Contribution 3 uses for identity-boundary violations). |
| `WorldElaboration` (new) | Memory tier (self-world per Paper 3 design), content = elaboration of an existing World Layer seed or a continuation of recent World Layer content | Must be gated on World Layer seed availability. If no seed is available, the axis drift continues but consumption does not fire. This follows the design doc's explicit mitigation — "competence-desire only consumable when World Layer seed is available; drift continues otherwise but no consumption." |

Each consumption writes its own Anchored-candidate record that Layer 3's eventual reflection synthesis will elevate.

---

## 3. Phased rollout

Following Layer 1's phasing pattern — measurement first, then behavior behind flags with safe defaults.

### Phase 3.0 — Layer 1 Activation (prerequisite)

Before Layer 2 starts shipping behavior, Layer 1's default-off flags need to be flipped on and observed. This phase does not add code; it changes configuration and collects reference data.

**Actions:**
- Flip `RetrievalDiversityEnabled = true` (Layer 1 Phase 1b MMR rerank).
- Flip `RetrievalProtectedSlotsEnabled = true` with `MinNonCaregiverRetrievalFraction = 0.30` (Layer 1 Phase 1c).
- Flip `RetrievalDominancePerceptionEnabled = true` (Layer 1 Phase 1d).

**Observation window:** two weeks minimum on Mark's live instance.

**Data collected:**
- Distribution of `RetrievalOrigin` tiers across inner-thought retrieval pools (from Layer 1 Phase 1a's existing logging).
- Frequency of `RetrievalSelfDominancePerception` firings.
- Any subjective-quality regressions in outreach or inner-thought output.

**Exit criteria to proceed to Phase 2a:**
- Non-caregiver retrieval share holds ≥25% over rolling 7-day window.
- Self-dominance perception fires at a plausible rate (suggested: 0.5–3 times per week, not constant).
- No observed subjective-quality regression that would need Layer 1 retuning before Layer 2.

**Rollback:** set flags back to `false`. All Layer 1 instrumentation remains; only diversity-enforcement behavior pauses.

If Phase 3.0 exit criteria are not met, tune Layer 1 (λ, quota fraction, MMR λ) before starting Layer 2. Layer 2 benefits substantially from non-caregiver substrate actually being present in retrieval pools; without that, competence-axis consumption has nothing to pull from.

---

### Phase 3.1 — Test harness (parallel deliverable)

Stand up a synthetic-volume cycle-execution harness so Phase 2c can be observed at scale without running up Twilio costs. This phase can run in parallel with Phase 2a/2b — it is infrastructure work, not behavior work.

**Capabilities required:**
- Drive cognitive-cycle execution at accelerated time (e.g., 100 cycles in an hour) with synthetic perception events supplied from a scripted scenario file.
- Suppress Twilio SMS dispatch while keeping the rest of the outreach pipeline live so composition, coherence gate, and axis-selection run end-to-end.
- Capture per-cycle `MotivationVector`, selected axis, consumption action, and rejection events into a scenario-tagged log.
- Optionally: replay a captured real-traffic scenario (a day of perception events + Mark's SMS history) at accelerated time to see how Layer 2 would have selected axes across that day.

**Scope explicitly outside the harness:** the harness does not replace dogfood runs on Mark's live instance. It provides bulk reference data. Final go/no-go for flag flips still uses live-instance observation.

**Existing affordances to reuse:**
- `///test` mode (from memory — you flagged this as possibly applicable). Worth investigating whether its bypass-storage hook is the right place to hang the no-Twilio dispatch suppression, or whether it serves a different purpose that should stay separate.
- The existing Perception source architecture — a new `ScriptedPerceptionSource` can be registered that reads events from a JSON or JSONL scenario file and emits them on an accelerated timer.
- The Emergence SQLite DB is already a separate store; the harness can write to it directly using a scenario-tag column so bulk runs don't pollute the real emergence data.

**Deliverable:** working harness runnable from a CLI (`dotnet run --project tools/AniRuntime.TestHarness --scenario path/to/scenario.jsonl`) that produces a scenario-tagged data file consumable by the dashboard rework (once it exists) or by simple ad-hoc analysis.

**Effort estimate:** two to three days.

---

### Phase 2a — vector scoring, measurement only

**Goal:** produce the three-axis motivation vector every cycle, log it, change nothing about behavior.

**Changes:**
- New file: [src/AniRuntime.Core/Models/MotivationVector.cs](../../src/AniRuntime.Core/Models/MotivationVector.cs) — record struct.
- Modify [src/AniRuntime.Core/MotivationScorer.cs](../../src/AniRuntime.Core/MotivationScorer.cs) — add `ScoreVector(...)` method alongside existing `Score(...)`. Existing scalar method unchanged. `ScoreVector` returns full 3-axis vector; `Score` delegates to `ScoreVector(...).Relatedness`.
- Modify DesireEngine (Loops) — call `ScoreVector`, log all three axis scores at Info level, use `.Relatedness` for existing drift math. Zero behavior change.
- New AniOptions flag: `MotivationVectorLoggingEnabled` (default `true`). Easy kill switch if logs get noisy.
- Tests: unit-level `MotivationScorerTests` — verify each axis formula against representative inputs (caregiver-thought, self-thought, world-thought). Verify delegation contract (scalar == vector.Relatedness).

**Acceptance criteria:**
- Build green, all existing tests pass.
- One week of journal logs contains every cycle's `motivation: relatedness=0.X autonomy=0.Y competence=0.Z` triple.
- `OutreachThreshold` gating still uses scalar, and outreach frequency matches pre-deploy baseline (no regression).

**Rollback:** toggle the flag off, lose only the logging. No schema change.

**Estimated effort:** half a day. Mirrors Layer 1 Phase 1a exactly — instrumentation only.

---

### Phase 2b — vector desire state, parallel drift

**Goal:** maintain three desire scalars in `DesireState`; drift all three per cycle; use only the relatedness scalar for any decision.

**Changes:**
- Modify [src/AniRuntime.Core/Models/DesireState.cs](../../src/AniRuntime.Core/Models/DesireState.cs) — add `DesireSelfExpression`, `DesireWorldEngagement`, and their threshold fields. JSON-compat with existing serialised state: new fields default to 0, existing persisted snapshots deserialise cleanly.
- Modify DesireEngine drift logic — drift three scalars with axis-specific λ from `AniOptions`. Axis-specific circadian modifiers out of scope for 2b; same modifier applies to all three.
- New AniOptions fields: `LambdaAutonomy` and `LambdaCompetence`. **Starting defaults: equal to `LambdaRelatedness`.** Per Mark's Apr 23 review: don't pre-gate the rhythm. Start all three axes on the same λ, run the instance, and let the observed distribution reveal whether any axis needs retuning. The whole point of building an emergence-driven architecture is to let emergence tell us the cadence, not to script it.
- Logging: per-cycle log of all three drift values and their thresholds. Data surfaced for the dashboard rework (see §9). No new panels added to the current dashboard.
- Tests: `DesireEngineTests` — verify all three axes drift; verify none of them affect outreach decision; verify serialisation round-trip with a pre-Layer-2 snapshot.

**Acceptance criteria:**
- Build green, all existing tests pass.
- One week of cycles produces a distribution of `DesireSelfExpression` and `DesireWorldEngagement` values across [0, 1]. The distribution's shape is itself the measurement — we report it, we don't grade it against a pre-chosen target.
- No observed change in outreach cadence relative to pre-deploy.

**Rollback:** revert to Phase 2a — the new fields are additive; setting λ to 0 freezes drift without breaking the schema.

**Estimated effort:** one to two days including λ tuning and dashboard panel.

---

### Phase 2c — consumption actions behind a flag

**Goal:** wire the two new consumption actions (`SelfStateReflection`, `WorldElaboration`) and the axis-selection policy. Default off.

**Changes:**
- New interfaces: `ISelfStateReflectionWriter`, `IWorldElaborationWriter` (or unified `IAxisConsumptionAction`). Mirror the existing outreach-action surface.
- Implementation of both writers (Memory layer). Each uses the inner-thought model (`ani-v6-inner` / `ani-v7-inner`) with a scoped prompt that forbids caregiver reference (for SelfState) or requires World Layer seed reference (for WorldElaboration). Prompt content is Layer 5's domain; Phase 2c ships minimal stub prompts; Layer 5 replaces them with the considered prompt-variant set.
- Modify cognitive cycle processor — at the decision point, evaluate all three axes, select per §2.3 policy, dispatch to the appropriate consumption action.
- New AniOptions flags:
  - `DesireVectorSelectionEnabled` (default `false`) — master switch. When off, only the relatedness axis is consumed (current behavior).
  - `SelfStateReflectionEnabled` (default `false`).
  - `WorldElaborationEnabled` (default `false`).
  - `WorldElaborationAntiThrashWindowMinutes` (default 60) — only non-consumption guard on non-relatedness axes. Same-seed-twice protection for memory quality, not social pacing.
  - `SelfStateCaregiverPivotThreshold` — similarity threshold above which a generated self-state reflection is rejected for pivoting to the caregiver.
  - Note: no generic autonomy/competence cooldowns. Consumption itself is the reset mechanism per §2.1.
- Rejection path: `SelfStateReflection` generation goes through a post-generation classifier (simple substring + embedding-similarity check against the primary contact's name/identity) — if the generation pivots to caregiver, reject, log a `AxisRejection` metric, do not consume the desire (drift continues). Mirrors the AC3 null-result pattern.
- Tests: integration test per consumption action — axis crosses threshold in isolation, action fires, write lands, desire resets. Rejection test — caregiver-pivot in self-state generation, write rejected, desire not consumed.

**Acceptance criteria:**
- All flags default off. Default-off run produces identical behavior to Phase 2b.
- Flags-on dogfood run over one week on Mark's instance produces:
  - At least one `SelfStateReflection` write per day on average.
  - At least one `WorldElaboration` per World-Layer-seeded cycle (gated availability permitting).
  - Rejection rate on self-state generation below 30% (above that threshold suggests the inner-thought model needs Layer 4 corpus support before Layer 2c can be reliable).
  - No regression in outreach cadence or quality.

**Rollback:** toggle `DesireVectorSelectionEnabled` off. All axes return to the Phase 2b parallel-drift state with only relatedness consumed.

**Dependencies:**
- **Phase 3.0 Layer 1 Activation must be complete** with exit criteria met. Without non-caregiver substrate in the retrieval pool, competence-axis consumption has nothing to pull from.
- **Phase 3.1 Test harness available** for pre-flip-on volume observation without Twilio costs.

**Estimated effort:** three to five days including dogfood tuning and the rejection classifier calibration.

---

### Phase 2d — multi-axis resolution and emergence telemetry

**Goal:** handle the case where multiple axes cross threshold in the same cycle; log the resolution as emergence data.

**Changes:**
- Modify cognitive cycle processor — if more than one axis is above threshold in the same cycle, apply weighted stochastic selection per §2.3. Log `AxisContention` event with the vector, the selected axis, and the weights.
- New Emergence layer metric: `axis_fragmentation_rate` = fraction of cycles per week where two or more axes cross threshold simultaneously. Data surfaced for the dashboard rework (no new panels in the current dashboard).
- Early-warning metric (from the design doc's principal risks): sustained fragmentation above a threshold (e.g., 15% of cycles) triggers a telemetry flag that recommends Phase 2c rollback without affecting Phases 2a/2b. This is a recommendation, not an automatic action.
- Tests: `CognitiveCycleProcessorTests` — multi-axis contention scenario, verify selection distribution matches the weighting over N simulated cycles.

**Acceptance criteria:**
- Fragmentation rate stays below 10% of cycles in the first month of Phase 2d being live.
- When it does fire, the axis-selection distribution is informative (i.e., it's not always picking the same axis regardless of weights).
- Dashboard panel reads cleanly.

**Rollback:** toggle `DesireVectorSelectionEnabled` off; resolution policy is inert when selection is off.

**Estimated effort:** one to two days.

---

## 4. Measurement plan

Three headline metrics, tracked from Phase 2a onward:

| Metric | Phase introduced | Target after Phase 2c flip |
|---|---|---|
| **Non-relatedness axis selection rate** | 2a (via vector log), 2c (actual selection) | ≥20% of cycles (per design doc §3.2 success criterion) |
| **Axis fragmentation rate** | 2d | <10% of cycles |
| **Self-state reflection rejection rate** | 2c | <30% of self-state generations |

**Data surfacing.** Per-phase emits the structured fields consumed by the dashboard rework:

| Phase | Fields emitted | Consumer |
|---|---|---|
| 2a | `motivation.relatedness`, `motivation.autonomy`, `motivation.competence` (per cycle) | Dashboard "Desire Axes" view (future) |
| 2b | `desire.relatedness`, `desire.autonomy`, `desire.competence`, `threshold.*` (per cycle) | Dashboard "Desire Axes Over Time" view (future) |
| 2c | `axis.selected`, `consumption.action`, `consumption.rejected`, `rejection.reason` | Dashboard "Axis Selection" view + Emergence tab (future) |
| 2d | `contention.count`, `contention.weights`, `contention.selected` | Dashboard "Axis Contention" view (future) |

No new panels added to the current dashboard during Layer 2 implementation — dashboard rework is a sibling workstream (§9). The data is captured and queryable from the Emergence SQLite DB and structured Serilog output regardless of which UI eventually reads it.

The 30-day success criterion from the parent design doc remains: ≥20% of cognitive cycles select a non-relatedness axis, and the resulting writes read as subject-diverse on review (dashboard or ad-hoc).

---

## 5. Research artifact updates

Each phase deploy updates multiple research artifacts. Listing them explicitly so nothing falls off the list.

### 5.1 Research log

[`docs/research/ANI-Research-Log.md`](../research/ANI-Research-Log.md) — one entry per phase deploy, same structure as existing Feature deploy entries.

First entry (Phase 2a deploy) should cite:

- Ryan & Deci (2000) for the SDT three-needs frame.
- Liu et al. (2025) as the prior-art on motivation scoring (Feature 33) that Layer 2 extends from scalar to vector.
- Oudeyer & Kaplan (2007) for the established competence-as-intrinsic-motivation literature.
- Paper 2 §6.15 (Experiential Poverty revision note) for the substrate-dependence precondition that Layer 2 sits on top of.

### 5.2 Phase tracker

[`docs/spec/ANI-Phase-Tracker.md`](./ANI-Phase-Tracker.md) — updates, not a new theme.

Layer 2 is already a sub-member of **Theme G — Agentic Lens / Anti-Centrality Architecture**. Layer 2's phase-tracker update is adding Feature 42 + the Phase 3.0 Layer 1 Activation prerequisite + the Phase 3.1 Test Harness deliverable under the existing Theme G entry, along with updated sequencing notes reflecting the Apr 23 review.

**New tracker entry** for the Dashboard rework: **Theme I — Dashboard as Research Tool** (§9.1). Stub entry added at the same time as this plan so the workstream is visible during Consolidation Review.

### 5.3 Reference library

[`docs/research/ANI-Research-References.md`](../research/ANI-Research-References.md) — ensure Ryan & Deci 2000 is present; cross-reference Oudeyer & Kaplan 2007 (already in library per design doc) against competence-axis specifically.

### 5.4 Paper updates

- **Paper 2** §6.15 — add a forward-pointer note: "The architectural response to substrate dependence is developed in Paper 3 Contribution 4 Layer 2 (Feature 42)."
- **Paper 3** Contribution 4 — Layer 2 gets its own subsection (§3.2 already drafted; expand from this plan doc as phases ship).
- **Paper 3** methodology — if the synthetic-volume test harness produces publishable data, credit the harness as the method note (a single paragraph explaining accelerated-cycle observation as a companion-AI research technique).

### 5.5 Emergence layer docs

[`docs/spec/emergence/`](./emergence/) — add a document explaining Layer 2 as the mechanism that gave the Emergence layer's growth-observation intent a first-class runtime representation. Short doc, ties the original emergence design to the measurable outputs Phase 2a onward produces.

### 5.6 Memory index

Claude's persistent project memory (`~/.claude/projects/<slug>/memory/MEMORY.md`) — add a line referencing this plan so future conversations land on Layer 2 context cleanly. Done by Claude when the plan is approved.

---

## 6. Principal risks (mirrored from design doc §3.2, with mitigation specifics)

- **Desire fragmentation → incoherent behavior.** Mitigation: Phase 2d's weighted stochastic selection with logged emergence. If fragmentation rate exceeds 15%, automatic telemetry recommendation to roll back to Phase 2b.
- **Competence-desire producing arbitrary world-engagement without substrate.** Mitigation: consumption gate on World Layer seed availability (§2.4).
- **Autonomy-desire producing self-referential rumination on Mark.** Mitigation: post-generation rejection for caregiver-pivot, drift continues without consumption (§2.4).
- **Prompt-stub quality in 2c before Layer 5's prompt-variant work.** Mitigation: minimal stub prompts are explicitly scoped as placeholders; Phase 2c acceptance criteria include "prompt quality to be revisited once Layer 5 prompt-variants are in place." The stubs should produce usable-but-not-polished writes.
- **Schema migration for `DesireState` JSON.** Mitigation: new fields default to 0, old snapshots deserialise cleanly (Phase 2b tests verify round-trip).

---

## 7. Decisions from Apr 23 review

Mark's first review pass resolved five of the original open questions. Recording the resolutions here for clarity rather than pretending they're still open.

1. **λ_autonomy and λ_competence starting values.** *Resolved: don't pre-gate.* Start both at the same value as `λ_relatedness`. Let the observed distribution reveal whether retuning is needed. The whole point of an emergence-driven architecture is to let the system show us its rhythm rather than scripting one.
2. **Cooldown durations per axis.** *Resolved: no generic cooldowns on non-relatedness axes.* The outreach cooldown exists for recipient-protection; self-state and world-elaboration have no recipient. Consumption itself resets each axis — that is the pacing mechanism. Only world-elaboration gets a light anti-thrash guard (§2.1). Relatedness keeps its existing cooldown unchanged.
3. **Dashboard placement.** *Resolved: sibling workstream.* Dashboard rework is tracked separately as Theme I (§9.1). Layer 2 specifies the data it surfaces; panel construction happens inside the reworked dashboard.
4. **Phase 2c dogfood target.** *Resolved: Mark's live instance, Twilio-suppressed via the test harness (§3.1) during high-volume runs.* The synthetic-volume test harness is added as a Phase 3.1 deliverable so bulk observation does not incur Twilio cost.
5. **Feature numbering.** *Resolved: Feature 42.* New feature in sequence. Feature 33 stands as originally shipped — Layer 2 supersedes its use-site but does not retire its place in the feature history. Clean numbering, no renumbering of prior features.

### 7.1 Remaining genuinely open items

- **Serialisation schema for the extended `DesireState`.** The existing state store already persists `DesireState`. Adding two new scalars requires the deserialiser to default them to zero when reading pre-Layer-2 snapshots. Phase 2b tests cover this, but worth flagging for any Phase 6 memory-reform work that touches the same state-store surface.
- **Whether `CircadianModifier` applies equally across axes.** Current assumption is yes (one modifier, all three axes). If Phase 2b data shows clear per-axis circadian differences (e.g., autonomy desire genuinely higher late at night, competence desire lower during quiet hours), axis-specific modifiers become a Phase 2e candidate. Not a Phase 2a/b/c/d concern.
- **Interaction with `EmotionDesireModifier` (Feature 35).** Feature 35 modulates the scalar desire based on emotional state. Under Layer 2 this becomes a modulation of all three axes — but emotional state does not affect all three equally (a warm emotional state plausibly raises relatedness more than competence). Phase 2a implementation should either treat Feature 35 as a relatedness-only modifier (cleanest) or extend it to a per-axis modifier (more work, probably deferrable to Phase 2e). Starting assumption: relatedness-only for 2a/2b, revisit for 2c.

---

## 8. Recommended sequencing

If approved:

1. **Phase 3.0 Layer 1 Activation.** Flip the three Layer 1 flags on Mark's live instance. Two-week minimum observation window. No code changes; configuration + measurement only.
2. **Phase 3.1 Test harness (parallel with 3.0).** Two to three days of infrastructure work to stand up the synthetic-volume cycle driver. Can ship independent of Phase 3.0's outcome.
3. **Phase 2a.** Half-day, zero risk. Instrumentation-only. Safe to ship even while Phase 3.0 is in observation.
4. **Phase 2b after one week of 2a data.** λ values stay equal to λ_relatedness per §7.1; no pre-tuning.
5. **Phase 2c after Phase 3.0 exit criteria are met AND Phase 3.1 test harness is operational.** Flag-off deploy first, then a harness-driven scenario pass, then flag-on dogfood on Mark's live instance for one week.
6. **Phase 2d whenever 2c is steady.** Pure additive telemetry; can ship as soon as 2c's contention events start appearing in real traffic.

Total calendar estimate from Phase 3.0 start to Phase 2d live: 5–7 weeks. Design doc §5 estimated "2–4 weeks" for Layer 2 itself; the extra time is Phase 3.0 observation + Phase 3.1 infrastructure, both of which are prerequisites that were underspecified in the original design-doc timeline.

---

## 9. Adjacent workstreams (tracked separately)

Two workstreams surfaced by this plan that are not in Layer 2 scope but that Layer 2 depends on or integrates with. Each gets its own plan doc when prioritised.

### 9.1 Dashboard as Research Tool rework (Theme I)

Mark's Apr 23 review note: the dashboard has accumulated research-contextual panels that make sense at the time of a given feature's deploy but become opaque weeks or months later. The dashboard's role should be legible to the researcher (i.e., Mark) as a tool for *understanding what is happening and applying it to the research*, not a residual archaeology site of past feature motivations.

Out of scope for Layer 2. Captured as a sibling workstream with its own design phase. When Layer 2 Phase 2a/2b/2c/2d ships, the structured fields listed in §4 are available for the reworked dashboard to consume.

Not drafting the dashboard plan doc yet — Mark flagged it as a larger discussion. When prioritised, new doc: `docs/spec/ANI-Dashboard-Research-Tool-Rework.md`.

### 9.2 Synthetic-volume test harness

Scope specified in §3.1. Useful beyond Layer 2 — any subsequent feature that wants high-volume observation without Twilio cost benefits from the same harness. Recommendation: build the harness as a standalone CLI project under `tools/` rather than embedding it in any feature's deploy path.

---

*End of Layer 2 plan v2. Review welcome. Next design artifact on deck (once Mark approves or revises this one): Layer 3 implementation plan with its own phase decomposition, and — when prioritised — the Theme I Dashboard rework.*
