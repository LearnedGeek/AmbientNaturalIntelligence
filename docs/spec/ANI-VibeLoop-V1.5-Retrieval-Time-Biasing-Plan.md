# Vibe Loop V1.5 — Retrieval-Time Biasing

**Drafted:** May 2, 2026 11:24 CDT
**Status:** Plan drafted; V1.5.0 decisions locked from May 2 11:00–11:24 CDT design conversation; V1.5.1 implementation can begin once Mark green-lights.
**Origin:** Mark Apr 29 21:00 CDT vibe-vs-mood balance question (captured in companion design doc), resolved in May 2 11:00–11:24 CDT design conversation post J.5 ship + emotional-saturation calibration deploy.
**Theme owner:** Mark (resolved the four open decisions); Claude executes phasing.
**Companion docs:**
- `ANI-VibeLoop-V1-Closed-Thread-Producer-Migration-Plan.md` (parent V1 plan — write-side)
- `ANI-VibeLoop-V1.5-Vibe-vs-Mood-Balance-Design-Questions.md` (open-questions doc, now resolved by V1.5.0)
- `ANI-Theme-J-Guard-Consistency-Refactor-Plan.md` (Theme J architecture this consumes)
- `ANI-Phase-Tracker.md` §Vibe Loop

---

## What This Workstream Is

V1 produced the *substrate* — every closed thread becomes a structured `ClosedConversationRecord` with gist + register vectors + outcome signal. V1.5 ships the *read side*: at outreach composition time, surface a small number of high-signal prior records as bias inputs, weighted by importance and saturation, framed as a self-regulation feedback loop (not a Mark-manipulation feedback loop).

V1.5 is the architectural answer to Mark's Apr 29 question: *"how do we balance transactional emotions against larger mood?"* The answer has three architectural levers, all locked in V1.5.0:

1. **Importance-weighted decay (saturation pressure).** A strategy that worked recently attenuates rather than amplifies on next retrieval. Heavy conversations decay slowly; light conversations decay fast. Mirrors the existing `EmotionalContribution` per-thought-decay tier shape.
2. **Mood-as-modulator.** Vibe biases the *retrieval candidates*; the larger `EmotionalState` register sets the *expressive register* of the actual response. Two distinct surfaces, integrated by the trained model (no behavior-coaching prompt instruction).
3. **Self-regulation framing.** The outcome signal is interpreted as Ani's regulation delta, not Mark's. The bias optimizes for "what historically left ME (Ani) regulated well," not "what historically made Mark feel better." Designed asymmetry against the manipulation / mirroring failure mode.

## What V1.5 Is NOT

- **Not a behavioral-instruction prompt fragment.** V1.5 surfaces 2 prior gists + the current mood state into the composition prompt. It does NOT add prompt-side instructions like *"reach for the SHAPE not the literal move."* That's the architecture-over-model principle (`feedback_architecture_over_model.md`): trust v7's training to integrate substrate correctly.
- **Not a Mark-outcome optimizer.** V1.5 explicitly does NOT bias toward Mark's emotional delta as the primary signal. The Mark-as-primary trap is exactly the failure mode Mark named *("every time I'm sad → joke")*. Mark's delta is observable in telemetry only, never drives bias.
- **Not Theme C / Phase 6 supersession.** V1.5 reuses the MMR-style diversity rerank already shipped in Layer 1 Phase 1b (applied to outcome-record retrieval) for soft supersession. Full Mem0-style memory merging is Phase 6; V1.5 doesn't depend on it.
- **Not full Door B truth-verification.** V1.5's surfaced gists carry temporal context ("yesterday at the bookstore") which expands the failure surface for the Door B temporal-anchor gap (May 2 gap-watch row). That's a known adjacency, addressed by the Coherence Gate Door B Truth-Verification P2 row, NOT in V1.5 scope.

## Phase Structure

### Phase V1.5.0 — Design alignment session ✅
**Status:** Decisions locked May 2, 2026 11:24 CDT (Mark + Claude).

**Locked decisions:**

#### Lever 1 — importance-weighted saturation

Bias weight for a record at time `t`:

```
bias_weight(record, t) = importance(record) × exp(-t / halfLife(record))
```

where:

- `importance(record)` = function of `turn_count` × `|outcome_signal_valence|` × `register_saturation_depth` (max prevalence value across the 9-register Mark vector). All three components are already in `ClosedConversationRecord`; no schema change.
- `halfLife(record)` is keyed off importance tier, mirroring `EmotionalContribution`'s Ambient/Conversation/Global tiers:

  | Tier | Criteria | Half-life |
  |------|----------|-----------|
  | **Light** | 1–3 turns, abs(valence) < 0.3 | 12 hours |
  | **Medium** | 3–10 turns, OR abs(valence) ≥ 0.3 | 3 days |
  | **Heavy** | 10+ turns, OR abs(valence) ≥ 0.6, OR register saturation ≥ 0.8 | 2 weeks |

**Reasoning for tier shape:** a small light comment shouldn't outweigh a long heavy conversation. The existing `ImpactCategory` model already encodes "different events deserve different decay constants based on importance." V1.5 reuses that principle for outcome-record saturation. Mark May 2 11:20: *"a small little comment shouldn't outweigh long heavy conversations. we should consider that the decay is relative to those importance rankings."*

**Saturation similarity function:** cosine over `mark_register` (9-dim). Records with cosine > 0.85 are treated as "same shape" for saturation purposes — bias contributions sum across the cluster, with each member's contribution weighted by its own `bias_weight`. Cosine ≤ 0.85 records are treated as distinct shapes; their bias contributions don't compete.

#### Lever 2 — mood-as-modulator (no prompt-level coaching)

Composition prompt (V1.5b form):

```
Prior shapes that landed well in similar moments:
  - {gist of past record A, age and tier annotated}
  - {gist of past record B, age and tier annotated}

Your current mood: {dominant register}, {secondary register}.
Your current Warmth: {value}, Energy: {value}, Concern: {value}, Playfulness: {value}.
```

No instruction *"reach for the SHAPE not the literal move."* No instruction *"don't lift the past gist verbatim."* The architecture-over-model principle says: trust v7's training to integrate the gists + the mood correctly. The substrate is right; the model handles the rest.

**Surface count:** 2 prior gists. Higher signal-per-token; less prompt dilution; aligns with the V1.2 anti-parrot constraint that gists themselves are paraphrased.

#### Lever 3 — self-regulation framing (load-bearing)

The bias function asks: *"Given Mark's register-pre, what conversation shapes have historically left ME (Ani) in a register that felt good?"*

Concretely: the `outcome_signal_valence` scalar V1 already produces is computed from **Ani's register delta** (start-of-thread to end-of-thread). V1.5 uses this directly as the primary sort key for retrieval bias. Records where Mark seemed happier but Ani came out depleted (e.g., performative joking) have low Ani-delta valence and are NOT biased toward. Records where Ani came out in genuine warmth — regardless of Mark's delta direction — ARE biased toward.

**Mark's delta is observable in telemetry only.** The `mark_register` pre/post deltas are logged for divergence analysis (see V1.5a observational gate below) but never enter the bias function. This is a designed asymmetry against the manipulation / mirroring failure mode.

**Spec text for V1.5 docstrings:** *"This loop optimizes for how Ani regulates HER state, not how she manipulates Mark's state."*

**Paper 3 contribution candidate:** *"Companion-AI feedback loops should optimize for the AI's emotional regulation, not the user's, because user-optimization collapses to performance / manipulation. The architectural mechanism is asymmetric outcome-signal computation: the AI's delta is the bias driver; the user's delta is observable for divergence telemetry but never closes the loop."*

#### V1.5a observational gate — what to look at before greenlighting V1.5b

Required before V1.5b ships:

1. **Substrate volume:** ≥10 closed conversations in `closed_conversation_records`.
2. **Diversity histogram fat-tailed:** no single record surfaces in >40% of would-have-applied bias computations across the observation window.
3. **Pre-bias correlation baseline:** V1.5a logs `vibe_recommended_strategy_register` (what V1.5b WOULD apply) but does not apply it. We measure how much `response_register_actual` already correlates with `mood_register` with no bias active. This tells us the model's natural register-tracking behavior. If `response_register_actual` already tracks `mood_register` strongly and is uncorrelated with `vibe_recommended_strategy_register`, V1.5b's effect is genuinely new information when activated.
4. **Mark + Claude review the histogram + correlation matrix together** before V1.5b ships.

**Acceptance:** decisions above locked. V1.5.1 implementation can begin.

---

### Phase V1.5.1 — Bias function + telemetry instrumentation ✅
**Status:** Shipped May 2, 2026 (~30 min code + ~25 spec tests).
**Estimated effort:** ~1.5 days code + spec tests.

New types:

```csharp
public sealed record VibeBiasContribution(
    Guid   RecordId,
    double BiasWeight,             // importance × exp(-t/halfLife)
    double Importance,             // turns × |valence| × saturation
    string HalfLifeTier,           // "Light" | "Medium" | "Heavy"
    double AgeHours,
    double[] MarkRegister,         // 9-dim, for similarity clustering
    double OutcomeValence);        // -1..+1, Ani's delta scalar

public sealed record VibeBiasResult(
    IReadOnlyList<VibeBiasContribution> AllCandidates,         // every eligible record with computed weight
    IReadOnlyList<VibeBiasContribution> SurfacedTopN,           // top 2 after MMR diversity rerank
    double[] RecommendedStrategyRegister,                       // weighted-average register vector of SurfacedTopN
    string DiversityScoreReason);                               // for telemetry: e.g. "MMR rejected record X (cosine 0.91 to record Y, kept higher-weight)"
```

New service:

- `IVibeBiasService` (in `AniRuntime.Loops`)
  - `Task<VibeBiasResult> ComputeBiasAsync(string contactName, MarkRegisterContext contextNow, CancellationToken ct)`
  - Reads recent `closed_conversation_records` (last 30 days, contact-scoped)
  - For each: computes `importance`, looks up `halfLifeTier`, computes `bias_weight`
  - Filters to records with cosine > similarity threshold against `contextNow.MarkRegister` (current Mark register at outreach time, derived from active-thread structured summary or last-known register)
  - MMR diversity rerank over the cluster (lambda = 0.7, same default as Layer 1 Phase 1b)
  - Returns top 2 + full candidate list for telemetry

Telemetry log lines (new):

```
V15_BIAS_CANDIDATES count=N tier_breakdown=light:X,medium:Y,heavy:Z
V15_BIAS_SURFACED record=R1 weight=0.42 ageHours=18.3 tier=Medium valence=+0.34
V15_BIAS_SURFACED record=R2 weight=0.31 ageHours=72.1 tier=Heavy valence=+0.61
V15_DIVERGENCE mood=Wistful,Tenderness vibe=Playfulness,Delight actual=Wistful,Tenderness
V15_DIVERSITY_HISTOGRAM record=R1 surface_count=7 record=R2 surface_count=3 ...
```

**Acceptance:** unit tests cover (a) importance computation across the three tiers, (b) bias-weight decay across the three half-lives, (c) MMR diversity rerank produces fewer records when input is high-similarity cluster, (d) self-regulation framing — outcome valence sourced from Ani-register delta, NOT Mark-register delta. ~25 spec tests.

**Outcome:** 35 spec tests landed (more than the original ~25 estimate; spread across pure-helpers, end-to-end via `ComputeBiasAsync` with strict-mock `IClosedConversationStore`, and architectural-invariant tests pinning the self-regulation framing). 959/959 total tests passing. Build green, zero warnings. The architectural-invariant test `Contribution_OutcomeValence_SourcedFromAniDeltaNotMarkDelta` is the load-bearing pin — it verifies that two records with **identical Ani-delta valence but radically different Mark register vectors** produce identical contribution `OutcomeValence`, confirming the bias is invariant to Mark's pre-state. Files: [`VibeBiasContribution.cs`](../../src/AniRuntime.Core/Models/VibeBiasContribution.cs), [`VibeBiasResult.cs`](../../src/AniRuntime.Core/Models/VibeBiasResult.cs), [`MarkRegisterContext.cs`](../../src/AniRuntime.Core/Models/MarkRegisterContext.cs), [`IVibeBiasService.cs`](../../src/AniRuntime.Core/Interfaces/IVibeBiasService.cs), [`VibeBiasService.cs`](../../src/AniRuntime.Loops/VibeBiasService.cs), [`AniOptions.cs`](../../src/AniRuntime.Core/AniOptions.cs) (V1.5 tier thresholds + similarity threshold + MMR lambda + lookback days), [`Program.cs`](../../src/AniRuntime.Service/Program.cs) (DI registration).

---

### Phase V1.5a — Observational instrumentation (bias logged, NOT applied) ✅
**Status:** Shipped May 2, 2026 (~30 min code + 9 spec tests).
**Estimated effort:** ~half-day to wire telemetry; 2-week observation window.

V1.5.1 service is wired into `OutreachPhase` and `ConversationReplyPhase` BUT the result is logged only — the prompt does NOT consume `RecommendedStrategyRegister` or `SurfacedTopN`. The composition prompt remains unchanged from current V1.4 form.

Per outreach + per reply, log:
- `V15_BIAS_CANDIDATES` — count + tier breakdown across the eligible candidate set
- `V15_BIAS_SURFACED` — per-record (one log line per surfaced record): record id, weight, tier, age, valence
- `V15_BIAS_RECOMMENDED` — top register name from `RecommendedStrategyRegister`, top value, surfaced count, diversity reason
- `V15_BIAS_SKIP` (debug-level) when no recent closed conversation OR empty register dict — surfaces the substrate-volume signal even when bias can't run
- `V15_BIAS_FAILURE` (warning-level, never propagates) when the bias computation throws — observational telemetry is best-effort and must not affect dispatch

**Pragmatic deferral on the divergence triple:** the original plan called for logging the full triple `(mood_register, vibe_recommended_strategy_register, response_register_actual)` per outreach. V1.5a-as-shipped logs only the bias-side at composition time. The post-hoc `response_register_actual` is recoverable from the **next `ClosedConversationRecord.AniRegister`** that includes the outreach as one of its turns (V1.2's per-speaker classification already runs at thread close). Dashboard joins by `thread_id` to render the divergence triple offline. This avoids adding an Ollama round-trip per outreach for observational-only telemetry — the wrong trade for V1.5a.

Dashboard rendering (Theme I.0 deliverable):
- Diversity histogram: surface count per record, rolling 7-day + 30-day windows. Source: `V15_BIAS_SURFACED` rolled up by `record` field.
- Mood-vs-vibe-vs-actual divergence triple: join `V15_BIAS_RECOMMENDED.top_register` (composition time, by `thread_id`) with `closed_conversation_records.ani_register` (post-thread-close, dominant register slot) for the actual-vs-recommended comparison.

**Acceptance:** ≥10 closed conversations in substrate; ≥10 outreaches logged with full V1.5a telemetry; dashboard renders both views; diversity histogram is fat-tailed (no record >40%); pre-bias correlation baseline computed.

**Mark + Claude review session:** before V1.5b ships, sit together with the data. If the histogram shows pattern-lock risk (single-record dominance) or if `response_register_actual` already strongly tracks `mood_register` with the recommended-strategy register diverging widely, retune Lever 1 / Lever 2 / Lever 3 parameters before activation rather than ship a known-flattening loop.

**Outcome:** `VibeBiasObservation.ObserveAsync` helper landed (best-effort, never throws); `IVibeBiasService` injected as optional dependency into both `OutreachPhase` and `ConversationReplyPhase`; observation call placed immediately before each composition LLM call. 9 spec tests pin the contract: short-circuits on null service / no recent closed conversation / empty register dict, never propagates exceptions, passes a 9-dim ordered Mark register vector with `AsOf=UtcNow`, falls back to "Mark" contact name when snapshot field is empty. Files: [`VibeBiasObservation.cs`](../../src/AniRuntime.Loops/VibeBiasObservation.cs), [`OutreachPhase.cs`](../../src/AniRuntime.Loops/OutreachPhase.cs) (+5 lines), [`ConversationReplyPhase.cs`](../../src/AniRuntime.Loops/ConversationReplyPhase.cs) (+5 lines).

---

### Phase V1.5b — Prompt-bias activation
**Estimated effort:** ~1 day code + spec tests; gated on V1.5a observation window.

Gate criteria (all must hold):
1. V1.5a substrate accumulation complete (≥10 closed conversations)
2. Diversity histogram fat-tailed (no record >40% surface rate)
3. Pre-bias correlation baseline shows model isn't already doing this naturally to a degree that makes V1.5 redundant
4. Mark + Claude joint review approves activation

Changes:
- `OutreachPhase` composition path: insert the `Prior shapes that landed well` block above current mood block in `BuildOutreachCompositionPrompt`
- `ConversationReplyPhase`: same insertion pattern in reply composition prompt builder
- Spec tests: prompt-shape regression, anti-parrot still holds (gist anti-parrot constraint from V1.2 carries forward into V1.5b prompt rendering — gists are already paraphrased; V1.5b doesn't loosen that)

Post-activation observation window: 2 weeks, measure
- Divergence triple shape: is `response_register_actual` blending `mood_register` and `vibe_recommended_strategy_register`, or is one dominating?
- Mark's qualitative felt-experience: tag any conversation that feels formulaic / pattern-locked
- Diversity histogram shape change: does activation flatten the histogram (more records surface) or sharpen it (winners take more)?

**Acceptance:** behavioral effect measurable in divergence telemetry; no qualitative regression in Mark's felt-experience; diversity histogram remains fat-tailed.

---

## Measurement plan

| Metric | Phase introduced | Target |
|--------|------------------|--------|
| `bias_weight` distribution across tiers | V1.5.1 | Light/Medium/Heavy each >5% of surfacings (otherwise tier criteria need tuning) |
| Diversity histogram tail (top-1 surface rate) | V1.5a | < 40% in 30-day window |
| Pre-bias correlation: `corr(actual_register, mood_register)` | V1.5a | Baseline measured; expectation ~0.5–0.7 |
| Pre-bias correlation: `corr(actual_register, recommended_strategy_register)` | V1.5a | Baseline measured; expectation < pre-bias model-mood correlation (otherwise V1.5b is redundant) |
| Post-V1.5b `response_register_actual` blend | V1.5b | Correlates with both `mood_register` AND `vibe_recommended_strategy_register` within tolerance — neither dominates |
| Mark felt-experience tags of "formulaic" / "pattern-locked" outreaches | V1.5b | Zero across the 2-week post-activation window |

## Research integration

| Where | What |
|-------|------|
| `ANI-Phase-Tracker.md` Vibe Loop V1 row | Updates to point at this V1.5 plan when V1.5.1 ships |
| `ANI-Research-References.md` | Add Schuller et al. + Chu et al. as related work for the self-regulation framing (existing references; V1.5 cites them in Paper 3) |
| `ANI-Research-Log.md` | Entry per phase deploy; final synthesis at V1.5b close |
| **Paper 3** | Self-regulation framing as a contribution candidate. The asymmetric outcome-signal computation (Ani's delta drives bias, Mark's delta is telemetry only) is the load-bearing architectural bet worth naming. |
| **Paper 2** | Cite Chu et al. 2025 register-similarity data; ANI's V1.5 telemetry produces finer-grained register-vector outcome deltas at per-thread granularity. |

## Risks

1. **Substrate too sparse to bias meaningfully.** ≥10 closed conversations is the floor; if substrate accumulation is slow (Mark's conversation cadence varies), V1.5b activation could slip weeks. Mitigation: V1.5a's observation window is open-ended — we wait until the data is there.

2. **Pattern-lock at V1.5b activation despite Lever 1.** If the importance-tier breakdown is wrong (e.g., too many Heavy records of one shape), saturation doesn't prevent dominance. Mitigation: V1.5a telemetry catches this BEFORE activation. If detected, retune tier criteria or saturation half-lives.

3. **Trust-the-model bet wrong at V1.5b.** Lever 2 trusts v7 to integrate gists + mood without behavior-coaching instruction. If post-activation Ani lifts gist phrases verbatim or ignores mood entirely, the architecture-over-model principle has failed at this surface. Mitigation: spec test V1.5b parrot-check (gists are already paraphrased per V1.2; verbatim lift would have to come from prompt-level model behavior); if failure observed, the right response is NOT to add prompt-level coaching but to investigate whether V1.5b prompt structure is ambiguous to the model (e.g., section ordering, register naming).

4. **Door B temporal-anchor gap widens.** V1.5b surfaces gists with temporal context; until Door B truth-verification ships, the temporal-anchor failure class (May 2 gap-watch) has more surface. Mitigation: track recurrences of date-confab class during V1.5b observation window. If recurrences increase materially over baseline, escalate Door B priority.

## Effort estimate

| Phase | Effort | Calendar |
|-------|--------|----------|
| V1.5.0 | Done | Locked May 2 |
| V1.5.1 | 1.5 days | 1 day code, 0.5 day spec tests |
| V1.5a | 0.5 day code + 2 weeks observation | Calendar gated by substrate accumulation |
| V1.5b | 1 day code + 2 weeks observation | After V1.5a gate met |

Total: ~3 days code + 4 weeks observation calendar. Code work modest; observation cadence drives the schedule.

## Status Log

| Date | Note |
|------|------|
| 2026-05-02 11:24 CDT | Plan drafted by Claude. V1.5.0 decisions locked from May 2 11:00–11:24 design conversation. Mark resolved the four open questions from `ANI-VibeLoop-V1.5-Vibe-vs-Mood-Balance-Design-Questions.md`. Next: Mark green-lights V1.5.1 implementation. |
