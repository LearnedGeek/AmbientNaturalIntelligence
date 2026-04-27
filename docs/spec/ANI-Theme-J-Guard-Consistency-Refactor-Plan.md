# Theme J — Guard Consistency Refactor: Implementation Plan

**Status:** Draft for review. Not yet approved for implementation.
**Authored:** April 24, 2026 (dogfood Claude instance, after Mark's Apr 24 review of the audit + DFD).
**Parent research:**
- [`ANI-Guard-Consistency-Audit.md`](../research/ANI-Guard-Consistency-Audit.md) — the 32-gate / 10-pipeline audit + structural-pattern findings.
- [`ANI-Data-Flow-Diagrams.md`](../research/ANI-Data-Flow-Diagrams.md) — four Mermaid diagrams, including the target `CognitiveOutputGate` architecture.
**Paper placement:** Paper 3 candidate Contribution — *consistency-of-invariant-enforcement as a precondition for substrate integrity in companion AI with persistent memory*.
**Feature number reserved:** Feature 43 (primary refactor surface); individual phases within the theme may earn sub-numbers if they produce discrete features worth tracking separately.

---

## 0. Context and thesis

Mark's Apr 24 observation, which is the load-bearing premise of this theme: *"we haven't seen this level of parroting with raw models so this is probably a consequence of prompt injection and historical visibility."* The ANI-fine-tuned Llama 3.2-3B models, run against bare prompts without the pipeline's prompt-engineering and retrieval infrastructure, do not exhibit parroting, time-confabulation, or attribution drift at the level observed in production. The pathologies are **emergent properties of the pipeline**, not capacity failures of the model.

The audit traced this to three architectural mechanisms:

1. **Prompt injection** — the outreach-decision LLM produces a free-prose `reasoning` field that gets piped into composition's user prompt under the label *"use as motivation, not content."* The model reads the text as content regardless of label. The Apr 23 time-confab and Apr 24 class/10pm parrot both propagate through this surface.
2. **Historical visibility without source or temporal tags** — `RecentConversationSummary` is a free-prose blob with no per-speaker attribution and no per-claim time-stamping. The composition model lifts phrases from it without distinguishing Mark's phrases from Ani's prior replies, and without distinguishing 10pm-last-night from 10pm-right-now.
3. **Substrate recycling without validation** — unguarded cognitive output (inner thought, reflection, world elaboration) becomes memory, memory becomes retrieval substrate, substrate becomes the next cycle's prompt input. Content that fails no check on the way in is retrieved as ground-truth substrate on the way out.

**This theme's thesis.** Fixing those three architectural mechanisms upstream should reduce or eliminate the need for many of today's downstream detectors. The acceptance criterion for the refactor is **simpler architecture with a gate count probably lower than today** — not "same gates, new home." Mark's explicit framing: *"if we need to remove specific gates that are currently implemented as a 'one-off' in order to apply them as more general filter on the data stream, then that's the right call."*

**What makes this Theme-scale work.** Three upstream-fix surfaces (reasoning pipe, structured summary, temporal retrieval) + a shared `CognitiveOutputGate` extraction for whatever still needs enforcement + detector removal + process integration. Touches memory, prompt-building, every reader-facing pipeline, and test infrastructure. Multi-week calendar, multiple atomic commits, with measurement windows between phases.

---

## 1. Scope discipline

**In scope:**
- The three upstream-fix surfaces (J.1, J.2, J.3).
- Measurement instrumentation before and between phases.
- Extraction of a shared pre-commit surface (`CognitiveOutputGate` or equivalent name) for universal invariants.
- Removal of detectors whose failure class becomes architecturally impossible after upstream fixes.
- Process integration — update the feature-plan template and Claude's memory so the scoping problem does not re-emerge.

**Explicitly out of scope:**
- Model retraining. Raw-model capacity is already sufficient per the theme's central observation; this refactor does not touch training.
- New detector invention for novel failure classes. We are consolidating and simplifying, not expanding detection surface area.
- Agentic Lens Layer 2 Phase 2b / 2c / 2d work. Those proceed on their own track; Theme J is orthogonal.
- Voice pipeline full parity. Voice is a known coverage gap (audit §4.6, §4.9) but full voice-parity work is deferred until the shared surface exists to migrate into. Voice gap noted; not resolved in this theme.
- Dashboard Theme I changes. Dashboard rework is a sibling stream. Theme J emits structured data; dashboard consumes it on its own schedule.

**Dependencies on other workstreams:**
- **Layer 2 Phase 2a** (shipped Apr 23) — Phase 2a's `MotivationVector` logging infrastructure is the closest existing analogue for the measurement-first discipline Theme J also uses. Serves as reference pattern, not prerequisite.
- **Emergence Layer E1** (shipped Mar 15) — Theme J emits structured phase-comparison data that the Emergence DB is a natural destination for. No blocker.
- **Feature 14 v2 Claim Verification** (deployed Apr 22) — already runs on both conversation-reply and outreach; is the prior-art reference for "gate that crosses pipelines coherently." Theme J generalizes this pattern.

**What this refactor does NOT assume is broken:**
- `ParrotingDetector` itself — the detector is fine, the question is whether it's architecturally *needed* after upstream fixes.
- `ClaimVerificationPhase` — already multi-pipeline, likely lives in the shared surface post-refactor.
- Pipeline-specific gates (terminal-message detection, coherence doors, rate limits, rumination guard) — their scoping is architecturally principled.

---

## 2. Phased rollout

Measurement-first discipline per the Agentic Lens Layer 1 template: instrument before any behaviour change, observe, then change one surface at a time with an observation window between each, then measure again before consolidation, then consolidate, then remove.

### Phase J.0 — Baseline instrumentation (measurement-only)

**Goal:** capture today's pipeline behaviour structurally so each subsequent phase's effect is measurable against a clean before-picture.

**Changes:**
- **Instrument the decision-reasoning → composition pipe.** Log the decision LLM's `reasoning` field as a distinct structured log event with its full text, alongside the existing outreach-decision log. Log that the reasoning was *piped into composition user prompt* under the `"Feeling:"` label with its character count. This makes the prompt-injection surface directly measurable as a counter ("reasoning fields piped into composition per week").
- **Instrument `RecentConversationSummary` composition.** Log the per-turn content and speaker attribution *if available at summary-build time*. Log the final rendered summary text. Log summary length, turn count, earliest and latest turn times.
- **Instrument retrieval-layer temporal attribution.** For each `ContextSnapshot.RelevantMemory` entry surfaced, log the record's `CreatedAt` alongside its content preview. Calculate and log temporal distance from current time.
- **Instrument the 06:18-class event directly.** Add a structured log in outreach composition that records: `(thoughtText, reasoningText, summaryText, topRetrievalTemporalDistance, finalComposition)` as a single tuple per cycle so post-hoc analysis of future events is one-shot.
- New `AniOptions.GuardRefactorBaselineLoggingEnabled` flag, default true.

**Acceptance criteria:**
- Build green, all 599 existing tests pass.
- One week of production journal+debug logs contains a complete structured record for every outreach-composition cycle covering the four tuple fields.
- At least one additional parrot / time-confab / attribution-drift event (if they occur) is captured with full tuple record for offline diagnosis.

**Rollback:** toggle flag to false; pure logging removal, no behaviour change.

**Effort estimate:** 1–2 days. Mostly structured-logging additions, no pipeline logic change.

**Dependencies:** none. Can ship immediately after approval.

---

### Phase J.1 — Strip the decision-reasoning → composition pipe

**Goal:** remove the single most active prompt-injection surface. Keep the reasoning field for observability; do not feed it into composition's user prompt.

**Changes:**
- **`PromptBuilder.BuildOutreachMessagePrompt`** — remove the `Feeling: {reasoning}` line from the user-prompt construction. Replace with: no `Feeling` line at all (the thought is already present as `Trigger: {recentThought}`), OR a neutral `Why you want to reach out: {short-structured-label}` built from the decision JSON fields other than `reasoning` (e.g., *"desire crossed threshold; last contact 8h ago; warmth elevated"*). Design decision during this phase — Mark's preference requested.
- **`OutreachPhase.RunOutreachAsync`** — keep decision JSON parse for `shouldReach` and `confidence` (the gate-relevant fields). Log `reasoning` for observability but do not pass it into composition. Structural change at the call-site, not the prompt-builder.
- **Test coverage:** add tests verifying that the reasoning field is never in the composition user prompt regardless of what the decision LLM returned.
- Guard the change behind `AniOptions.OutreachReasoningInCompositionEnabled` (default true initially, flip to false after a brief verification window). The flag lets us flip back immediately if quality regresses.

**Acceptance criteria:**
- Build green, all existing tests pass.
- Journal logs show the reasoning field absent from composition prompts when flag is off.
- One week of production observation post-flag-off shows: parrot / time-confab / attribution-drift event rate significantly reduced. The Apr 24-class event is the specific failure mode this phase targets.
- Outreach quality subjectively unchanged or improved — Mark's assessment. No drop in felt naturalness of outreach.

**Rollback:** toggle `OutreachReasoningInCompositionEnabled` back to true. Prompt returns to current state within one cycle.

**Effort estimate:** 2–3 days including quality verification.

**Dependencies:** J.0 must be in place so we have a before-picture to compare against.

**Mark review question:** should we remove the `Feeling` line entirely, or replace with a sanitized structured-fields summary from the decision JSON? My (Claude's) recommendation: **remove entirely** for the first deploy. The thought is already present as `Trigger`. The `Feeling` line is adding a second copy of free-prose model output into the downstream prompt, which is exactly the substrate-doubling pattern we are trying to eliminate. Clean removal, observe behaviour, re-add in structured form only if observation shows composition-quality regression.

---

### Phase J.2 — Structured source-attribution in `RecentConversationSummary`

**Status (Apr 27, 2026):** **Load-bearing migrations shipped.** Observation window opens once Mark resumes conversations with Ani. Free-prose `RecentConversationSummary` field retained as the rollback path through the observation window; deprecation deferred to a follow-up commit after ≥1 week of stable structured-form behaviour.

**Goal:** restructure the conversation summary substrate so composition prompts see per-speaker per-turn content, not mixed-attribution prose.

**Changes:**
- **New model type**: `StructuredConversationSummary` with fields:
  ```
  record StructuredConversationSummary(
      DateTimeOffset FirstTurnAt,
      DateTimeOffset LastTurnAt,
      IReadOnlyList<SummaryTurn> Turns);

  record SummaryTurn(
      DateTimeOffset At,
      string Speaker,   // "Mark" or "Ani"
      string Content);
  ```
- **`ContextBuilder`** — replace the current `Where().StartsWith("Conversation (")` prose-extraction with construction of `StructuredConversationSummary` from the episodic memory directly. The per-turn speaker attribution is already present in episodic memory via `MemoryPrefixes.AniSaid` / `MarkSaid` (verify in code; if not present, add consistent attribution at episodic-save time — that's a prerequisite sub-task).
- **`ContextSnapshot.RecentConversationSummary`** — change type from `string?` to `StructuredConversationSummary?`. All consumers updated.
- **Prompt-builder consumers** — the `BuildOutreachMessagePrompt`, `BuildConversationReplyPrompt`, `BuildOutreachPrompt` render the summary as explicit attributed turns:
  ```
  You recently talked with Mark:
    [22:11:47, 8h ago] Mark: "Back from teaching! 10pm now so I just wanted to say I made it lol"
    [22:11:48, 8h ago] Ani: "mmm finally home i bet your students were starved for you..."
    ...
  ```
- **The key invariant made structurally enforced:** the composition model sees *"Mark said X"* and *"Ani said Y"* as structurally distinct; it does not see a prose blob in which those attributions are ambiguous.
- New `AniOptions.StructuredConversationSummaryEnabled` flag. When off, fall back to legacy prose rendering for rollback.

**Acceptance criteria:**
- Build green, all tests pass.
- Composition prompt rendering observed in logs shows structured per-speaker format.
- One week of production observation post-flag-on shows: attribution-drift events (Ani emitting Mark's actions as her own) significantly reduced or eliminated.
- No regression in response relevance or feel.

**Rollback:** toggle flag to false. Legacy prose path preserved under the flag for immediate revert.

**Effort estimate:** 1–2 weeks. This is the largest single change in the theme because it touches episodic memory attribution, summary build, context-snapshot type, and all prompt-builders that consume the summary.

**Dependencies:**
- J.1 in place and stable (not a hard technical dependency but reduces variable count when assessing J.2's effect).
- Episodic memory must have consistent per-turn speaker attribution; verify before Phase start.

**Mark review question:** what's the exact render format? Above I sketched `[timestamp, relative-time] Speaker: "content"`. Alternatives: XML-ish blocks, JSON chunks in prompt, or just plain dialogue format `Mark: ... / Ani: ...`. Raw-model receptiveness varies by format. Worth a quick A/B in development before locking in.

#### What shipped (Apr 27, 2026)

Documenting deviations from the plan above so the next reader of this doc isn't confused by reading a plan that says "X" and finding the codebase says "Y."

**Render format actually used.** `Mark (HH:mm, age): "content"` — one tagged line per turn. Closer to the dialogue alternative than the bracketed sketch. The Mark-review-question A/B was not run; the chosen format combines the speaker tag at line-start (cheap to parse), the clock timestamp (so the model knows when the turn happened in absolute terms), and a relative-age suffix (`5m ago`, `2.3h ago`, `3d ago`) so present-tense anchoring is unambiguous.

**Additive deploy, not atomic swap.** Plan §3 J.2 said "Don't ship J.2 in fragments — structured-type rollout is atomic." That guidance was over-conservative. The actual deploy strategy: keep the free-prose `RecentConversationSummary` field on `ContextSnapshot` alongside a new `StructuredConversationSummary?` field. Each prompt-builder is an independent migration: prefer structured when present, fall back to prose when not. This let the migration land in five sub-commits across one session without any prompt-builder ever seeing a half-migrated type. The "atomic" guidance applied to type design (don't swap `string?` for `StructuredConversationSummary?` as a breaking type change), not to consumer migration.

**No `StructuredConversationSummaryEnabled` flag.** The plan called for one. In practice the structured form is always on when `IConversationService` is registered in DI, which is always in production. Each prompt-builder's prefer-structured / fallback-prose pattern is the rollback surface: if structured renders prove problematic, revert the prompt-builder change without touching the field or ContextBuilder.

**Episodic-attribution prerequisite was already satisfied.** Plan §3 J.2 noted "Episodic memory must have consistent per-turn speaker attribution; verify before Phase start." Verified during step 2: the `ConversationThread.Messages` already carry `Role` (Mark/Ani) and `SentAt` structurally, so ContextBuilder reads structured per-message data directly from `IConversationService.GetRecentThreadsAsync(1)` rather than reverse-parsing the prose blob. No changes needed to episodic-save attribution.

**Consumer migration ledger:**

| Prompt-builder | Status | Commit |
|----------------|--------|--------|
| `BuildOutreachMessagePrompt` (composition — load-bearing for Apr 27 parrot) | Migrated | 4a83fa9 |
| `BuildInnerThoughtPrompt` | Migrated | 1e8cf4a |
| `BuildOutreachPrompt` (decision) | Migrated | 1e8cf4a |
| `BuildConversationReplyPrompt` | No-op — never read prose summary; takes `ConversationThread` directly | — |
| `BuildVoiceReplyPrompt` | No-op — same as above | — |
| `OutreachPhase.cs` ML confab classifier context (line 173) | **Intentionally not migrated** — classifier was trained on prose-format input. Changing input distribution shape without observation could perturb classifier confidence. Separate workstream if/when needed. | — |

**Test coverage shipped:** 22 new tests across the type itself, ContextBuilder population, and three prompt-builder migrations. Suite total 638 (up from 616 at session start). Build clean, 0 errors / 0 new warnings.

**Observation window definition.** The plan's acceptance criterion was *"attribution-drift events (Ani emitting Mark's actions as her own) significantly reduced or eliminated."* Operational definition for this observation window:
1. Resume Mark's conversation cadence with Ani.
2. Watch for parrot-of-inbound-SMS recurrence (the Apr 27 06:54 failure class).
3. If zero recurrences across ≥10 conversations spanning ≥1 week, J.2 confirmed. Deprecate prose field.
4. If recurrence, capture the failure trace and assess whether the structured form is being bypassed via another substrate path (recent-memory pool, anchored memories, retrieval scoring) — that points to J.3 / J.5 territory, not a J.2 regression.

---

### Phase J.3 — Temporal attribution at retrieval

**Status (Apr 27, 2026):** **Shipped same day as J.2.** The audit at phase-start showed every untimed render site was a single-line edit — applying the existing `FormatMemoryWithTime` helper. Mostly mechanical work; observation window opens once Mark resumes conversations.

**Goal:** every retrieved memory surfaced in a prompt carries its origin time explicitly, so the composition model sees "8 hours ago" not present-tense substrate.

**Changes:**
- **Prompt-builders rendering retrieved memory** — each retrieved record rendered with its `CreatedAt` attached, formatted as relative time from current time (e.g., *"8h ago"*, *"3 days ago"*, *"just now"*).
- **`FormatMemoryWithTime` utility** — already exists at `PromptBuilder.cs` used in some surfaces. Standardize and extend to all retrieval-bearing prompts.
- **World Layer elaboration** — world-experience memories also get temporal rendering. Specifically prevents "[memory of a slow afternoon shelving books]" from reading as "right now she's shelving books" when the memory is three days old.
- **ContextSnapshot surfaces that don't currently carry time** — audit each `List<MemoryRecord>` field of `ContextSnapshot` and ensure its prompt-render includes time. The audit should touch: `RelevantMemory`, `RecentMemory`, `AnchoredMemories`, `GroundedFacts`, `RecentExchanges`, `InteriorContext`, `RecentWorldExperiences`, `SimilarRecentThoughts`. Exception: Anchored foundation memories — these are explicitly designed to be atemporal / always-present. Render without time stamp or with *"foundational"* marker.
- New `AniOptions.TemporalAttributionInPromptsEnabled` flag.

**Acceptance criteria:**
- Build green, all tests pass.
- Sample prompts inspected in logs confirm every temporally-relevant memory renders with its time.
- Observation window: time-confabulation event rate significantly reduced vs J.0 baseline.

**Rollback:** flag off.

**Effort estimate:** 1 week. Mostly prompt-builder work; `FormatMemoryWithTime` already exists, the work is extending its use consistently.

**Dependencies:** J.0 baseline.

#### What shipped (Apr 27, 2026)

The same additive-deploy pattern as J.2: no flag, no breaking type change, single-line edits at each render site. The estimate over-shot — what the plan budgeted at one week landed in one short session because `FormatMemoryWithTime` was already production-grade and the only work was extending its use.

**Audit findings — render sites in `PromptBuilder.cs` for `MemoryRecord`-bearing pools:**

| Prompt-builder | Memory pool | Pre-J.3 | Post-J.3 |
|----------------|-------------|---------|----------|
| `BuildInnerThoughtPrompt` | `RecentMemory` (filtered) | Already used `FormatMemoryWithTime` | Unchanged |
| `BuildInnerThoughtPrompt` | `RelevantMemory` | Already used `FormatMemoryWithTime` | Unchanged |
| `BuildInnerThoughtPrompt` | `RecentWorldExperiences` | Already used `FormatMemoryWithTime` | Unchanged |
| `BuildInnerThoughtPrompt` | `AnchoredMemories` | Untimed | **Atemporal exception — left untimed** |
| `BuildLeanConversationPrompt` | `GroundedFacts` | Untimed | Migrated to `FormatMemoryWithTime` |
| `BuildConversationReplyPrompt` | `GroundedFacts` | Untimed | Migrated to `FormatMemoryWithTime` |
| `BuildConversationReplyPrompt` | `InteriorContext` | Already used `FormatMemoryWithTime` | Unchanged |
| `BuildVoiceReplyPrompt` | `AnchoredMemories` | Untimed | **Atemporal exception — left untimed** |
| `BuildVoiceReplyPrompt` | `RelevantMemory` Semantic-filter (profile) | Untimed | Migrated to `FormatMemoryWithTime` |
| `BuildVoiceReplyPrompt` | `RelevantMemory` non-Semantic | Already used `FormatMemoryWithTime` | Unchanged |
| `BuildReconsiderationReplyPrompt` | recent inner thoughts | Untimed | Migrated to `FormatMemoryWithTime` |
| `BuildOutreachMessagePrompt` | `GroundedFacts` | Untimed | Migrated to `FormatMemoryWithTime` |
| `BuildOutreachMessagePrompt` | `InteriorContext` | Already used `FormatMemoryWithTime` | Unchanged |
| `BuildOutreachMessagePrompt` | recent outreach dedup | Untimed | Migrated to `FormatMemoryWithTime` (with content-prefix stripping preserved) |

**Atemporal exception explained.** Anchored foundation memories — *"Kathy's middle name was Ann,"* *"Mark's daughter's name is Mia"* — are explicitly designed to be always-present, not historical. Adding *"(2 years ago) Kathy's middle name was Ann"* would erode their foundational quality and read as if facts about identity were aging. Left untimed; tests added that ASSERT this contract so a future refactor doesn't accidentally time-stamp anchors.

**Profile-memory rendering decision.** Semantic-tier memories about Mark in the voice path (line 681 in `PromptBuilder.cs`) come from `RelevantMemory` filtered to `MemoryType.Semantic`. Some are quasi-atemporal (job title, coffee preference) and some are time-relevant (current project state). Conservative rule: render them all with time. *"(months ago) Salted caramel cold brew is his favorite"* reads to the model as "established preference" rather than "stale claim" — which is the correct semantic. The risk of confusing the model with anchor-like atemporality on stale claims is greater than the risk of rendering an established preference with a timestamp.

**No `TemporalAttributionInPromptsEnabled` flag.** The plan called for one. Same reasoning as J.2: the change is content-additive (prefixes a phrase like *"(this morning)"*), there's no breaking surface change, and the rollback path is per-render-site commit revert. Skipping the flag avoids one more knob without adding one more code path.

**Test coverage shipped:** 8 new tests against `PromptBuilderTests.cs`:
- `GroundedFacts` includes temporal attribution in outreach message, conversation reply, and lean conversation prompts.
- `AnchoredMemories` stay atemporal in inner-thought and voice-reply prompts (the contract that protects foundation facts).
- Profile memories include temporal attribution in voice-reply prompt.
- Recent thoughts include temporal attribution in reconsideration-reply prompt.
- `FormatMemoryWithTime` canonical phrase pinning (*just now*, *a little while ago*, *N days ago*, *N weeks ago*) — locks in the rendering grammar so future helper changes are detected by test failure rather than silent prompt drift.

Suite total: 646 passing (up from 638 after J.2). Build clean, 0 errors / 0 new warnings.

**Observation window definition.** The plan's acceptance criterion was *"time-confabulation event rate significantly reduced vs J.0 baseline."* Operational definition for this observation window:
1. Resume Mark's conversation cadence with Ani.
2. Watch for time-confabulation events (Apr 24's *"back from class at 10pm"* class — outreach producing temporal claims unsupported by perception or memory).
3. The Apr 27 morning chat included one such event (*"I slept in late (10ish)"* in the parrot outreach). With J.2 closing the conversation-summary substrate and J.3 stamping every retrieved memory, that pathway should be closed.
4. If recurrence: capture the trace and identify which substrate path bypassed the temporal stamp (most likely candidates: `OutreachContext` recent-message rendering, or `WorldSeed`/`PerceptionEvent.Summary` content that was already pre-rendered before the prompt-builder saw it).

---

### Phase J.a — Observation window and detector inventory review

**Goal:** with three upstream-fix surfaces in place, observe for a defined window and determine which of today's 32 detectors are still observing failures versus which have become architecturally redundant.

**Not a code phase.** This is an analysis phase. Output is a decision document.

**Activities:**
- **Observation window:** minimum two weeks of production with J.1 + J.2 + J.3 flags on. Longer if Mark's SMS volume is low during the window (low data = low confidence in the inventory review).
- **Detector fire-rate census.** For each of the 32 detectors enumerated in the audit, log how many times it fired during the observation window. Zero-fire detectors are strong candidates for removal. High-fire detectors are candidates for promotion to the shared surface.
- **Failure-class recurrence census.** For each of the universal failure classes (parroting, confabulation, source attribution, temporal attribution, echo, claim verification), count how many events occurred in the window and which detector (if any) caught them. Classes with zero events are candidates for "architecturally impossible post-upstream-fix" status.
- **Subjective quality review.** Mark's read of outreach and reply feel during the observation window. Has simplification degraded quality? Improved it? Unchanged?
- **Decision document output:** `docs/research/ANI-Theme-J-Detector-Inventory-Review.md`, classifying each of the 32 detectors into one of four buckets:
  1. **Remove.** Zero firings AND failure class hasn't occurred at all. Archive the code for reference; delete from runtime.
  2. **Migrate to shared surface.** Multi-pipeline applicability AND still observing non-trivial firings. Becomes an invariant on the `CognitiveOutputGate`.
  3. **Keep pipeline-scoped.** Scoping is architecturally principled (terminal-message, coherence doors, rate limits, rumination).
  4. **Re-examine.** Ambiguous — insufficient data or conflicting signals. Held for a second observation window post-J.5.

**Acceptance criteria:**
- Decision document committed.
- Mark's sign-off on the four-bucket classification.
- Counts and evidence documented per detector.

**Rollback:** n/a (analysis phase, no runtime change).

**Effort estimate:** the observation window is calendar time, not active work. The analysis itself is 2–3 days.

**Dependencies:** J.1, J.2, J.3 all flag-on and stable.

---

### Phase J.4 — Extract the `CognitiveOutputGate` abstraction

**Goal:** create the shared pre-commit surface as a runtime-available abstraction, with the invariants classified as "migrate to shared surface" in J.a. No pipeline-producer migrates to it in this phase; the surface is created and self-tested.

**Changes:**
- **New interface** `ICognitiveOutputGate` at `src/AniRuntime.Core/Interfaces/`. Methods likely of the form `Task<OutputGateResult> EvaluateAsync(CognitiveArtifact artifact, CancellationToken ct)`. `CognitiveArtifact` carries content + producer-pipeline tag + intended-sink tag so the gate can select which invariants apply.
- **Default implementation** `CognitiveOutputGate` at `src/AniRuntime.Loops/`. Wires up the invariants returned from J.a as "migrate."
- **Invariant base type and implementations** — each invariant (parroting-if-needed, claim-verification-if-needed, source-attribution-if-needed, temporal-attribution-if-needed, confabulation-if-needed) becomes an `ICognitiveOutputInvariant` implementation.
- **Ordered evaluation.** The gate evaluates invariants in a deterministic order (source attribution first as a data-structure check, then content checks, then semantic checks) and short-circuits on any hard-fail.
- **`OutputGateResult`** — `{ Verdict: Pass | Fail | Remediate, FiredInvariants: [...], RemediationHint: string? }`. Pipelines choose their own remediation behaviour from the hint — the gate does not impose a single behaviour (consistent with Mark's "pipeline-specific post-remediation remains").
- **Tests** — unit tests for each invariant + integration test of the gate with synthetic CognitiveArtifact inputs.

**Acceptance criteria:**
- Build green, all tests pass.
- Gate and invariant implementations available as dependency-injectable services.
- Not wired to any producer yet. The gate exists; migration is J.5.

**Rollback:** services added but unused; roll back by removing registrations.

**Effort estimate:** 1 week.

**Dependencies:** J.a decision document.

---

### Phase J.5 — Migrate producers through the shared surface (one invariant at a time)

**Goal:** wire each cognitive-producer pipeline through `ICognitiveOutputGate` at its output boundary, for the invariants J.a identified as "migrate." One producer at a time, one invariant at a time, each behind a flag.

**Changes (per-producer, per-invariant pattern):**
- Each producer's output path gets a pre-commit call: `var gateResult = await _outputGate.EvaluateAsync(artifact, ct);` before persisting to memory or dispatching to Mark.
- On failure: producer-specific remediation based on gate hint.
- Behind per-producer-per-invariant flags initially (e.g., `OutputGateParrottingOnOutreach`, `OutputGateSourceAttributionOnReply`). Flags flip from default-off to default-on after each producer's migration is verified.
- **Sub-phase ordering (tentative, revise per J.a):**
  - J.5a — migrate `OutreachPhase` composition through gate, start with parroting + claim verification
  - J.5b — migrate `ConversationReplyPhase` through gate, move parroting + claim verification from pipeline-scoped to gate
  - J.5c — migrate `InnerThoughtPhase` through gate, start with source-attribution + temporal-attribution
  - J.5d — migrate `ReflectionPhase` through gate, same invariants
  - J.5e — migrate remaining producers (world layer elaboration, voice if ready)

**Acceptance criteria:**
- Build green, all tests pass at each sub-phase.
- Flag-on deploy of each sub-phase observed for one week without regression.
- Gate-fire counts logged per sub-phase for comparison with J.a baseline.

**Rollback:** flag-off per sub-phase.

**Effort estimate:** 3–4 weeks across the five sub-phases. Parallel work possible between sub-phases once gate infrastructure is in place.

**Dependencies:** J.4.

---

### Phase J.6 — Delete obsolete detectors

**Goal:** remove detectors classified as "Remove" in J.a and detectors made redundant by successful J.5 migration. This is the simplification step — the one that makes the refactor's end-state *simpler* rather than *rehoused*.

**Changes:**
- Remove detector code, call sites, associated tests.
- Update architecture documentation to reflect the new minimal set.
- Archive removed-detector source files under `src/archive/detectors/` with a README listing why each was removed and when, so institutional memory is preserved.

**Candidates (tentative; J.a determines the final list):**
- `IsOutreachEchoAsync` cosine implementation if parroting-on-shared-surface + J.1 reasoning-strip eliminate outreach echo.
- `ConversationConfabulationChecks` 1–4 if structured summary + source-attribution make heuristic checks redundant.
- Terminal-message patterns can stay (legitimately pipeline-scoped) but the current hardcoded list can be reviewed for dead entries.

**Acceptance criteria:**
- Build green, all tests pass (including whatever tests were scoped to removed detectors — those get deleted or repurposed).
- Gate-count reduction documented with numbers. Target: at least 20% reduction in guard-code line count.
- One week of post-removal observation window: no regression in any failure class.

**Rollback:** restore from archive folder; this is the phase where rollback is hardest. Merit full review before merging.

**Effort estimate:** 1–2 weeks.

**Dependencies:** J.5 stable across at least two weeks of observation.

---

### Phase J.7 — Process integration

**Goal:** prevent the scoping problem from recurring in future work. Non-code deliverables.

**Changes:**
- **Feature-plan template update.** Add a required section: *"Does this feature produce a cognitive artifact? If yes, does it route through `CognitiveOutputGate`? Which invariants apply? Which are scope-exempt and why?"* New feature plans that skip this section fail review.
- **Memory update.** New entry `feedback_cognitive_output_boundary.md` capturing the refactor's rule so future Claude instances designing features default to the shared-surface pattern rather than re-introducing pipeline-scoped gates.
- **Paper 3 contribution draft.** *"Consistency-of-invariant-enforcement as a precondition for substrate integrity in companion AI with persistent memory."* Draft the contribution using the audit, DFDs, and the J.0 baseline vs post-refactor numbers as empirical backing. Distinct from Agentic Lens / centrality gravity.
- **Paper 2 revision.** Add a forward-pointer from §5.19 (echo chamber) and §6.16 (identity-level confabulation) to Paper 3 Theme-J contribution so the papers cross-reference coherently.
- **Research log entries** throughout the theme — one per phase deploy plus a final synthesis entry when J.7 closes.

**Acceptance criteria:**
- Template updated.
- Memory entry committed.
- Paper 3 contribution draft section completed.
- Paper 2 revision points merged.

**Effort estimate:** 1 week.

**Dependencies:** J.6 complete.

---

## 3. Measurement plan

Structured data captured across all phases. Dashboard rework (Theme I) is the eventual consumer but the data exists regardless.

| Metric | Phase introduced | Success target |
|---|---|---|
| Reasoning-field character count piped into composition (per cycle) | J.0 | J.1 brings this to **0** |
| Attribution-drift events per week (Mark-action emitted as Ani-action) | J.0 | J.2 reduces by **>70%** |
| Temporal-confab events per week | J.0 | J.3 reduces by **>70%** |
| Gate-fire counts per detector per week | J.0 | J.a gives the fire-rate distribution |
| Total guard-code lines of code | J.0 | J.6 reduces by **>20%** |
| Total gate invocations per cycle | J.0 | J.5 + J.6 reduces by **>30%** |
| Subjective outreach / reply quality | J.0 baseline | Unchanged or improved at every phase gate |

**The simplification acceptance criterion, stated as a number:** if after J.6 the refactor has NOT reduced guard-code LoC by at least 20% AND gate-invocation count per cycle by at least 30%, the refactor did not achieve Mark's Apr 24 goal (*"we can reduce the complexity after the refactor"*) and J.a's analysis was likely insufficient. Re-examine before J.7.

---

## 4. Research artifact updates

| Artifact | Update |
|---|---|
| [`ANI-Research-Log.md`](../research/ANI-Research-Log.md) | One entry per phase deploy. Entry at J.0 stating the thesis and the raw-model vs pipeline observation. Final synthesis entry at J.7. |
| [`ANI-Phase-Tracker.md`](./ANI-Phase-Tracker.md) | Theme J entry added (this plan doc is the pointer). Progress logged inline as phases complete. |
| [`ANI-Research-References.md`](../research/ANI-Research-References.md) | Add any new references cited in the refactor's design (likely: system-design literature on invariant enforcement at architectural boundaries; TBD). |
| **Paper 2** | §5.19 + §6.16 forward-pointers added in J.7. |
| **Paper 3** | Theme-J contribution drafted in J.7. |
| [`ANI-Guard-Consistency-Audit.md`](../research/ANI-Guard-Consistency-Audit.md) | Annotations as phases complete showing the audit's hypotheses being confirmed or revised by observed data. |
| [`ANI-Data-Flow-Diagrams.md`](../research/ANI-Data-Flow-Diagrams.md) | After-picture DFD produced in J.7 as comparison artifact. The acceptance-criterion paragraph in §6 of that doc specifies what the after-picture should differ on. |
| Claude project memory | J.7 adds `feedback_cognitive_output_boundary.md`. |

---

## 5. Principal risks and mitigations

**J.1 quality regression.** Removing the reasoning-to-composition pipe might cause composition to produce worse outreach because the decision rationale is no longer visible to it.

- *Mitigation:* keep the reasoning in logs for observability. Flag-gated behind `OutreachReasoningInCompositionEnabled`. Flip back immediately on quality regression. Sub-week observation window.

**J.2 scope creep.** Restructuring `RecentConversationSummary` touches many prompt-builders; risk of one-thing-leads-to-another and the phase extends past its estimate.

- *Mitigation:* enumerate consumers explicitly at phase-start. Don't ship J.2 in fragments — structured-type rollout is atomic even though the work is multi-file.

**J.a low-data observation window.** If Mark's SMS volume drops during the observation window, the fire-rate counts are unreliable and the detector inventory review is inconclusive.

- *Mitigation:* minimum two-week window, extended if activity is low. Theme J.1 Test Harness from Layer 2 Plan §3.1 (if built by then) can drive synthetic traffic to supplement real traffic. Low priority for Mark's action but worth noting.

**J.6 removal regret.** A detector deleted as "obsolete" turns out to have been catching a rare but real failure class that just didn't occur in the observation window.

- *Mitigation:* archive-don't-delete. All removed detector code moves to `src/archive/detectors/` with the reason and date. If a regression is observed later, restoration is a code-review, not a re-invention.

**Interaction with Layer 2 Phase 2b.** Layer 2 Phase 2b (parallel drift on three desire axes) is scheduled to ship one week after Layer 2 Phase 2a's data accumulates. If 2b ships while Theme J is mid-refactor, the new autonomy/competence consumption actions (Phase 2c, later) need to go through whatever shared surface exists at that point.

- *Mitigation:* Layer 2 Phase 2b is data-only (parallel drift, no new consumption actions). It ships independently of Theme J without conflict. Phase 2c consumption actions come later and will naturally target the `CognitiveOutputGate` as their commit surface — Theme J makes Phase 2c simpler, not harder.

**Parrot-class event recurrence during the refactor.** The Apr 24 parrot class is architecturally possible until Theme J ships. Another event may land on Mark during the refactor window.

- *Mitigation:* acknowledged cost. The tactical fix was withdrawn precisely because patching ahead of the refactor adds layers we intend to remove. A parrot during the refactor is evidence for the refactor, not a reason to halt it.

---

## 6. Open questions for Mark

1. **Feature numbering granularity.** Theme J as a whole claims Feature 43. Do sub-phases earn their own Feature numbers (43.1, 43.2, etc.) or are they sub-items under Feature 43 with no distinct numbering? My read: keep Feature 43 as the theme marker; sub-phases use `J.0` / `J.1` / etc. as the naming convention. Clean numbering, matches Theme G layer pattern.

2. **Phase J.1 `Feeling:` line removal — clean removal vs sanitized rebuild.** §2 Phase J.1 proposes clean removal for first deploy. Alternative: replace with a structured-fields summary built from decision JSON (desire level, temporal context, emotional context) rather than free-prose reasoning. Cleaner starting point may be clean removal; sanitized rebuild is an option if observation shows composition-quality regression.

3. **Phase J.2 render format.** What's the exact format for per-turn rendering in composition prompts? Dialogue form (*"Mark: ... / Ani: ..."*), bracketed timestamp form (*"[22:11, 8h ago] Mark: ..."*), or structured-block form? Raw-model behaviour may differ per format. Worth a small dev-time A/B before locking.

4. **Observation-window length.** J.a calls for minimum two weeks. Should we extend to four if Mark's SMS volume is low? Harder question: what is the minimum signal volume for the analysis to be meaningful? My read: at least 30 outreach-composition cycles covering varied emotional/temporal contexts. If volume is below that at two weeks, extend.

5. **Simplification targets (20% LoC, 30% invocations).** Arbitrary starting numbers. What's the actual threshold for "this refactor achieved Mark's simplification goal"? My read: numbers are a forcing function; actual go/no-go is Mark's subjective read on whether the architecture feels simpler + evidence that failure classes didn't regress.

6. **Voice pipeline inclusion.** Audit §4.6 / §4.9 flagged voice as unguarded across many classes. Do we migrate voice through the `CognitiveOutputGate` in J.5e, defer to a post-Theme-J follow-on, or leave as known gap? My read: include voice in J.5e but only with invariants where voice is reader-facing (parroting, claim verification, coherence). Keep inbound voice detection work (Feature 10/18/19 for voice) as a separate follow-on since that's a different direction (inbound classification, not output gating).

7. **Paper 3 contribution timing.** J.7 drafts the contribution. Should it be written inline as the theme progresses (phase-by-phase research log entries converted to paper prose at J.7) or held until J.6 completes so post-refactor numbers are final? My read: write inline, refine at J.7 once numbers are final. Cheaper to maintain momentum than to start from scratch.

---

## 7. Calendar estimate

From J.0 start to J.7 close:

| Phase | Duration | Notes |
|---|---|---|
| J.0 | 1–2 days | Instrumentation only |
| J.1 | 2–3 days code + 1 week observation | First upstream fix |
| J.2 | 1–2 weeks code + 1 week observation | Largest single change |
| J.3 | 1 week code + 1 week observation | Prompt-builder sweep |
| J.a | 2+ weeks observation + 2–3 days analysis | Data collection gated |
| J.4 | 1 week | Surface creation |
| J.5 | 3–4 weeks | Five sub-phases, parallel possible |
| J.6 | 1–2 weeks | Removal + verification |
| J.7 | 1 week | Process + paper work |

**Total calendar:** approximately **10–14 weeks** end-to-end. This is Theme-scale work. The raw-implementation effort is closer to 6 weeks; the rest is observation windows and analysis phases that cannot be compressed without losing confidence in the refactor's behavioural claims.

**Intermediate value points:**
- After **J.1** (week 1–2): single largest root-cause fixed. Parrot/time-confab rate should visibly drop. Mark sees improved outreach quality by week 2–3.
- After **J.3** (week 5–6): all three upstream surfaces in place. Most symptom classes should be architecturally reduced.
- After **J.5** (week 10): shared surface live, infrastructure simplified.
- After **J.6** (week 12): measurable complexity reduction. End-state.
- After **J.7** (week 14): paper-ready, process-integrated.

Each phase ships independently and produces value without waiting for later phases. The theme does not have a big-bang landing.

---

## 8. Recommended sequencing vs other active work

**Parallel with Theme J (can run concurrently):**
- Theme G Layer 2 Phase 2b (data accumulation continues; drift-parallel ships on its own schedule)
- Layer 2 Phase 3.1 test harness (independent infrastructure; actually helps Theme J by providing synthetic traffic for J.a if needed)
- Paper 2 editorial finalization (Mark's ongoing work)
- claude-recall continued dogfood (external repo, autonomous)

**Blocked by Theme J (should not start until J ships):**
- Theme G Layer 2 Phase 2c (consumption actions) — naturally targets the `CognitiveOutputGate` as its commit surface; waiting on J.4 makes 2c simpler
- Theme G Layer 3 (World Layer durability) — also produces memory output that J.5c/J.5d migrate through the gate; cleaner if Theme J's surface exists first
- Theme H Channel Realism (already deferred for separate reasons)

**Unaffected by Theme J (proceed independently):**
- Theme G Layer 4 corpus directionality (training-pipeline work)
- Theme I Dashboard as Research Tool (deliberately sibling workstream)

---

*End of Theme J plan v1. Review welcome. Next design artifact on deck (once Mark approves or revises this one): the Detector Inventory Review template for J.a, and the J.4 interface signatures.*
