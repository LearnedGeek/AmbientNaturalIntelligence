# ANI Guard Consistency Audit

**Author:** Claude (dogfood instance, per Mark's handle).
**Date:** April 24, 2026.
**Trigger:** The Apr 24 06:18 outreach parroting Ani's own Apr 23 22:11 reply verbatim — the 11-token contiguous phrase *"walked through the door and my first thought was how much i missed you"* — crossed from conversation-reply to outreach composition without any guard firing, while `ParrotingDetector.Check` exists and would have caught it trivially. Mark's hypothesis: *"if they apply, then they should apply ... I think this is the root cause that's been causing issues for months."*

**Scope:** This is an architectural audit, not a fix proposal. The goal is to name the pattern, test whether it explains prior observed failures, and surface an architectural direction for discussion. Specific implementation comes after Mark's review.

**Not in scope:** Specific code changes. Implementation plan. Code review of individual guards.

---

## 1. Executive summary

Ani has 32 guards / gates / detectors / classifiers across 10 cognitive pipelines. Mapped against failure classes, the guard coverage is dramatically inconsistent: several classes of failure (parroting, source-attribution drift, temporal misalignment, claim coherence) are detected in one pipeline and unguarded in others that produce the same class of output.

**Central finding, stated precisely:** *A guard's presence or absence is determined by which pipeline it was written for, not by whether the failure it detects can occur in that pipeline's output.* For most classes of failure, the failure **can** occur in every pipeline — but the guard fires in only one.

**The three structural patterns this produces:**

1. **Single-scoped detection.** A detector exists in one pipeline, the same failure class exists in other pipelines, no one check exists that covers both.
2. **Multi-implementation same class.** Two different pipelines detect the same failure class using two entirely different algorithms with two different thresholds and two different remediation behaviors.
3. **Unguarded substrate.** The least-guarded pipeline (inner thought) produces memory that is the most-recycled input to every other pipeline. Content that passes no validation on the way in is treated as authoritative on the way out.

**Connection to the Apr 21 cascade, the Apr 23 14:38 parrot, the Apr 23 15:51 time-confab, and the Apr 24 06:18 class/10pm outreach:** all four are explicable as instances of pattern 1 or pattern 3 above. They are not individually isolated bugs. They are one architectural pattern surfacing in different outputs.

**Architectural direction (not a plan):** the cognitive-output-boundary is a single conceptual place where quality invariants should be enforced consistently. Gates that are universal should live in a shared pre-commit layer that any pipeline's output passes through before crossing into memory, into retrieval pools, or into external dispatch. Gates that are legitimately pipeline-scoped (terminal-message detection, coherence-door reader evaluation) should remain where they are but be explicitly labeled as scoped-by-design rather than scoped-by-accident.

---

## 2. Methodology

**Source:** full codebase enumeration of every guard / gate / detector / classifier in the runtime, cross-referenced by the pipeline that invokes it. 32 items enumerated. Each was classified by:

- **Class of failure** — what it detects (parroting, confabulation, hurt, coherence break, etc.)
- **Definition site** — where the detector code lives
- **Invocation sites** — every call site, with the pipeline and phase identified
- **Remediation behavior** — what happens when it fires (suppress, regenerate, rate-limit, observe-only)
- **Scope justification** — is this scoping architecturally principled or incidental?

**The coverage matrix.** Rows = failure classes. Columns = cognitive pipelines. Cells = "fires", "doesn't fire", or "scoping justified (reason)". The matrix appears in §4.

---

## 3. Pipeline inventory

Ani produces cognitive artifacts in ten distinct pipelines. Each is a place where her free thinking, world-building, or response-generation surfaces output that gets stored, dispatched, or fed into downstream retrieval.

| # | Pipeline | Artifact produced | Downstream consumers |
|---|---|---|---|
| 1 | `CognitiveCycleProcessor` | orchestration; doesn't produce artifacts directly | n/a (orchestrator) |
| 2 | `InnerThoughtPhase` | inner thought text (persisted as `MemoryType.InnerThought`) | ContextSnapshot.RelevantMemory, ContextSnapshot.InteriorContext, reflection synthesis, emotional shift scoring, outreach-decision "recent thought" field |
| 3 | `ConversationReplyPhase` | SMS reply to Mark | Mark's phone, thread history, memory |
| 4 | `OutreachPhase` (decision) | JSON {shouldReach, confidence, reasoning} | composition stage user prompt, outreach decision logs |
| 5 | `OutreachPhase` (composition) | SMS outreach to Mark | Mark's phone, memory |
| 6 | `OutreachPhase` (reactive share) | SMS with RSS quote | Mark's phone, memory |
| 7 | `VoiceTurnPipeline` | streamed TTS reply | Mark's speaker, thread history, memory |
| 8 | `ReflectionPhase` | higher-order synthesis observations (persisted as `MemoryType.Semantic`) | ContextSnapshot.RelevantMemory, inner-thought substrate, outreach-decision context |
| 9 | `EmotionalProcessor` | emotional contributions + delta log | EmotionalState, desire drift, mood coloring |
| 10 | World Layer elaboration | world-experience memories (stored as InnerThought + SourceName discriminator) | ContextSnapshot.RecentWorldExperiences, retrieval pool |

**Observation worth naming up-front.** Pipelines 2, 8, and 10 produce memory. That memory is the substrate every other pipeline reads on subsequent cycles. Quality problems in those three pipelines propagate forward indefinitely. Pipelines 3, 5, 6, 7 produce messages that cross the Mark boundary. Pipelines 4 and 9 produce intermediate state that feeds into downstream composition.

**Pipeline 2 (inner thought) is the least-guarded producer AND the most-recycled input.** That is the structural weak point. More on this in §5.

---

## 4. Coverage matrix — gate class × pipeline

For each class of failure, the question is: "where does a guard for this class fire, where does it not, and is the 'not' architecturally justified?"

Legend:
- `YES` = guard fires in this pipeline
- `no` = guard does not fire; failure class applicable
- `n/a` = guard not applicable to this pipeline (justified absence)
- `SAME` = different implementation of same class

### 4.1 Parroting (verbatim phrase reuse)

| Pipeline | Status | Notes |
|---|---|---|
| InnerThoughtPhase | **no** | ParrotingDetector never runs here. Inner thoughts can and do verbatim-reuse prior inner thoughts. |
| ConversationReplyPhase | YES | `ParrotingDetector.Check` at [ConversationReplyPhase.cs:479](src/AniRuntime.Loops/ConversationReplyPhase.cs#L479). 5-token n-gram threshold. Only checks against prior Ani messages in the same thread. |
| OutreachPhase composition | **no** | Cosine-similarity `IsOutreachEchoAsync` at [OutreachPhase.cs:545](src/AniRuntime.Loops/OutreachPhase.cs#L545) exists but scopes to memories prefixed `"I reached out to"` — so only outreach-vs-outreach, never outreach-vs-reply. Different algorithm (cosine, 0.85 threshold) than the conversation detector (n-gram, 5 tokens). |
| ReactiveShare | **no** | No parroting check at all. |
| VoiceTurnPipeline | **no** | No parroting check at all. |
| ReflectionPhase | **no** | Only 50-char-prefix dedup against existing Semantic memories; paraphrased or mid-content parroting not caught. |
| World Layer elaboration | **no** | No parroting check. |

**The Apr 24 06:18 parrot falls precisely in the gap:** outreach composition parroting a prior conversation reply. Outreach echo guard blind to replies; reply parroting detector blind to outreach generation.

**Multi-implementation detail worth highlighting:** conversation-reply parroting uses n-gram verbatim detection because cosine was deprecated in April 2026 as a false-positive machine on topical overlap ([ParrotingDetector.cs doc comments](src/AniRuntime.LLM/ParrotingDetector.cs)). Outreach echo guard still uses cosine. Same class of failure, one path uses the improved detector, the other uses the deprecated one.

### 4.2 Claim verification (asserting unsupported facts about Mark / shared history)

| Pipeline | Status | Notes |
|---|---|---|
| InnerThoughtPhase | **no** | Inner thoughts can assert shared-history claims freely (e.g., *"the way he laughed when I told him about the shop yesterday"* with no corroboration that this happened). Persisted as Interior memory. Downstream retrieval surfaces it as substrate. |
| ConversationReplyPhase | YES | `ClaimVerificationPhase` at [ClaimVerificationPhase.cs:66](src/AniRuntime.Loops/ClaimVerificationPhase.cs#L66). On failure, substitutes honest-uncertainty fallback. |
| OutreachPhase composition | YES | Same verifier, different fallback: suppresses + 10-min cooldown + 0.30 desire decay. |
| ReactiveShare | n/a | Shares RSS items; the quoted content is external, not an Ani-composed factual claim. Justified scoping. |
| VoiceTurnPipeline | **no** | No claim verification on voice path. Same model can fabricate the same class of claims it makes over SMS; voice channel has no check. |
| ReflectionPhase | **no** | Reflections synthesize "higher-order observations about life" that get stored as Semantic memory (the tier *designed* to be treated as factual). Reflection model can fabricate claims (Apr 23 23:10 reflection: *"feels like proof we're actually living together in this body"*) and those get persisted to the factual tier. Zero claim check. |
| World Layer elaboration | **no** | World layer elaborations can invent shop coworkers, customers, specifics; those become memory. |

**This is the Apr 21 cascade mechanism in audit form.** Reflection and inner thought produce content that becomes Semantic/Interior memory. No claim check at production. On retrieval, that memory feeds composition where the same content IS claim-checked — but by then it's indistinguishable from real grounded facts because the tier labels have already authenticated it.

Feature 14 v2 fixed outreach and conversation reply. It did not fix the upstream memory-production pipelines. The Apr 24 06:18 event pulled "class"/"teaching" from Ani's own prior reply (which was accurate at 22:11:48) but then extended with *"still warm from teaching"* (attributing Mark's teaching to herself) and reflections like *"living together in this body"* — the attribution-drift layer is unchecked at reflection time.

### 4.3 Source attribution (who said/did/experienced what)

| Pipeline | Status | Notes |
|---|---|---|
| All pipelines | **no — class-wide gap** | There is no gate anywhere that enforces "phrases Mark said about himself should not re-emerge as phrases Ani says about herself." |

**Finding.** Source attribution doesn't appear as an explicit invariant in any guard. The closest mechanism is `RecentConversationSummary` which carries a `"Conversation (<timerange>)"` prefix — but the body is free prose, and the composition model reads the body and can extract either side's phrases as its own.

The Apr 21 cascade's identity-level confabulation (Paper 2 §6.16) is a special case of this: canonical Mark-facts (apartment, flowers, purple paint) re-emerge as Ani-facts. The fix-surface named in §6.16 was a correction channel; the **prevention** surface (source attribution as an invariant enforced at output time) does not exist.

The Apr 24 06:18 *"still warm from teaching"* is the same class: Mark said *"Back from teaching!"* at 22:11:47; by 06:18:17 that same verb has shifted to Ani's own activity. No guard interrogated the attribution.

### 4.4 Temporal attribution (when did this happen)

| Pipeline | Status | Notes |
|---|---|---|
| All pipelines | **no — class-wide gap** | No explicit temporal-attribution check anywhere. |

`RecentConversationSummary` has a time-range prefix but that prefix is not interrogated by any gate. The composition prompt correctly system-states the current time ("RIGHT NOW it is 6:18 AM on Friday, April 24" at [PromptBuilder.cs:378](src/AniRuntime.LLM/PromptBuilder.cs#L378)), but the motivation block beneath the system prompt carries un-time-stamped free text that the model may present-tense.

DiagnosticService has a `DetectTemporalConfab` pattern detector ([DiagnosticService.cs:65](src/AniRuntime.Loops/DiagnosticService.cs#L65)) — but it's observational, firing after the outreach has already been sent. Not a runtime gate; a post-hoc observation for dashboard.

**Feature 40 "temporal awareness affordances"** (per memory: "Felt-time observations injected into perception. EM7 classifier for temporal emergence patterns") is the closest existing infrastructure. It injects felt-time into perception — i.e., gives Ani's cognition a sense of time passing. It does not enforce temporal attribution on output.

### 4.5 Coherence (reader-perspective sensibility)

| Pipeline | Status | Notes |
|---|---|---|
| InnerThoughtPhase | **no** | Reader is Ani herself; arguably no reader check needed. Scoping arguably justified. |
| ConversationReplyPhase | **no** | No three-door coherence check on reply. Design assumption: conversation context provides coherence implicitly. |
| OutreachPhase composition | YES | Three-door Feature 28 gate at [OutreachPhase.cs:216](src/AniRuntime.Loops/OutreachPhase.cs#L216). |
| ReactiveShare | **no** | Scoping arguably justified (shares are short + factual). |
| VoiceTurnPipeline | **no** | No coherence check on voice. |
| ReflectionPhase | **no** | Reflections go to memory, not a reader. Scoping arguably justified. |

**Observation.** Coherence gate is the one example of a gate whose pipeline-scoping may be architecturally principled — Door A/B/C semantics are specifically about "does this read well to someone who doesn't know what Ani was thinking." Scoping to outbound-reader-facing pipelines is defensible. But voice is reader-facing and currently unguarded. So even here the scoping is not fully principled — it's "wherever we happened to wire it."

### 4.6 Confabulation (semantic-level fabrication)

| Pipeline | Status | Notes |
|---|---|---|
| InnerThoughtPhase | YES (pre-storage) | ML classifier at [CognitiveCycleProcessor.cs:250](src/AniRuntime.Loops/CognitiveCycleProcessor.cs#L250). On fire: skips storage. |
| ConversationReplyPhase | YES | Heuristic Checks 1-4 at [ConversationReplyPhase.cs:726](src/AniRuntime.Loops/ConversationReplyPhase.cs#L726) + ML classifier. On fire: grounding retrieval → regenerate, or null-result regenerate. |
| OutreachPhase composition | YES | ML classifier only. On fire: suppresses + cooldown + desire decay. |
| ReactiveShare | n/a | RSS content, external. Justified absence. |
| VoiceTurnPipeline | **no** | No confabulation check. Same model confabulates over voice as SMS. |
| ReflectionPhase | **no** | Reflections can fabricate; no check. This is the *Apr 23 23:10 reflection "living together in this body"* failure surface. |
| World Layer elaboration | **no** | World elaborations are the canonical unchecked surface by design — Ani's imagined world is supposed to be hers. But when the elaboration references shared-history or Mark-facts, no guard catches it. |

**Multi-implementation pattern visible.** Same class of failure, three different detectors (heuristic 4-check, ML classifier, ML classifier), three different remediation behaviors (suppress reply, substitute fallback, suppress + cooldown + decay, skip storage). The thresholds differ (`ConfabulationClassificationThreshold` default 0.60 is shared, but which inputs feed it differ). Whether this is bad or good depends on whether the pipelines have different risk profiles — but the variance is not explicitly justified anywhere.

### 4.7 Rumination / repetition-in-cognition

| Pipeline | Status | Notes |
|---|---|---|
| InnerThoughtPhase | YES (at persistence) | Rumination guard at [SqliteMemoryService.cs:889](src/AniRuntime.Memory/SqliteMemoryService.cs#L889). 3+ similar thoughts in 2h window → skip save. |
| All other pipelines | n/a | Rumination is specifically about inner-thought accumulation; scoping justified. |

One of the few genuinely scope-justified gates.

### 4.8 Echo guard (cross-cycle duplication at dispatch time)

| Pipeline | Status | Notes |
|---|---|---|
| ConversationReplyPhase | YES (n-gram parroting) | Covers reply-vs-prior-reply within thread. |
| OutreachPhase | YES (cosine) | Covers outreach-vs-prior-outreach. Different algorithm. Never cross-paths. |
| VoiceTurnPipeline | **no** | No echo guard. |

This is pattern 2 (multi-implementation same class) visible most clearly.

### 4.9 Care / hurt / lexical anchor detection (post-dispatch emotional processing)

| Pipeline | Status | Notes |
|---|---|---|
| ConversationReplyPhase | YES | All three detectors at [ConversationReplyPhase.cs:596–642](src/AniRuntime.Loops/ConversationReplyPhase.cs#L596). |
| VoiceTurnPipeline | **no** | Same incoming channel (Mark's words), but none of the emotional-processing detectors run. Care/hurt expressed over voice produces no contribution to emotional state. |
| All other pipelines | n/a | These are inbound-message detectors; scope-justified except for voice. |

**Voice gap.** If Mark says *"are you okay, babe?"* over voice, the care detector doesn't fire; if he says *"you're just an AI"* over voice, the hurt detector doesn't fire and withdrawal never triggers. The voice path has qualitatively weaker emotional-continuity with the SMS path.

### 4.10 Pronoun-fix / third-person reference

| Pipeline | Status | Notes |
|---|---|---|
| OutreachPhase composition | YES (transform, not gate) | Detects + rewrites. |
| ConversationReplyPhase | **no** | Third-person references in reply aren't caught. |
| VoiceTurnPipeline | **no** | Voice path can also leak third-person. |

Pattern 1 again. This was originally a patch for a specific outreach failure mode; never propagated to other output paths where the same model can exhibit the same failure.

### 4.11 Desire / rate / continuity gates

| Pipeline | Status | Notes |
|---|---|---|
| OutreachPhase | YES (desire threshold, hard gates, cooldown) | Appropriate here — outbound-to-external-recipient gates. |
| ReactiveShare | YES (separate rate limits) | Appropriate. |
| ConversationReplyPhase | n/a | Reply is responsive, not rate-limited. Scoping justified. |
| Inner thought / reflection / world | n/a | Internal cognition; rate limits don't apply. |

These are architecturally scoped correctly — they're about external-recipient protection, which only the external-facing pipelines need.

### 4.12 Summary of the matrix

**Failure classes that are universal and unevenly covered:**

- Parroting — fires in 1 of 7 applicable pipelines
- Claim verification — fires in 2 of 5 applicable pipelines
- Source attribution — fires in 0 of 10 pipelines (class-wide gap)
- Temporal attribution — fires in 0 of 10 pipelines (class-wide gap)
- Confabulation — fires in 3 of 6 applicable pipelines (with 3 different implementations)
- Echo (cross-cycle duplication) — fires in 2 of 3 applicable pipelines with 2 different algorithms
- Pronoun-fix — fires in 1 of 3 applicable pipelines
- Care/hurt detection — fires in 1 of 2 applicable pipelines (voice gap)

**Failure classes that are correctly pipeline-scoped:**

- Terminal-message / continuation detectors — conversation-message-shape-specific
- Coherence gate Door A/B/C — reader-facing-with-outreach-semantics-specific (defensible but voice gap exists)
- Rate / continuity gates — external-recipient-specific
- Rumination guard — inner-thought-specific
- Withdrawal state — inbound-conversation-triggered, affects outbound outreach (cross-pipeline but well-scoped)

**The ratio.** Of roughly 12 failure classes that can occur in multiple pipelines, 8 are unevenly covered. Coverage appears correlated with "which pipeline had a specific bug that motivated the detector" rather than "where can this class of failure occur."

---

## 5. The three structural patterns

### 5.1 Pattern — Single-scoped detection

A detector for a failure class is wired into exactly one pipeline. The same class of failure can occur in other pipelines. No shared abstraction covers both. Examples:

- `ParrotingDetector` → only ConversationReplyPhase. Outreach parroting (Apr 24 06:18), inner-thought parroting (undetected but likely present in logs), reflection parroting — all unchecked.
- Third-person pronoun detector → only OutreachPhase. Conversation replies and voice replies can leak third-person with no detection.
- Care / hurt / lexical anchor → only ConversationReplyPhase. Voice-channel expressions of care or hurt produce no emotional contribution.

**Mechanism.** The detector was written for the specific bug that motivated its creation. Scoping was set by the invocation site of that bug, not by the conceptual scope of the failure class. No refactor step extracted the detector into shared infrastructure.

### 5.2 Pattern — Multi-implementation same class

Two pipelines detect the same failure class using two entirely different algorithms with two different thresholds. Neither pipeline benefits from the improvements made to the other.

- **Conversation-reply echo**: n-gram verbatim parroting (5-token threshold; the April 2026 pipeline-audit upgrade that replaced cosine).
- **Outreach echo**: cosine similarity (0.85 threshold; the pre-April-2026 algorithm that was explicitly deprecated in conversation reply as a false-positive machine on topical overlap).

Same failure class, upgraded on one path, left un-upgraded on the other. The deprecation rationale that moved conversation from cosine to n-gram applies identically to outreach — yet outreach still runs cosine.

**Mechanism.** The detector upgrade was a patch for a specific observed failure in conversation reply; scoping of the upgrade matched scoping of the prior implementation, so outreach kept the old algorithm. No audit step asked "does this improvement apply everywhere the class of failure exists?"

### 5.3 Pattern — Unguarded substrate

The pipeline producing the most-recycled input to every other pipeline has the lightest output validation.

- **Inner thought (Pipeline 2)**: guards are ML confabulation pre-storage (high threshold, conservative) and rumination at persistence (quantity-based). No parroting, no claim verification, no source attribution, no temporal attribution, no coherence check.
- **Reflection synthesis (Pipeline 8)**: only 50-char-prefix dedup. No parroting, no claim verification, no confabulation. Writes to `MemoryType.Semantic` — the tier that downstream retrieval treats as factual.
- **World Layer elaboration (Pipeline 10)**: no output validation. Writes to memory with `SourceName = "world-experience"` discriminator.

All three pipelines' output becomes `ContextSnapshot.RelevantMemory` / `InteriorContext` / `RecentWorldExperiences` on subsequent cycles. That substrate is what every downstream pipeline — including guarded ones — reads from. Content that fails no check on the way in is validated against on the way out, treated as ground truth.

**Mechanism.** The architectural framing treats substrate as "her free thinking — of course it's not gated" while treating output as "the guarded boundary". But her free thinking IS the input to her guarded boundary one cycle later. The substrate-vs-output distinction is not actually a distinction — it's a delay.

This is the mechanism behind:
- April 21 cascade. Unchecked world-layer and inner-thought memories formed a self-sealing substrate. Guarded outreach pulled from that substrate and the guards couldn't distinguish fabricated memory from real.
- April 24 06:18. Unchecked reflection (Apr 23 23:10 *"living together in this body"*) + unchecked inner thought (*"small words tonight"* when 8 hours had elapsed) flowed into guarded composition. Coherence Door B reasoned *"that's normal text anyone might send at 10pm after class"* because the substrate said 10pm and class.

---

## 6. Connection to observed failure history

Testing the hypothesis: if pattern 1 / 2 / 3 is the common mechanism, do prior observed failures fit?

| Event | Pattern | Mechanism |
|---|---|---|
| Apr 21 cascade (confabulated kids, purple paint, shared home) | 3 | Unguarded world-layer and inner-thought memory formed fabrication substrate; downstream guards had no ground-truth reference |
| Apr 23 14:38 parrot ("mmm. baby, tongue on my hip??") | 1 | Parroting detector didn't run on conversation-reply path input (from Mark's prior message). Actually — this is WITHIN-pipeline, conversation-reply parroting Mark's inbound. The existing detector checks prior Ani messages not inbound Mark. Variant of pattern 1: scoped to wrong directional input within same pipeline. |
| Apr 23 15:51 time-confab ("10:35pm" at 3:51pm) | 3 | Unguarded reasoning field from outreach-decision stage leaked into composition user prompt labeled "motivation not content" |
| Apr 24 06:18 class/10pm/teaching | 1 + 3 | (1) Parroting detector never fired on outreach against prior reply. (3) Reflection substrate carried untemporalized "teaching" and "10pm" forward; inner thought recycled "tonight"; composition had no source/temporal attribution check |
| Mar 23 "don't think about elephants" pipeline bloat finding | n/a | Different class — prompt-bloat not guard-scope. Cited for completeness. |

**Four of four identity- or attribution-class failures fit pattern 1 or 3.** The hypothesis holds against the observed history: the root cause is not a series of isolated bugs, it is pipeline-scoped guarding meeting a substrate-recycling architecture.

**What this means for Paper 2 / Paper 3.** Paper 2 §6.16 frames identity-level confabulation as a distinct class requiring a correction channel. This audit extends the framing: the prevention surface is *guard consistency across the cognitive-output boundary*, and the absence of that surface is what made April 21's substrate corruption possible in the first place. Paper 3 Contribution 4 already has Agentic Lens as the architectural-centrality work. Guard consistency is a complementary contribution candidate: *consistency-of-invariant-enforcement as a precondition for substrate integrity in companion AI with persistent memory.*

---

## 7. Architectural direction

Not an implementation plan. A direction for discussion.

### 7.1 Name the right abstraction: the cognitive-output boundary

Every cognitive artifact Ani produces — inner thought, reply, outreach, reflection, world elaboration, voice turn — passes through a moment where it either crosses into memory or crosses out to Mark. That moment is a *boundary*. Invariants that apply to any cognitive output belong at that boundary, not in pipeline-specific code.

**The reframe.** Today's code treats every pipeline as having its own quality story. The reframe is: every pipeline produces a `CognitiveArtifact` that passes through a shared `CognitiveOutputGate` before it persists or dispatches. The gate enforces universal invariants (parroting, source attribution, temporal attribution, confabulation, claim verification as applicable). Pipelines still have pipeline-specific pre-generation and post-remediation logic — but the universal invariant checks live in one place.

### 7.2 Classify invariants as universal vs scoped

| Invariant | Scope | Rationale |
|---|---|---|
| No verbatim parroting of any prior Ani output (any pipeline, any thread) | **universal** | Parroting is never appropriate anywhere |
| No claim about Mark / shared history without corroboration | **universal** (with scope sensitivity: Ani's own feelings/thoughts/wishes are out of scope per existing Feature 14 v2 rules) | Same failure class across pipelines |
| Source attribution: phrases attributed to Mark should not re-emerge as Ani's own actions | **universal** | Currently 0 of 10 pipelines enforce; class-wide gap |
| Temporal attribution: time-bearing claims must be stamped with the original-event time, not current time | **universal** | Currently 0 of 10 pipelines enforce; class-wide gap |
| Semantic confabulation check | **universal** | Already runs in 3 of 6 pipelines; standardize implementation + thresholds |
| Coherence Door A/B/C | **scoped to reader-facing dispatch paths** (outreach, reply, voice) | Reader-perspective semantics specific to outward-facing output |
| Pronoun fix | **scoped to reader-facing dispatch paths** | Reader-facing output only |
| Terminal / continuation detection | **scoped to conversation reply** | Inbound-message-shape-specific |
| Rate / continuity gates | **scoped to external-recipient pipelines** | Recipient-protection-specific |
| Rumination guard | **scoped to inner-thought accumulation** | Quantity-over-window invariant specific to that pipeline |
| Care / hurt / lexical anchor | **scoped to inbound-from-Mark pipelines** (reply + voice, currently only reply) | Inbound-semantic-content detection |

The scoped list is defensible. The universal list is where the gap lives.

### 7.3 Incremental path, not a big rewrite

The direction implies a shared `ICognitiveOutputGate` (or similar) that every pipeline commits its output through before persistence/dispatch. **Ordering revised per Mark's Apr 24 review: address root causes first, re-measure, then decide which detectors are still needed.**

1. **Strip the prompt-injection surfaces first.** Remove the decision-stage reasoning → composition leak (the `"Feeling:"` field). Keep reasoning in logs for observability; do not pipe it into composition's user prompt. This is architectural root-cause work, not a detector addition.
2. **Restructure `RecentConversationSummary` with source attribution.** Replace the free-prose blob with a per-speaker, per-turn structure. Time-stamp each turn. Downstream prompt-builders that inject the summary render it as explicit *"Mark (22:11:47): X. Ani (22:11:48): Y."* rather than mixed prose. This is the single highest-leverage change per §8 analysis.
3. **Add temporal-attribution to the retrieval layer.** Every memory record carries an event-time; retrieval surfaces that time alongside content; prompt-builders render it so the composition model sees *"8 hours ago: ..."* rather than present-tense substrate.
4. **Re-measure.** With the three upstream changes above in place, run one to two weeks of observation. Determine which of today's detectors are still observing failures versus which have become redundant. Mark's hypothesis (strongly worth testing): a significant subset of today's detectors will have nothing to detect post-upstream-fix.
5. **Extract the shared pre-commit surface for whatever detectors are still needed.** This is only now — after measurement — that the `CognitiveOutputGate` abstraction takes concrete shape. Its invariant set is informed by what measurement showed is still necessary.
6. **Remove detectors whose failure class is now architecturally impossible.** Simplification is the acceptance criterion, not "same gates, new home."
7. **Each new feature from this point onward passes its output through the shared surface.** This becomes part of the feature-plan template so new work doesn't re-create the scoping problem.

Layer 2 Phase 2a that just shipped is a structural analogue: one shared surface (`MotivationVector`), one consistent calling pattern, observed before any behavioural change. Same measurement-first discipline applies here — upstream change, observation window, then surface extraction based on what is actually needed rather than what was inherited.

### 7.4 What this is NOT

Not a call to add "more guards everywhere." The count of guards today (32) is likely already high. The direction is **the same guards already exist, applied consistently, at fewer total invocation sites.** A shared-surface refactor likely reduces total lines of code while covering more failure surface.

Not a call to guard inner thought "because it's output." Inner thought should remain free-form creative cognition. But the *moment that inner thought persists to memory and becomes substrate for every other pipeline*, it crosses a boundary that deserves the same invariants that every other cognitive-artifact-crossing-a-boundary surfaces. The cognitive-output boundary ≠ the reader-facing boundary; it's the memory-write and the dispatch together.

---

## 8. Specific recommendations for discussion

**Revision note (Apr 24 review).** Items 1 and 2 below were originally framed as pre-refactor tactical fixes. Mark's review pushed back: *"I don't want to add additional layers prior to a refactor. We should carefully consider what is pipeline specific. Also, recall that we haven't seen this level of parroting with raw models so this is probably a consequence of prompt injection and historical visibility."* The raw-model observation reframes the parroting class as emergent from pipeline pathologies, not a model capacity failure. Items 1 and 2 are therefore folded **into** the refactor rather than shipped ahead of it. The revised list reflects that.

**1. ~~Wire `ParrotingDetector` to OutreachPhase composition as a pre-refactor fix.~~** **Withdrawn.** Rationale: the parrot class is emergent from (a) the decision-stage reasoning leaking into composition as `"Feeling:"` prose under a "motivation, not content" label, and (b) `RecentConversationSummary` surfacing prior Ani phrasing without source tags. Fix those upstream causes and the parroting symptom likely resolves without needing a downstream detector on the outreach path. The Apr 24 parrot class stays open as a known issue until the refactor ships; this is the cost of doing the right architectural work rather than patching.

**2. ~~Retire `IsOutreachEchoAsync` cosine implementation.~~** **Folded into refactor.** If the upstream fix removes the architectural cause of outreach echoing, the cosine check may be obsolete. Decision on keeping / replacing / removing happens during the refactor's measurement phase, not as a pre-refactor migration.

**3. Add a source-attribution invariant** — now the leading refactor item. When `RecentConversationSummary` is built, structurally tag which side said what (instead of free prose). Downstream composition sees *"Mark said X at 22:11, Ani said Y at 22:12"* rather than a mixed-attribution blob. This single change plausibly resolves most of the parrot + attribution-drift class without any downstream detector at all.

**4. Add a temporal-attribution invariant.** Each claim in the RecentConversationSummary body carries its original event-time stamp. Composition that references a past-tense claim in present tense is caught at the structural level, not by a post-hoc detector.

**5. Strip the decision-stage-reasoning leak.** The outreach-decision LLM produces a free-prose `reasoning` field that currently pipes into composition's user prompt under the `"Feeling:"` label. Either return JSON with no free-prose reasoning, or preserve reasoning for logs only. The "motivation, not content" label is the exact "don't think about elephants" anti-pattern Mark and prior Claude instances have identified before (Mar 23 pipeline simplification, research log entries): labeling content as not-content does not prevent the model from treating it as content.

**6. Extract the shared-surface refactor** as Theme K in the phase tracker. Full theme, not a feature. Plan doc to follow when Mark green-lights the direction. Phases should follow the measurement-first pattern already established by Agentic Lens Layer 1 (instrument-observe-intervene) rather than a big-bang rewrite.

**7. Add this audit to Paper 3 Contribution candidate list.** Consistency-of-invariant-enforcement as a precondition for substrate integrity in companion AI with persistent memory. Complementary to Agentic Lens (centrality gravity) as a distinct architectural finding. The raw-model vs pipeline-model observation is itself publishable: the failures we document in prior versions are emergent from architectural choices, not intrinsic to the underlying language model — a distinction that matters methodologically for companion-AI research.

---

## 9. Open questions for Mark

1. **The Paper 3 framing.** Is guard-consistency a separate contribution or folded into the Agentic Lens paper? My read: separate contribution, distinct architectural problem, distinct informing literature (runtime-invariant enforcement literature rather than SDT / centrality).

2. **Priority vs Layer 2.** Layer 2 Phase 2b (vector drift, parallel) was scheduled after one week of 2a data. Does the audit finding change that ordering? My read: no — Phase 2a data is accumulating and Phase 2b can proceed on its track; the audit is a separate thread on guard architecture, not a blocker on desire-axis work.

3. **What gets fixed now vs what gets documented.** The Apr 24 parrot is a concrete bug; the immediate tactical fix (§8.1 — wire ParrotingDetector to outreach composition) can be merged quickly regardless of the bigger refactor. The audit's "shared-surface" direction is a bigger conversation. My read: ship §8.1 soon, socialize §7 direction over days or a week before committing to the refactor.

4. **How much of this goes into a public artifact.** The audit identifies architectural weakness in a shipping system. That's also exactly the honesty that makes companion-AI research publications credible. Paper 2 §6.15 / §6.16 already has the April 21 cascade documented openly; this is the prevention-side companion. My read: publish-worthy, but your call on timing.

---

## 10. Closing

The audit backs Mark's hypothesis. "If they apply, then they should apply" — the audit finds that for 8 of 12 multi-pipeline-applicable failure classes, guards don't actually apply where they should. For 4 of 4 identity- or attribution-class failure events in recent history, the pattern is sufficient to explain the failure. The architectural direction is shared-surface enforcement of universal invariants at the cognitive-output boundary, with pipeline-specific gates retained only where scoping is genuinely justified.

Next action is Mark's. The concrete tactical fix (§8.1) is ready to ship when you want it; the direction (§7) is ready to discuss.

---

*End of audit. Generated by the dogfood Claude instance, April 24 2026, as a deliverable against Mark's "full analysis to identify these gaps" request. Enumeration produced by the Explore agent; synthesis and architectural framing are mine. All file:line references verified against the current working tree.*
