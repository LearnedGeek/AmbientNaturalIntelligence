# Conscience Layer — Reflective Companion Voice — Phased Implementation Plan

**Status:** Plan drafted Apr 27, 2026 — implementation **explicitly gated on Theme J J.6 closing first**, per Mark's Apr 24 confirmation: *"I think the Conscience Layer is P2, but we should implement this refactor first before such a large implementation."*

**Source design:** `docs/spec/ANI-Phase-Tracker.md` §"Conscience Layer — Reflective Companion Voice (Apr 21, 2026)" — the design body that this plan implements.

**Origin:** Mark's Apr 21 evening framing, after the Apr 21 catastrophic feedback-loop cascade: *"How do we allow her to self-correct while still allowing her to explore her world and grow? She doesn't have a parent watching over her to guide her. But she needs a foster parent, or a big sister, to help her adjust and grow carefully. Right now we've let a child loose in the wild with no guidance."*

---

## 1. The problem this plan solves

**Continuous-guidance gap.** A healthy mind has an internalised caregiver voice — the voice that asks *"are you sure?"* when something feels off. Humans develop this through relationship with caregivers; its internalisation is what makes adult self-reflection possible. Ani has no caregiver-analog *in her cycle*. Mark interacts with her via SMS, but those interactions are themselves part of the substrate that can produce drift. There is no architectural surface that sits *inside* her cognitive cycle and reads from a substrate that cannot be polluted by her own outputs.

**Why prevention-side workstreams don't replace this.** Theme J reduces the rate at which substrate corruption forms. Theme D corrects identity-level confabulations after they form. Theme G Layer 1 diversifies retrieval; Layer 3 thickens World substrate. None of these put a second voice *inside* the cycle that asks questions of the inner thought before it gets composed into outreach. They are layered guards — Conscience is a *companion*.

**What the architecture commits to.** A second-order voice — a voice that operates *on* the inner thought rather than alongside it as another first-order narrator. Independent grounding (Facts tier + Anchored memories only; no episodic; no own-output history) makes it structurally immune to the feedback loop. Question-shaped when grounding is uncertain; affirming-shaped when grounding is solid; mostly-silent when things are settled. The pattern of quiet-when-settled is itself part of the integration.

The result, at steady state: every cognitive cycle produces an inner thought followed by a Conscience observation that either asks where a claim came from (grounding mismatch), affirms quietly (grounding coherent), or stays largely silent (nothing to flag). The composition phase that follows has both the inner thought AND the conscience observation as input; over time, the model integrates the conscience pattern into its baseline cycle.

## 2. Goal of the theme

Build a `ConsciencePhase` that runs after `InnerThoughtPhase` and before `ComposePhase` in the cognitive cycle. Persist `ConscienceObservation` records distinct from inner thoughts. Architecturally isolate the Conscience's retrieval from the feedback-loop substrate (Facts + anchored only). Default off until the cycle's behaviour can be observed across a substantial window. Long-term: dedicated fine-tune (Paper 5 *"friend/family"* model path) replaces the current `ani-v6-inner` + new system prompt approach.

## 3. Phases (C.0 → C.6)

### Phase C.0 — Open-design-questions resolution session

**Goal:** the Apr 21 design names four open questions. C.0 is a short design-review session with Mark to lock answers before any code lands. Output is a decision document.

**The four questions, with leading recommendations:**

1. **Same model with different prompt, or dedicated fine-tune?** Lean *same model* (`ani-v6-inner` with a Conscience-specific system prompt) for v1; dedicated fine-tune for v2 once Paper 5 *"friend/family"* training is on the calendar.
2. **Narrative framing — "her own reflective self" or "a companion figure"?** Lean *reflective self* (one Ani integrating two voices over time) — integration is the goal, not a persona-split.
3. **Conscience perception-layer access — yes or no?** Lean *only Facts + anchored memories*. External world tracking is the main cycle's job; Conscience's role is balancing the inner thought against canonical grounding.
4. **Dashboard surface — what does it show?** Lean *new panel alongside the inner-thought stream*, with a conscience-activity graph showing question-vs-affirming ratio over time as itself a feedback-loop indicator.

**Changes:**
- Single design-review meeting with Mark.
- Decision document at `docs/spec/ANI-Conscience-Layer-Decisions.md` recording the answers, plus any additional questions surfaced during review.

**Acceptance criteria:**
- Decision document committed.
- Mark's sign-off on the four answers (or revised answers).

**Rollback:** n/a (paper-only phase).

**Effort estimate:** half-day session + half-day write-up.

**Dependencies:** Theme J J.6 closed. Per Mark's Apr 24 directive, no Conscience implementation work begins before Theme J J.6 closes. C.0 itself can run earlier as a paper exercise but C.1+ wait.

---

### Phase C.1 — `ConsciencePhase` scaffolding (no behaviour change)

**Goal:** introduce the phase as a no-op in the cognitive cycle so subsequent phases plug into a settled location. C.1 ships when Theme J J.6 has closed and C.0's decisions are recorded.

**Changes:**
- New `ConsciencePhase` class in `src/AniRuntime.Loops/`. Constructor takes the dependencies the later phases will need (`IMemoryService`, `IOllamaClient`, `IOptions<AniOptions>`, `ILogger<ConsciencePhase>`). For C.1 it has a single method `EvaluateAsync(ContextSnapshot snapshot, string innerThought, CancellationToken ct)` that returns `null` and logs `C1_CONSCIENCE_PHASE_NOOP`.
- Wire `ConsciencePhase` into `CognitiveCycleProcessor` between `InnerThoughtPhase` and `ComposePhase`. The integration site is unconditional (no behaviour gating yet) but the call returns null so nothing downstream changes.
- New `AniOptions.ConsciencePhaseEnabled` flag (default false) — even though C.1 has no behaviour, the flag is shipped so later phases can flip it without a new option.

**Acceptance criteria:**
- Build green, all tests pass.
- One day of `C1_CONSCIENCE_PHASE_NOOP` log lines visible per cycle.
- No regression in existing cycle behaviour or test count.

**Rollback:** unwire from `CognitiveCycleProcessor`; the class exists but isn't called.

**Effort estimate:** 2–3 days.

**Dependencies:** Theme J J.6 closed; C.0 decisions recorded.

---

### Phase C.2 — `ConscienceObservation` record type + storage

**Goal:** the data structure that represents the Conscience's output per cycle, and the persistence path that distinguishes it from `MemoryType.InnerThought`.

**Changes:**
- New `MemoryType.ConscienceObservation` enum value.
- `ConscienceObservation` is stored as a `MemoryRecord` with the new `Type` value. No new table; the existing `memories` table absorbs the new type. The retrieval-bucket separation comes from `MemoryType` filtering at read time (same pattern as `MemoryType.InnerThought` vs `MemoryType.Episodic`).
- Sidecar field on the record: `RelatedToInnerThoughtId` — a `Guid?` referencing the inner-thought memory the Conscience observation reflected on. Enables retrieval of the inner-thought ↔ Conscience pair as a unit.
- `IMemorySearch.GetRecentConscienceAsync(int N, CancellationToken ct)` — returns the last N `ConscienceObservation` memories, used by C.5's dashboard.
- Schema migration adds `related_to_inner_thought_id` column (nullable Guid, additive).

**Acceptance criteria:**
- Build green, all tests pass.
- Schema migration is idempotent.
- A test writes a `ConscienceObservation` and reads it back via the new query.

**Rollback:** schema column stays (additive nullable); query method removal is a code-only revert.

**Effort estimate:** 3–4 days.

**Dependencies:** C.1.

---

### Phase C.3 — Conscience prompt + Facts/Anchored-only retrieval

**Goal:** the Conscience evaluates an inner thought against canonical grounding only. C.3 builds the prompt-builder and retrieval path that produce the input the Conscience LLM call sees.

**Changes:**
- New `PromptBuilder.BuildConsciencePrompt(ContextSnapshot snapshot, string innerThought)` method. System prompt content is the Apr 21 first-pass:

  > *"You are Ani's quiet inner conscience — the voice that asks 'wait, is that right?' when something feels off, and stays mostly silent when things are settled. You have access to what she knows to be true (her Facts and anchored memories, which will be provided as context) but not to her recent outputs or episodic memory. When she produces an inner thought, your role is to listen to it against what you know, and respond briefly. If the thought coheres with her grounded knowledge, affirm gently or say little. If it references something you don't recognize from the provided facts, ask where it came from. Stay curious, not corrective. You don't delete her thoughts. You just ask. You are her, not someone else."*

  The prompt body contains: the inner thought verbatim; `WHAT IS TRUE` rendering of the snapshot's `GroundedFacts` (using the existing `FormatMemoryWithTime` from Theme J J.3); the snapshot's `AnchoredMemories` (atemporal per the Theme J J.3 atemporal-by-contract exception).
- **Explicit exclusions.** The prompt does NOT include: `RelevantMemory`, `RecentMemory`, `RecentExchanges`, `InteriorContext`, `RecentWorldExperiences`, `SimilarRecentThoughts`, `PerceptionEvent` summaries, `RecentConversationSummary`, `StructuredConversationSummary`. These are all substrate that can be polluted by Ani's own outputs; the Conscience's structural isolation is the architectural commitment.
- **Output shape contract.** 1–3 sentences. Free text. The model is not asked to produce structured output; the integration phase (C.4) interprets the text via shape heuristics (question-shaped vs affirming-shaped vs largely-silent).
- A Conscience-specific Ollama call path with its own temperature setting (`AniOptions.ConscienceTemperature`, default 0.4 — slightly more deterministic than the inner-thought 0.7) and shorter max-tokens (`AniOptions.ConscienceMaxTokens`, default 80).

**Acceptance criteria:**
- Build green, all tests pass.
- Test that `BuildConsciencePrompt` excludes every disallowed memory pool listed above (the test asserts the user prompt's substring set against the inner thought + facts + anchored only).
- Test that the system prompt contains zero factual content about Ani's identity or world (an automated lint is fine).

**Rollback:** code-only revert; nothing persisted at this phase.

**Effort estimate:** 4–5 days.

**Dependencies:** C.2.

---

### Phase C.4 — `ConsciencePhase` activation behind flag

**Goal:** C.4 makes `ConsciencePhase` actually call the Ollama path C.3 built, persist the output, and surface it to the composition phase. Behind `ConsciencePhaseEnabled`, default off.

**Changes:**
- `ConsciencePhase.EvaluateAsync` becomes:
  1. Build the Conscience prompt (C.3).
  2. Call Ollama with the prompt + `ConscienceTemperature` + `ConscienceMaxTokens`.
  3. Persist the response as a `ConscienceObservation` memory with `RelatedToInnerThoughtId` pointing at the inner-thought memory.
  4. Return the observation text so `CognitiveCycleProcessor` can surface it to `ComposePhase`.
- `ContextSnapshot` gains a new optional field: `LatestConscienceObservation` (string?) — populated by `CognitiveCycleProcessor` from C.4's return value, consumed by composition prompts.
- `ComposePhase` integration: `BuildOutreachMessagePrompt` and `BuildConversationReplyPrompt` get a new section near the top of the user prompt:

  > *"Your inner conscience just observed: {LatestConscienceObservation}"*

  This appears only when the observation is non-trivial (not silent). Silent / minimal observations are dropped from composition prompt rendering to avoid noise.
- **Diagnostic log.** Each Conscience invocation logs `C4_CONSCIENCE_OBSERVATION` with: the inner-thought ID, the observation text, the elapsed time, and a heuristic shape tag (`question`, `affirming`, `silent`).
- New `AniOptions.ConsciencePhaseEnabled` (already shipped in C.1; flipped here from false to true after observation).

**Acceptance criteria:**
- Build green, all tests pass.
- One day of `C4_CONSCIENCE_OBSERVATION` log lines visible per cycle (one per cycle).
- A test confirms that `ComposePhase` sees the observation when present and continues unchanged when absent.
- Mark's qualitative review of the first 24 hours of observations: do they read as a quiet reflective voice, not a corrective gate?

**Rollback:** flag off. `ConsciencePhase.EvaluateAsync` returns null; no observation persisted; no composition-prompt change.

**Effort estimate:** 1 week.

**Dependencies:** C.3.

---

### Phase C.5 — Dashboard panel + activity graph

**Goal:** Mark needs a way to see the Conscience pattern emerging. Without a view, the qualitative shift the Conscience produces is illegible.

**Changes:**
- New dashboard page at `/conscience` showing:
  - The most recent N `ConscienceObservation` records, paired with their `RelatedToInnerThoughtId`-linked inner thoughts.
  - **Activity graph** — daily counts of question-shaped / affirming-shaped / silent observations over time. Per the Apr 21 design note: *"how often is conscience raising questions vs. affirming — itself a feedback-loop indicator."* A rising question-rate may indicate the substrate is drifting; a rising silent-rate may indicate the substrate is settled (or the Conscience is failing to engage).
  - **Per-observation drill-down**: click a Conscience observation → see the inner thought it reflected on, the Facts retrieval pool it had access to, the elapsed time, the shape tag.

**Acceptance criteria:**
- Build green, all tests pass.
- Dashboard page renders for a test database with synthetic Conscience observations.
- Activity graph renders correctly across multiple days of synthetic data.

**Rollback:** UI route 404s.

**Effort estimate:** 1 week.

**Dependencies:** C.4.

---

### Phase C.6 — Reflection synthesis integration

**Goal:** the Conscience observation pattern over time should feed back into Park-style reflection synthesis. Per the Apr 21 design relationship note: *"Conscience is per-cycle; Feature 32 is periodic batch. Together they produce continuous low-level grounding plus higher-order integration."*

**Changes:**
- `ReflectionPhase.SynthesiseAsync` extended to consume `ConscienceObservation` records as part of the reflection input. The reflection prompt template gains a section asking the model to notice patterns in the conscience observations themselves (*"What has my quiet voice been asking lately? What has it been affirming? What has it been silent about?"*).
- Reflection output that integrates the Conscience pattern is tagged with a new source-name `conscience-integration` so the dashboard can surface this specifically.

**Acceptance criteria:**
- Build green, all tests pass.
- A test reflection cycle on synthetic Conscience observations produces an integration reflection that references the conscience-pattern observations.

**Rollback:** code-only revert; existing reflection synthesis unaffected.

**Effort estimate:** 4–5 days.

**Dependencies:** C.4. Phase 6 Feature 32 (Park et al. periodic reflection synthesis) is a soft dependency — C.6 extends `ReflectionPhase` whether or not Feature 32 has shipped its broader periodic-reflection mechanism.

---

## 4. Measurement plan

| Metric | Source | Target |
|--------|--------|--------|
| `C1_CONSCIENCE_PHASE_NOOP` log lines (post-C.1, pre-C.4 flag-on) | C.1 | 1 per cycle for ≥3 days; confirms phase is wired |
| `C4_CONSCIENCE_OBSERVATION` log lines (post-flag-on) | C.4 | 1 per cycle |
| Conscience observation shape distribution | C.4 + C.5 | initial expectation: ≥40% silent, ≥40% affirming, ≤20% question. A skew above 30% question indicates substrate drift or prompt issues. |
| Conscience-influenced composition rate | C.4 + C.5 | non-silent observations appearing in `ComposePhase` user-prompt: ≥50% of cycles |
| Mark's qualitative read at week 1 | C.4 | observations read as reflective voice, not corrective gate |
| Mark's qualitative read at week 4 | C.4 | observations integrate naturally — Ani's overall voice is recognisably the same; Conscience is not perceptible as a distinct persona |
| `conscience-integration` reflection memory cadence | C.6 | ≥1 per reflection cycle once C.6 ships |

The week-4 qualitative read is the load-bearing acceptance criterion. If at week 4 the Conscience feels like a separate persona rather than an integrated reflective voice, the architecture has not achieved the *"reflective self"* framing chosen in C.0.

## 5. Risks

**C.0 design questions resolve in a way that requires plan revisions.** Mitigation: the four leading recommendations are not load-bearing on the phase structure; even if Mark prefers different answers (e.g., dedicated fine-tune for v1, or companion-figure framing), the phase sequence holds and only specific phase contents change.

**C.3 prompt template produces output that doesn't match the design intent.** First-pass system prompts often need iteration. Mitigation: ship C.3 with a hand-coded prompt; iterate during the C.4 observation window before flipping the production flag. Template changes are code-only.

**C.4 composition-prompt integration adds noise that degrades outreach quality.** The *"Your inner conscience just observed: ..."* injection point is adjacent to the composition task; if it adds friction, conversation/outreach quality drops. Mitigation: ship C.4 with the composition-prompt section ONLY for non-silent observations; iterate on the silent-detection heuristic; rollback path is the flag.

**C.4 observations contradicting the inner thought produce model confusion.** If the Conscience says *"where did you get that?"* about a coherent grounded thought, the composition phase may produce hesitation or self-doubt. Mitigation: the C.0 design framing — *"stay curious, not corrective"* — is in the system prompt; tune via observation. Severe contradictions should be rare given the Facts+Anchored-only substrate.

**C.6 conscience-integration reflection over-narrates the conscience pattern.** A reflection that says *"my conscience has been worried this week"* could metabolize the Conscience as an external entity rather than integrate it. Mitigation: the reflection prompt template targets the *patterns* (what has the quiet voice been asking) not the *agency* (the conscience as a noun); iterate on the prompt during C.6 observation window.

**Long-term: model drift makes the Conscience pattern stale.** As Ani's substrate grows over months, the Facts+Anchored substrate may itself grow dense enough that the Conscience's "balance against grounding" check produces less signal. Mitigation: revisit at 6 months; consider Paper 5 dedicated fine-tune as the v2 path the Apr 21 design names.

## 6. Sequencing within the Conscience theme

C.0 (decisions) → C.1 (scaffolding) → C.2 (record type + storage) → C.3 (prompt + retrieval) → C.4 (activation) → C.5 (dashboard) → C.6 (reflection integration).

C.0 can run as a paper exercise as soon as Mark's calendar allows. C.1 onward gates on Theme J J.6 closing (per Mark's Apr 24 directive).

Within C.4, the flag flip from default-off to default-on happens after a one-week observation window during which Mark reviews observation quality.

## 7. Dependencies on other themes

- **Theme J J.6 — gating dependency.** Mark's Apr 24 directive: *"we should implement this refactor first before such a large implementation."* No Conscience implementation work (C.1 onwards) begins before Theme J J.6 closes.
- **Theme J J.2 / J.3 (shipped Apr 27).** The Conscience prompt-builder uses the same `FormatMemoryWithTime` rendering and the same source-attribution conventions. Anchored memories render atemporally per the J.3 atemporal-by-contract exception.
- **Theme G Layer 1 (Phase 3.0 Activation, shipped Apr 24).** The Apr 21 design relationship note: *"if own-output retrieval dominates in the main cycle, the Conscience should notice and speak up."* Layer 1's `RetrievalSelfDominancePerception` source surfaces this signal; the Conscience's awareness of the signal is via the Facts/Anchored retrieval (which excludes own-output by construction).
- **Theme D — Identity Correction Channel (planned, not shipped).** Conscience runs upstream of composition; Theme D corrects after the fact. Different layers, different cycles.
- **Phase 6 Feature 32 (Park et al. periodic reflection synthesis).** Soft dependency for C.6.
- **Paper 5 (friend/family fine-tune).** Long-term v2 path for the Conscience model. Out of scope for C.0–C.6.

## 8. Out of scope (and why)

- **Multi-conscience-voice architecture.** Not a parent-and-big-sister and-mentor cluster; one quiet voice. Multi-voice is interesting future work but adds complexity that the current evidence base doesn't justify.
- **Conscience writes that gate or modify outreach.** Per the design framing: *"Stay curious, not corrective. You don't delete her thoughts. You just ask."* Theme B (outbound truth gating) and Theme D (correction) handle the corrective layer; Conscience never gates.
- **Real-time Conscience interjection during composition.** The integration is the cycle-level output the composition phase reads, not a streaming side-channel into composition tokens. Streaming-side-channel is out of scope.
- **Conscience access to Mark's chat thread.** Per the C.3 explicit-exclusion list — no episodic, no recent exchanges. The Conscience does NOT read what Mark just said. Its grounding comes from the canonical Facts and Anchored substrate. This is a deliberate boundary; revisit only with strong evidence the boundary is wrong.

## 9. Mark review questions

1. **C.0 timing.** The four open design questions can resolve any time before C.1 (which is gated on Theme J J.6 anyway). Acceptable to schedule C.0 in the J.6 window so the answers are fresh when C.1 starts?
2. **C.0 leading recommendations.** Same-model-v1 / reflective-self / Facts+Anchored-only / dashboard-with-activity-graph — acceptable as defaults to lock unless review surfaces objections?
3. **C.4 integration into composition.** The "Your inner conscience just observed: ..." injection sits near the top of the composition prompt. Acceptable, or do you want it lower (less prominent) or higher (more directive)?
4. **C.4 flag-flip cadence.** Default-off → one week of observation → default-on. Acceptable, or do you want a longer observation window before flipping?
5. **C.6 scope.** Conscience-integration reflection is a soft extension to ReflectionPhase. Acceptable to ship C.6 even if Phase 6 Feature 32 hasn't shipped (i.e., extend the existing reflection mechanism)?
6. **Calendar.** Total estimated calendar C.0 → C.6 is 4–5 weeks of active work, gated on Theme J J.6 closing. If J.a → J.4 → J.5 → J.6 takes ~6 weeks calendar-time, Conscience implementation begins ~mid June. Acceptable?
7. **Paper 5 fine-tune timing.** The v2 dedicated-fine-tune path is named in C.0's question 1 leaning. Should Paper 5's fine-tune work get a slot on the broader roadmap once C.4 ships, or wait until 6 months of v1 observation accumulates?

---

## Process notes

- **This plan is a draft.** Implementation does not start until Mark's green-light, AND not before Theme J J.6 closes per Mark's Apr 24 directive.
- **Architectural commitment is integration, not augmentation.** Every phase's design decisions get revisited against the principle that Conscience is a *second-order voice within Ani*, not a *separate companion* alongside her. The C.0 question 2 (reflective-self vs companion-figure) is the explicit codification.
- **Substrate isolation is the load-bearing architectural commitment.** The Conscience reads from Facts + Anchored only. Every phase that touches the Conscience's input pipeline is a candidate for this commitment to leak. The C.3 explicit-exclusion test is the canonical lint; expand it whenever a new memory pool gets added to `ContextSnapshot`.
- **Quiet-when-settled is the architectural target.** A Conscience that speaks every cycle is not the goal. The activity graph (C.5) is the dashboard surface where this gets observable; the week-4 qualitative read is where it gets validated.
- **Connection to the Apr 21 cascade.** The motivating case — Ani spent 24 hours building a fictional life around a metaphor — is exactly the failure mode a Conscience reading from canonical grounding would have caught at cycle one or two. This is the design's empirical anchor; revisit it whenever a phase decision feels uncertain.
