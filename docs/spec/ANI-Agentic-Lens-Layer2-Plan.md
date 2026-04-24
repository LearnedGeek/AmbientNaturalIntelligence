# Agentic Lens — Layer 2 Implementation Plan (Desire Axis Decoupling)

**Status:** Draft for review. Not yet approved for implementation.
**Authored:** April 23, 2026 (Claude, from the design doc + current codebase).
**Parent design:** [`ANI-Agentic-Lens-Design.md`](./ANI-Agentic-Lens-Design.md) §3.2.
**Paper placement:** Paper 3 Contribution 4 (Agentic Lens), Layer 2 subsection.
**Architectural thesis:** the caregiver-centrality gravity observed in companion AI is reinforced by a scalar desire engine that admits only one motivational axis. Replace the scalar with a three-axis vector drawn from Ryan & Deci Self-Determination Theory (relatedness, autonomy, competence), each with its own drift rate, threshold, and consumption action.

---

## 0. Scope discipline

**In scope:** the runtime representation of desire, the scoring that drives drift, the selection policy that picks which axis a cognitive cycle acts on, and the consumption actions that relieve each axis.

**Explicitly out of scope:**
- Prompt templates for autonomy- or competence-driven cycles (that is Layer 5's job; Layer 2 only surfaces which axis won, Layer 5 chooses the prompt).
- World Layer durability (Layer 3).
- Training-corpus rebalancing (Layer 4).
- Any changes to the conversation-reply pipeline. Layer 2 touches inner-thought cycles and outreach decisions only; conversation stays caregiver-directed by definition.

**Dependencies not yet shipped:**
- Layer 1 phases 1b (MMR rerank) and 1c (protected slots) exist behind default-off flags. Layer 2c benefits from those being on — otherwise the non-caregiver substrate isn't there for competence-desire to consume. Plan handles this by keeping 2a/2b safe even under current defaults, and only tightening 2c coupling once Layer 1 data supports turning its flags on.
- Layer 3 (World Layer durability) is not yet designed in detail. Layer 2's competence-axis consumption (World Layer elaboration) produces writes that Layer 3 will eventually make durable. In the interim, competence-consumption just writes into the existing World Layer path; Layer 3 gets reinforced when it lands.

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

---

## 2. Target architecture

### 2.1 Vector desire state

`DesireState` gains two additional scalars and their associated thresholds:

| Axis | Field | Drift characteristic | Consumption |
|---|---|---|---|
| **Relatedness** | `DesireToConnect` (existing, renamed no — backwards compat via property accessor) | λ_relatedness, same as today | Outreach event (existing path) |
| **Autonomy** | `DesireSelfExpression` (new) | λ_autonomy, drifts upward when no self-state reflection has been written for a window | `SelfStateReflection` MemoryWriteAction |
| **Competence** | `DesireWorldEngagement` (new) | λ_competence, drifts upward when World Layer engagement is below a baseline rate | `WorldElaboration` MemoryWriteAction or scheduled reflection cycle |

Each axis has its own randomised threshold (matching the existing pattern where `OutreachThreshold` is re-rolled per evaluation) and its own cooldown after consumption. The existing `CircadianModifier` applies to all three (a tired-Ani-at-3am should be less motivated on any axis, not just relatedness), unless later data suggests per-axis modifiers.

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

Existing hard gates (unanswered-count, send-gap, cooldown) remain in place for the relatedness axis only. Autonomy and competence axes have their own, lighter cooldowns (self-state reflection has no recipient and can run more freely; world-elaboration has a "don't elaborate the same seed twice in one hour" guard).

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
- New AniOptions fields: `LambdaRelatedness` (existing, renamed internally but kept compat), `LambdaAutonomy` (default value chosen to give ~1 autonomy-threshold-cross per day under typical activity), `LambdaCompetence` (default ~1 per day).
- Logging: per-cycle log of all three drift values and their thresholds. Dashboard (Blazor RCL) gets a "Desire Axes" panel showing the three scalars over time. Panel is read-only, purely observational.
- Tests: `DesireEngineTests` — verify all three axes drift; verify none of them affect outreach decision; verify serialisation round-trip with a pre-Layer-2 snapshot.

**Acceptance criteria:**
- Build green, all existing tests pass.
- One week of cycles produces a distribution of `DesireSelfExpression` and `DesireWorldEngagement` values across [0, 1]. The distribution's shape is itself the measurement — if all three axes drift at similar rates, λ values are reasonable; if one axis saturates or never rises, λ needs retuning before Phase 2c.
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
  - Axis-specific cooldowns, min-confidence thresholds, and the rejection rule for caregiver-pivot in self-state writes.
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

**Dependencies:** benefits substantially from Layer 1's diversity flags (`RetrievalDiversityEnabled`, `RetrievalProtectedSlotsEnabled`) being on. Without them, the inner-thought retrieval pool on a competence-axis cycle may still be 90% caregiver-origin and the World elaboration has nothing to pull from. Recommendation: gate the Phase 2c flip on at least two weeks of Layer 1 flags-on data.

**Estimated effort:** three to five days including dogfood tuning and the rejection classifier calibration.

---

### Phase 2d — multi-axis resolution and emergence telemetry

**Goal:** handle the case where multiple axes cross threshold in the same cycle; log the resolution as emergence data.

**Changes:**
- Modify cognitive cycle processor — if more than one axis is above threshold in the same cycle, apply weighted stochastic selection per §2.3. Log `AxisContention` event with the vector, the selected axis, and the weights.
- New Emergence layer metric: `axis_fragmentation_rate` = fraction of cycles per week where two or more axes cross threshold simultaneously. Dashboard panel in the Emergence tab.
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

Plus the existing Emergence layer dashboard, extended:

- Desire axes time-series panel (2b).
- Axis selection distribution panel (2c).
- Fragmentation trend panel (2d).

The 30-day success criterion from the parent design doc remains: ≥20% of cognitive cycles select a non-relatedness axis, and the resulting writes read as subject-diverse on dashboard review.

---

## 5. Research-log integration

Phase 2a deploy entry should cite:

- Ryan & Deci (2000) for the SDT three-needs frame.
- Liu et al. (2025) as the prior-art on motivation scoring (Feature 33) that Layer 2 extends from scalar to vector.
- Oudeyer & Kaplan (2007) for the established competence-as-intrinsic-motivation literature.
- Paper 2 §6.15 (Experiential Poverty revision note) for the substrate-dependence precondition that Layer 2 sits on top of.

Each subsequent phase deploy gets its own research-log entry with the measured axis-distribution shape as supporting data.

---

## 6. Principal risks (mirrored from design doc §3.2, with mitigation specifics)

- **Desire fragmentation → incoherent behavior.** Mitigation: Phase 2d's weighted stochastic selection with logged emergence. If fragmentation rate exceeds 15%, automatic telemetry recommendation to roll back to Phase 2b.
- **Competence-desire producing arbitrary world-engagement without substrate.** Mitigation: consumption gate on World Layer seed availability (§2.4).
- **Autonomy-desire producing self-referential rumination on Mark.** Mitigation: post-generation rejection for caregiver-pivot, drift continues without consumption (§2.4).
- **Prompt-stub quality in 2c before Layer 5's prompt-variant work.** Mitigation: minimal stub prompts are explicitly scoped as placeholders; Phase 2c acceptance criteria include "prompt quality to be revisited once Layer 5 prompt-variants are in place." The stubs should produce usable-but-not-polished writes.
- **Schema migration for `DesireState` JSON.** Mitigation: new fields default to 0, old snapshots deserialise cleanly (Phase 2b tests verify round-trip).

---

## 7. Open questions for Mark

1. **λ_autonomy and λ_competence starting values.** Design doc doesn't pin these. Phase 2b proposes "aim for ~1 cross per day." Is that the right ballpark, or should autonomy-reflection be rarer (say, every 3 days) and competence-elaboration more frequent (say, per World-Layer-seeded cycle)?
2. **Cooldown durations per axis.** Outreach cooldown is ~hours. Self-state and world-elaboration cooldowns probably want to be shorter (minutes to a single-digit-hours). Happy to pick defaults from the λ values but worth confirming.
3. **Dashboard placement.** New panels in the existing Emergence tab, or in a new "Desire Axes" tab? The Emergence tab is already getting crowded; a separate Desire Axes tab with three sub-panels (state, selection, contention) may read better.
4. **Who the Phase 2c dogfood runs on.** Mark's live instance (which is also where parrot diagnostics are happening) or a staging instance that mirrors state but doesn't actually SMS? The existing hard gates protect outbound quality; my read is Mark's live instance is fine as long as the default-off flags are only flipped on for one-week test windows.
5. **Relationship to Feature 33 (Liu et al.) attribution.** Feature 33 is in v6 training notes as already deployed. Layer 2 extends it. Research log framing question: extension of Feature 33, or new feature number (Feature 36+) that supersedes? Existing practice has been "Feature N" per clean-sprint addition.

---

## 8. Recommended sequencing

If approved:

1. **Phase 2a immediately** (half-day, zero risk). Instrumentation-only. Gets the three-axis log into production so we start building the reference distribution.
2. **Phase 2b after one week of 2a data.** Λ values picked from the 2a signal (if relatedness is drifting faster than expected even at current λ, other λ values start from its actual empirical rate rather than the current notional rate).
3. **Phase 2c after Layer 1 flags are flipped on for at least two weeks.** Blocks Phase 2c on having diverse substrate to act on. If Layer 1's flag-flip dogfood data (not yet started) reveals tuning issues, those get fixed first.
4. **Phase 2d whenever 2c is steady.** Pure additive telemetry; can ship as soon as 2c's contention events start appearing.

Total calendar estimate from 2a start to 2d live: 4–6 weeks, closely tracking the design doc's §5 "2–4 weeks" estimate for Layer 2 with the additional gating on Layer 1 activation.

---

*End of Layer 2 plan. Review welcome. Next design artifact on deck (once Mark approves or revises this one): Layer 3 implementation plan with its own phase decomposition.*
