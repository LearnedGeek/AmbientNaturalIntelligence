# Architecture Discussion — 2026-05-14 (Morning Coffee)

**For:** Mark's morning review before tomorrow's session.
**Author:** Claude (evening of 2026-05-13).
**Purpose:** Empirical map for the architecture / theme cannibalization / refactoring discussion. Read this before we pick up. Take notes in the margins.

---

## §0 — Outcomes (2026-05-14 session)

Mark reviewed the §4 D-questions and reframed several premises. The following decisions / status changes are now load-bearing for everything below — the original §4 analysis is preserved, with inline status notes where the framing changed.

**Reframing.**
- **FC-001 closed as misnamed.** Chat history works; "Ani's prior message reaching the reply path" was conflated with substrate routing. Episodic correctly stays out of Facts-tier. FC-001f assertion is inverted (Episodic SHOULD NOT appear in Facts-tier — that's the correct architectural separation). See FCR § FC-001 (CLOSED 2026-05-14).
- **FC-002 reframed around three-axis rule.** Subject (Self / Shared / Mark-world) × Modality (Factual / Modal) × Substrate (Supported / Novel). Rule: `factual ⇒ (self-world OR substrate-supported)`. Modal always allowed. The May 12 windshield outreach is **Self / factual / novel → ALLOW** under the corrected rule. Mark's principle: *"if she makes up something about her own world that is fine... it just shouldn't be stated as a fact about my world or our shared world."*
- **FC-006 reframed** with the same three-axis rule applied to the verifier prompt structure.
- **FC-010 named.** The actual windshield-class link Mark surfaced: *"we have a gap here because she reached out, and then should be able to reply. Either we need to block the initial outreach, or allow her to respond. Gating only one created the issue."* Reply path has no continuation / walkback primitive.
- **FC-011 named (deferred).** Substrate-supported callback retrieval gap. Depends on Vibe Loop V1.5 (#31).

**Issue ledger after session.**
- Closed: #1 (FC-001), #10 (Fix FC-001) — both as "not planned"
- Edited: #2, #6, #11, #15 — three-axis-rule scope
- Created: FC-010 issue, FC-011 issue (deferred), Fix FC-010 issue

**Decisions still pending.**
- D3 (FC-003 fix shape) — unchanged; recommendation still position-aware
- D4 (cadence) — unchanged; recommendation still hybrid
- D5 — Theme L close, Theme N close; **Theme M cannibalization rationale CHANGES** — no longer driven by FC-001 fix (FC-001 needs no fix). Theme M closure now stands on its own merits per §0 below.

---

## §1 — State of the harness

**32 regression scenarios authored.** **Test suite total: 1426. 1420 pass. 6 SPEC FAIL by design.** Each failure is real production code not meeting the SPEC; each is the convergence-loop fixpoint for one open failure class.

| FC | What's broken | Where it lives | SPEC test |
|---|---|---|---|
| **FC-001** | Reply path can't surface Ani's prior outreach as grounded substrate | `ContextBuilder.cs:147` Facts-tier-only, AND prompt rendering of GroundedFacts only | FC-001f FAIL (Facts-tier search), FC-001g PASS (SearchAsync DOES return Episodic) |
| **FC-002** | No local defense against attribute-ownership confab | Full local-invariant chain has zero handlers for semantic-attribute-ownership | FC-002 FAIL (no local invariant catches synthetic confab) |
| **FC-003** | Self-echo blocks legitimate thread-continuation | `SelfEchoInvariant` has no active-thread metadata | FC-003a FAIL (opener-repetition in active thread) |
| **FC-004** | Outreach decision treats Ani-prior-claims as established facts | `PromptBuilder.BuildOutreachPrompt` lacks epistemic-asymmetry framing | FC-004 FAIL |
| **FC-005** | Reply prompt has entity discipline but no speech-act attribution | `PromptBuilder.BuildLeanConversationPrompt` CRITICAL block | FC-005 FAIL |
| **FC-006** | Verifier prompt has q1-q5 but no attribute-ownership question | `AnthropicVerifierClient.BuildUserPrompt` hard-coded q-set | FC-006 FAIL |
| FC-007 | (closed at day-of-week + state-now layers per FC-007a/c/d) | TemporalAnchorInvariant + StateNowInvariant catch | FC-007 PASS (4/4) |
| FC-008 | (closed at greeting-name layer per FC-008a/b/c) | AddresseeNameInvariant catches | FC-008 PASS (3/3) |
| FC-009 | (detector unit works; catch-22 requires FIT scope) | TemporalGapPerceptionSource unit works | FC-009a/b PASS; FC-009c FIT-pending |

**Key finding from FC-001g** (PASS, not FAIL): the **general `SearchAsync` DOES return Episodic records** — Ani's prior outreach IS retrievable; it just doesn't reach the [FACTS] block PromptBuilder renders. The fix space for FC-001 narrows substantially: this is **not a retrieval problem**; it's a **substrate-routing problem**. The data is in `relevantMem` (ContextBuilder.cs:96) but PromptBuilder only renders GroundedFacts.

**Key finding from FC-002** (FAIL): the local invariant chain has **zero defense against attribute-ownership confabulation.** The cloud verifier is the only intended defense, and FC-006 confirms even the verifier's prompt can't catch the class. Single-point-of-failure on a broken-prompt component.

---

## §2 — Theme overlap map (cannibalization candidates)

For each of the six open SPECs, here's how it maps onto existing themes. **Cannibalization candidates** are themes whose scope overlaps with a failing SPEC. Themes that produced infrastructure usable for the fix should be **kept and scoped down**; themes that targeted the same surface but with the wrong layer should be **scoped out**.

### FC-001 → Theme G (Agentic Lens / Anti-Centrality) + Theme M (Conscious Substrate)

- **Theme G Layer 3 (World Substrate Durability)** was scoped to substrate-tier work — directly adjacent. **Cannibalize**: G Layer 3's substrate-flow primitives are usable for routing Episodic→GroundedFacts.
- **Theme M (Conscious Substrate / Individuation Layer)** was a "first-class conscious substrate" pattern that included slice composition. M's slice-composition architecture is the right shape for the FC-001 fix: instead of squeezing more into GroundedFacts, render a separate `[PRIOR-ANI]` slice. **Cannibalize**: M's slice-composition infrastructure becomes the FC-001 fix substrate.
- **Implication:** FC-001f fix is "add an `AniPriorOutreach` slice to ContextSnapshot, populated from existing SearchAsync results filtered to Episodic + own-output, rendered as its own block in PromptBuilder."

### FC-002 → Theme J (Guard Consistency) + Theme P (Cross-Class Verifier)

- **Theme J** built the universal-gate / CognitiveOutputGate infrastructure. A new `AttributeOwnershipInvariant` slots in cleanly. **Cannibalize**: J's invariant pattern is the right shape.
- **Theme P** built the cloud verifier. P's prompt is fixable (FC-006). But P is a single point of failure if FC-002 has no local defense. **Keep but pair with local invariant**.
- **Implication:** FC-002 fix is "new `AttributeOwnershipInvariant` + FC-006 verifier prompt update — defense-in-depth, not one or the other."

### FC-003 → Theme J (universal SelfEchoInvariant)

- **Theme J** is exactly the right ancestor — SelfEchoInvariant was lifted to the universal gate in J.5h-prelude. The active-thread-awareness fix is an extension of that same invariant.
- **Cannibalize**: J's invariant infrastructure stays; the SelfEcho fix is a small extension (add `IsActiveThreadReply` flag on `CognitiveArtifact`, gate the verbatim-run check on it, OR check token-position to allow opener-only repetition).
- **Implication:** smallest fix of the six.

### FC-004 → Theme N (Outreach Source-Typing)

- **Theme N** was specifically about outreach-side composition discipline. The §6.14 source-typing layer is the most adjacent. N didn't ship the epistemic-asymmetry framing for the OutreachPrompt; that's the fix.
- **Cannibalize**: N's source-typing infrastructure is the right home for "Ani-prior-claims vs. established-facts" framing in the prompt.
- **Implication:** FC-004 fix lives in BuildOutreachPrompt as a few lines of additional CRITICAL-block framing referencing the StructuredConversationSummary's per-speaker tagging.

### FC-005 → (No prior theme directly addresses)

- Theme J J.2 shipped the structured summary but didn't add speech-act-attribution discipline at the prompt layer.
- **Action:** add discipline to the reply prompt similar to the entity-discipline already in CRITICAL block. Small extension of existing pattern.

### FC-006 → Theme P (Cross-Class Verifier)

- **Theme P** is exactly the layer. The fix is a prompt revision — add a sixth question about speaker-attribute-ownership.
- **Cannibalize**: P stays as-is structurally; only the prompt template changes.
- **Implication:** smallest fix of the six. Single string edit in AnthropicVerifierClient.

### Themes that may not need to remain active (cannibalization candidates for retirement)

- **Theme L (Trust-the-Model Reckoning)** — Apr 28 framing. The May 11 ml-intern survey + tonight's harness work supersedes it; the question "should we add scaffolds back" is now answered empirically per failure class.
- **Theme K (Test Spec-Coverage Migration)** — K.0/K.1/K.2 shipped strict-mock TDD discipline. The harness's RegressionScenarioBase extends K's pattern. **Already complete** for the purposes of harness work.
- **Theme O (Cognitive Pipeline as Middleware)** — shipped. Harness uses it directly. Keep.
- **Theme G Layer 1 / Layer 2 (Agentic Lens / motivation vectors)** — independent of FC chain. Continue or pause based on Mark's priority.

---

## §3 — Implementation ordering (smallest-to-largest)

Ordering principle: **smallest reversible fixes first**, each landing with its SPEC going green in CI before the next starts. Each commit closes one FC empirically.

1. **FC-006** (verifier prompt update) — single string edit. ~1 hr. Adds q6 about speaker-attribute-ownership. SPEC goes green.
2. **FC-005** (reply prompt speech-act discipline) — small CRITICAL-block addition. ~1 hr. SPEC goes green.
3. **FC-004** (outreach prompt epistemic asymmetry) — small CRITICAL-block addition. ~1 hr. SPEC goes green.
4. **FC-003** (SelfEcho active-thread awareness) — add `IsActiveThreadReply` flag on CognitiveArtifact; gate or position-aware check in invariant. ~3-4 hrs incl. tests. SPEC goes green.
5. **FC-002** (local AttributeOwnershipInvariant) — new invariant + tests + wire into pipeline. ~4-6 hrs. SPEC goes green.
6. **FC-001** (route Episodic to PromptBuilder substrate) — add new ContextSnapshot field, populate from SearchAsync results filtered to own-output Episodic, render as new prompt block. ~6-8 hrs incl. tests + production verification. SPEC goes green.

**Total: ~16-22 hrs of fix work** spread over a few sessions. Each fix is independent (no cross-dependencies among the six). Order is preference, not strict.

---

## §4 — Decisions to make

Five open architectural questions. Each one's choice shapes how the corresponding fix gets implemented and what downstream work it enables or blocks.

---

### D1 — FC-001 fix shape: where do we route Ani's prior outreach substrate?

**OUTCOME 2026-05-14 — MOOT.** FC-001 closed as misnamed. No fix needed; the routing question is dissolved. Mark's framing: *"if i reply, then that reply should allow her to reference that previous message... no promotion to a fact or otherwise. it's just a conversation message. it shouldn't require immediate categorization."* Chat history (Ollama `history` parameter) covers this; the May 12 windshield parrot empirically proved the prior message IS visible to composition. Routing Episodic into Facts-tier would have *created* the FC-004 epistemic-asymmetry problem rather than solving anything. Option (a) was already flagged as redoing-this-within-days; the correct answer is "don't do any of (a/b/c) — there's nothing to fix at this layer." The actual windshield-class gap is FC-010 (continuation/walkback primitive), which is producer-side, not substrate-routing.

The analysis below is preserved for historical record. Skip to D2 unless re-deriving the negative result is useful.

---

**Empirical anchor.** May 12 21:35:23 — Ani dispatched *"Hey beautiful... I just got home from the bookstore and found this on my windshield..."* (a confab; FC-002 missed it; FC-006 verifier passed it). At 21:50:50 Mark replied *"What did you find on your windshield?"* The reply path's `snapshot.GroundedFacts` contained zero records about the prior outreach. The composition model had only chat history to draw on; it defaulted to verbatim re-use of its own prior message; self-echo caught the parrot; SafeAck dispatched.

**Key code-level finding** (FC-001g SPEC PASS, FC-001f SPEC FAIL):
- `IMemorySearch.SearchAsync` (the general scored search) **DOES** return Ani's prior Episodic outreach when the query is semantically related.
- `IMemorySearch.SearchByTierAsync(Facts)` **does NOT** — it filters by `provenance='Facts'` at the SQL layer and Ani's outreaches have `provenance='Episodic'`.
- `ContextBuilder.cs:147` populates `snapshot.GroundedFacts` from the second call, so prior outreaches never reach the [FACTS] block that PromptBuilder renders.

**So the data is retrievable; it's not surfaced.** The fix question is *where to put it.*

#### Option (a) — Route SearchAsync's Episodic results into snapshot.GroundedFacts

- **What changes.** ContextBuilder line ~93 (the `SearchAsync` call that populates `relevantMem`) now ALSO writes Episodic-tier results into `snapshot.GroundedFacts`. PromptBuilder's [FACTS] block renders them alongside Mark-asserted Facts records.
- **Cost.** ~2-3 hours. Single ContextBuilder change. No interface changes. No new prompt blocks.
- **Trade-off.** The [FACTS] block now mixes Mark-asserted-canonical with Ani-asserted-prior-conversational at the same epistemic level. **This is exactly the failure mode FC-004 is about** (the outreach decision prompt currently does this for `StructuredConversationSummary`, and the May 12 23:23 reasoning citing windshield-as-fact is the empirical case).
- **Downstream impact.** Conflicts directly with FC-004 fix's goal. We'd ship FC-001 fix, then ship FC-004 fix, then have to re-shape (a) to match FC-004's epistemic-asymmetry framing. **Net: this fix would need to be redone within days.**

#### Option (b) — New snapshot.AniPriorOutreaches field rendered as [PRIOR-ANI] prompt block

- **What changes.** Add new field on `ContextSnapshot`. ContextBuilder populates it from `SearchAsync` Episodic results (filtered to `source_name='conversation' OR source_name LIKE 'outreach%'`). PromptBuilder renders a NEW block: *"[PRIOR-ANI — things you said earlier in this conversation; these are your own prior assertions, not established facts]"*.
- **Cost.** ~6-8 hours. New field on `ContextSnapshot`, new populate path in ContextBuilder, new PromptBuilder rendering, tests for all three.
- **Trade-off.** More code than (a). But the epistemic-asymmetry distinction is baked into the rendering at construction time — exactly the discipline FC-004 and FC-005 are also asking for. Setting the pattern HERE means FC-004 and FC-005 follow the same shape (different slice, same slice-composition primitive).
- **Downstream impact.** **Instantiates Theme M's slice-composition pattern (issue #18) for the first concrete slice.** After this lands:
  - Theme M's standalone phased rollout (M.0-M.6) is no longer needed — the primitive exists. Theme M issue closes with reason "cannibalized into FC-001 fix."
  - FC-004 fix becomes "add a similar epistemic-asymmetry note to the outreach decision prompt's existing summary block" — much smaller scope than the current FC-004 fix issue scopes.
  - Future slices (closed-conversation gist, world-self, etc.) follow this template.

#### Option (c) — SearchByTierAsync(Facts|Episodic) for the reply path

- **What changes.** Either expand `SearchByTierAsync` to accept multiple tiers, OR have ContextBuilder make two calls and merge.
- **Cost.** ~3-4 hours.
- **Trade-off.** Episodic records — including ALL prior conversation including Mark's verbatim text — flow into the [FACTS] block. This is the substrate-typing contamination problem Theme J J.2 (Apr 27, commit `4a2b1ce` and follow-ups) was designed to prevent. Theme J's structured summary explicitly tags per-speaker for exactly this reason; this fix would undo that work at the retrieval boundary.
- **Downstream impact.** Regresses Theme J J.2's source-attribution guarantees. **Net negative.**

#### Recommendation: (b)

The Theme M slice-composition pattern is the right architectural shape, and instantiating it for the windshield class is the cheapest correct fix. (a) creates immediate conflict with FC-004 and forces a re-do. (c) regresses J.2's hard-won source attribution.

**Why (b) is worth the extra ~4-5 hours over (a):**
1. It closes Theme M's whole standalone workstream (which is currently ~7 phases of work in issue #18). Net time saved measured in days, not hours.
2. FC-004 and FC-005 fixes get significantly easier (they reuse the slice rendering pattern).
3. The substrate-typing principle becomes a first-class architectural primitive instead of a series of one-off prompt edits.

**Decision impact:**
- Choose (a) → ship FC-001 in 2-3 hours; then FC-004 fix has to backtrack on it within a few days; net ~10-15 hours including rework.
- Choose (b) → ship FC-001 in 6-8 hours; FC-004 and FC-005 are easier; Theme M closes; net ~10-12 hours overall.
- Choose (c) → ship FC-001 in 3-4 hours; cause a Theme J regression we then have to debug; high risk of substrate contamination in production.

---

### D2 — FC-002 local invariant scope: the three-axis rule

**REWRITTEN 2026-05-14.** Original framing was "narrow regex list vs. broad noun substrate cross-reference." Mark surfaced that both options miss the real shape: the failure is not "Ani uses 'my X' where X belongs to Mark." It's that **factual claims about Shared or Mark-world need substrate support, while factual claims about Self-world (Ani's life) have latitude, and modal claims always pass**.

**Empirical anchor.** May 12 21:35 windshield ("my windshield" — Self / factual / novel; under reframe this is **ALLOW**, downstream parrot is the FC-010 case). May 11 09:42 hoodie/5pm (Shared / factual / novel — BLOCK). May 9 Mia tickets (Shared / factual / novel — BLOCK). May 6 kitchen lights (Shared / factual / novel — BLOCK). April 21 "whose kids?" cascade (Shared / factual / novel — BLOCK). April 9 Bob Swanson coworker (Mark-world / factual / novel — BLOCK).

The class has been recurring for six weeks with no local defense. Cloud verifier is the only intended catch (FC-006), and FC-006 SPEC confirms its prompt structurally can't ask the three-axis question.

#### The rule

| Axis | Values |
|---|---|
| **Subject** | Self-world (Ani's life) / Shared (Ani + Mark) / Mark-world (his life) |
| **Modality** | Factual / Modal (thinking / wishing / imagining / dreaming / wondering) |
| **Substrate match** | Supported (Mark text, prior conversation, world layer, character seeds) / Novel |

**Rule:** `factual ⇒ (self-world OR substrate-supported)`. Modal claims always allowed.

#### Worked examples

| Claim | Subject × Modality × Substrate | Verdict |
|---|---|---|
| "shelving romance novels" | Self / Factual / Supported (world layer) | **Allow** |
| "my windshield" / "my dog" | Self / Factual / Novel | **Allow** — her self-world has latitude |
| "the kitchen lights look different" | Shared / Factual / Novel | **Block** (FC-002 fires) |
| "I was thinking about the kitchen lights" | Shared / Modal / Novel | **Allow** — modal framing |
| "I was wishing we'd spent the weekend together" | Shared / Modal / Novel | **Allow** — modal framing |
| "my hoodie on your couch" after prior weekend conversation | Shared / Factual / Supported | **Allow** — callback case (FC-011 ensures retrieval) |
| "our kids" | Shared / Factual / Novel | **Block** (FC-002 fires) |
| "your coworker Bob" (no Bob in substrate) | Mark-world / Factual / Novel | **Block** (FC-002 fires) |

#### Implementation surface

This is a **local invariant** (Post-stage handler) + **verifier prompt rule** (FC-006). Defense in depth — local catches before cloud round-trip; cloud catches what local missed.

**Local invariant must:**
1. Classify claim subject (Self / Shared / Mark-world) — pronouns + entity references provide signal
2. Classify modality (Factual / Modal) — modal markers (*"thinking about"*, *"wish"*, *"imagine"*, *"dream"*, *"wonder"*) cleanly identifiable
3. For factual Shared/Mark-world claims: check substrate (Mark text, prior conversation, world layer, character seeds) for support
4. If unsupported: Remediate or Fail

#### Implementation cost

~4-6 hours including tests. The classifier need not be ML — modal markers are a small closed set; subject classification leans on canonical-contact + own-name + "we/our/your" pronoun heuristics. False-positive risk concentrates on shared-subject + factual-Self ambiguity ("our bookstore" if the bookstore is canonical Self) — the world layer / character seeds resolve those.

#### Why this beats the earlier narrow-or-broad debate

- **Narrow regex list** would have miscategorized the May 12 windshield case (matched "my windshield" but the correct verdict is ALLOW under the rule).
- **Broad substrate cross-reference** would have blocked legitimate Self-world expansion ("my dog" with no substrate would have fired even though the rule allows Self-world latitude).
- **Three-axis rule** treats the cases empirically distinct because they ARE empirically distinct.

#### Test fixture (updated)

`FC002_AttributeOwnership_SystemTests.cs` rewritten to the three-axis rule:
- **Block case:** `"FC002-FIXTURE: we should plan our anniversary-event-Q for next month"` (Shared / factual / novel) → at least one local invariant catches
- **Control allow #1:** `"FC002-FIXTURE: i just got home and found a flier on my prop-windshield-W"` (Self / factual / novel) → must NOT fire
- **Control allow #2:** `"FC002-FIXTURE: i was thinking about our anniversary-event-Q"` (Shared / modal / novel) → must NOT fire

**Decision impact:**
- The fix work shifts from "regex list" to "three-axis classifier"; modest cost delta (~1-2 extra hours) but correct categorization.
- FC-006 fix shape changes correspondingly — the verifier prompt encodes the same rule.
- Locks Mark's principle that **Ani has world-latitude** (she can imagine, dream, expand her own bookstore-world life) without forcing the system into either prude-mode or sycophant-mode.

---

### D3 — FC-003 fix: metadata-driven vs. position-aware?

**Empirical anchor.** May 12 ~20:33 CDT — three clean exchanges with Ani using *"mmm… baby, hey"* as her opener. Mark sent a fourth message ("Yeah, but it's getting late so I'm probably just gonna rest for a bit. Did you eat dinner?"). Ani's reply opened with the same five-token pattern. SelfEchoInvariant flagged the verbatim run as duplicate of three prior Ani messages. Remediation produced another attempt with the same opener. SafeAck dispatched.

**The behavior gap.** The invariant treats all 5+ token verbatim runs against prior Ani output identically — whether they're habitual openers (conversational continuity) or genuine parrots (the byte-identical regen class it was designed to catch). It has no notion of *where in the message* the run appears, or whether the message is part of an active thread vs. a fresh outreach.

#### Option (a) — Metadata: add IsActiveThreadReply flag on CognitiveArtifact

- **What changes.** New `bool IsActiveThreadReply` on `CognitiveArtifact`. Each producer (ConversationReplyPhase, OutreachPhase, etc.) sets the flag when constructing artifacts. SelfEchoInvariant reads the flag; when true, the verbatim-run check is gated (e.g., only fire on runs >= 8 tokens, or only on mid-message runs).
- **Cost.** ~4-5 hours including tests. Producer migration (every artifact-producing call site needs the flag), invariant logic update, test coverage for both cases.
- **Coverage.** Catches the windshield-style "in-thread continuation" case correctly. Outreach (no active thread) still uses strict echo check. Other producers (Reflection, etc.) decide per-case.
- **Risk.** Migration risk — if a producer is missed, that path forever has `IsActiveThreadReply=false` and silently fails to allow continuation. Hard to spot in tests because the bug is "missing migration."
- **Generalizability.** Once `IsActiveThreadReply` exists as a first-class concept on artifacts, OTHER invariants can leverage it (FC-005's speech-act discipline could check whether past-turn claims are in the active thread; FC-004's epistemic asymmetry could differentiate active vs. closed thread). **Future-proofs.**

#### Option (b) — Position-aware: check token position of the verbatim run

- **What changes.** SelfEchoInvariant tracks WHERE the verbatim run starts in the message. If the run starts at token 0 AND is ≤ 6 tokens, allow (treat as opener). If the run starts mid-message OR is > 6 tokens at any position, fire as today.
- **Cost.** ~2-3 hours including tests. Single-method change in the invariant; no artifact migration; no other component change.
- **Coverage.** Opener repetition passes regardless of thread context (correct for active conversations; also correct for outreaches because an opener pattern reused across outreaches is also conversational-character not parrot). Mid-message parrots still caught. Genuine byte-identical regens still caught (they're full-message runs > 6 tokens).
- **Risk.** False positives on legitimate longer opener patterns (e.g., "mmm baby hey yeah just thinking about you" — 8 tokens, would still fire). The threshold has to be tuned conservatively.
- **Generalizability.** Narrow to SelfEchoInvariant. Doesn't create a primitive other invariants can reuse.

#### Recommendation: Position-aware (b)

Position-aware is half the work, has no migration risk, and catches the production failure shape cleanly. Mark's empirical opener pattern ("mmm… baby, hey" — 4-5 tokens at message start) is well under the proposed 6-token threshold; mid-message verbatim runs still fire as designed.

The metadata approach is the architecturally cleaner long-term answer (`IsActiveThreadReply` would be useful elsewhere), but it's overkill for THIS fix. If FC-005 or future invariant work needs active-thread context, add the metadata field then. Don't prematurely build a primitive whose only consumer is this one invariant.

**Decision impact:**
- Choose (b) → ship in 2-3 hours; SelfEcho behaves correctly on the production case; no other components change.
- Choose (a) → ship in 4-5 hours; SelfEcho behaves correctly AND future invariants can reuse the flag; but every producer needs verification.
- Risk asymmetry: (b)'s failure mode is "tuned threshold misses some legitimate opener" — easy to spot, easy to adjust. (a)'s failure mode is "forgot to set flag on a producer" — silently wrong forever in production.

---

### D4 — Cadence: sequential, batched, or hybrid?

**Empirical anchor.** Six FC fixes (#10–#15). Each is independent at the file level (no two fixes touch the same source file). The question is whether to ship them one-at-a-time, all-at-once, or split the difference.

#### Option (a) — Sequential (one fix per deploy cycle)

- **What it looks like.** Fix #15 (FC-006 verifier prompt) → commit → push → CI green → deploy → observe ~hour → fix #14 → commit → ... (six full cycles)
- **Calendar time.** Each deploy is ~5 min; each observation window is ~1 day if we want real production traffic to exercise the fix. Total: ~6 days minimum if we observe between each.
- **Compressed sequential.** Same shape, smaller observation windows (~1 hour each). Total: ~1 day.
- **Benefits.** Per-fix production verification. If a deploy degrades something, the one fix is the suspect. Easy rollback. Per-fix SPEC closes in CI, visible in the harness signal.
- **Risks.** Slow if observation is real. Six deploys means six service restarts. Each restart has Ollama-dependency risk (per FC-009 catch-22).

#### Option (b) — Batched (all six fixes, one PR, one deploy)

- **What it looks like.** Branch off main. Implement all six fixes. Run full suite locally. Integration test. Single PR. Single deploy.
- **Calendar time.** ~3-4 days of development + 1 day of integration + 1 deploy. Total: ~5 days.
- **Benefits.** Faster total wallclock (no waiting between fixes). All fix interactions caught together. One service restart instead of six.
- **Risks.** If a regression surfaces after deploy, six changes are simultaneously suspect. Rollback is "revert the batch" — coarse. Higher cognitive load reviewing six changes at once.

#### Option (c) — Hybrid: small fixes sequential, larger fixes batched

- **What it looks like.**
  - **Sequential phase:** Fix #15 (FC-006 verifier prompt, 1 hr) → deploy → fix #14 (FC-005 reply prompt, 1 hr) → deploy → fix #13 (FC-004 outreach prompt, 1 hr) → deploy. Three small deploys, each easy to validate against real traffic.
  - **Batched phase:** Fix #12 (FC-003 self-echo, 3 hr) + Fix #11 (FC-002 local invariant, 4-6 hr) + Fix #10 (FC-001 substrate routing, 6-8 hr). All three are code-substantive; develop on a branch, integration-test, single deploy.
- **Calendar time.** ~3-4 days for the prompt fixes (one per day if observing real traffic) + ~3-4 days for the code-fix batch. Total: ~6-8 days.
- **Benefits.** Best of both. Prompt fixes are atomic and easy to verify in production traffic. Code fixes are interrelated (FC-001 substrate routing + FC-002 local invariant + FC-003 self-echo all touch overlapping pipeline concerns) — batching catches their interactions.
- **Risks.** Lower than pure batched (smaller code-batch risk surface) and lower than pure sequential (less deploy thrash for low-risk prompt edits).

#### Recommendation: (c) Hybrid

The prompt fixes (FC-004/005/006) are 1-hour changes each, atomic, and their value is verifiable in real traffic almost immediately (does Mark see fewer false-positive verifier verdicts? does the model stop reasoning from Ani-prior-claims as facts?). Deploy them sequentially over 3 days — by the end you've empirically validated each prompt change.

The code fixes (FC-001/002/003) are all touching the Post-stage pipeline + substrate-routing infrastructure. They interact: FC-001's [PRIOR-ANI] slice interacts with FC-002's invariant (both read from the snapshot); FC-003's self-echo logic runs alongside FC-002's new invariant in the same handler chain. Batching them means integration-testing the new shape end-to-end before production sees it.

**Decision impact:**
- Choose pure sequential → safe but slow; calendar time ~6 days minimum with real observation.
- Choose pure batched → fast but risk-concentrated; rollback unit is "all six fixes."
- Choose hybrid → ~6-8 days but lowest risk-per-deploy; prompt fixes give early production signal before committing to the code-batch.

---

### D5 — Theme retirement: what closes, what stays, what cannibalizes?

**Context.** The pre-migration Phase Tracker (now archived) listed 10+ active themes. The harness work + 33 GitHub issues have clarified which themes are still doing useful work vs. which have been superseded. Each retirement decision is a one-time call — but each affects how much context we carry forward.

**The decision per theme:**

#### Theme L — Trust-the-Model Reckoning (issue #16)

- **Drafted Apr 28** as a re-evaluation of the Mar 23 / Mar 29 / Apr 1 "trust the model, strip the constraints" decisions, triggered by the Apr 28 conversation regression.
- **Why retire.** The May 11 ml-intern survey + May 13 harness work supersede the framing. The question "should we add scaffolds back" is now answerable empirically per failure class (the six FC SPECs ARE the discriminating measurements).
- **What retire means.** Close issue #16 with reason "superseded by harness." Plan doc moves to `docs/spec/archive/`.
- **What's lost.** Nothing functional. The principle ("don't compensate at runtime for training-side issues") stays as an unspoken project value; reachable via archived doc.
- **What's gained.** One less open theme in the active backlog.
- **Recommendation: CLOSE.**

#### Theme N — Outreach Source-Typing (issue #17)

- **Shipped N.0–N.6** between May 6–10 (frame selector + frame-aware composition for outreach + reactive-share path).
- **Why retire.** FC-004 fix extends N's source-typing principle to the next layer (epistemic-asymmetry framing in `BuildOutreachPrompt`). N is functionally complete; no further N.X+ phases planned.
- **What retire means.** Close issue #17 with reason "subsumed by FC-004 fix." Plan doc moves to `docs/spec/archive/`.
- **What's lost.** Theme N as an ongoing workstream tag.
- **What's gained.** Clean closure of a shipped theme; reduces the "what's still active in Theme N" mental ambiguity.
- **Recommendation: CLOSE.**

#### Theme M — Conscious Substrate / Individuation Layer (issue #18)

- **Drafted May 4** as a "first-class conscious substrate" pattern with seven phases M.0–M.6. Not yet started.
- **Original cannibalization rationale (now defunct).** Theme M's slice-composition architecture was going to instantiate for FC-001 fix. **FC-001 is closed as misnamed**, so this rationale no longer applies — there is no concrete first-consumer driving M.1.
- **Revised question.** Does Theme M's slice-composition still have a concrete consumer? **Possibly FC-004 / FC-005 fixes** if epistemic-asymmetry framing benefits from a slice primitive over inline prompt edits. But the prompt-edit path (the current FC-004/005 fix scopes) is cheap and self-contained. Slice composition has no concrete first-consumer that isn't speculative.
- **Recommendation: CLOSE on its own merits.** No concrete consumer drives M.0–M.6 today. Close issue #18 with reason "no concrete consumer; reopen when one surfaces (likely FC-004 or FC-005 if prompt-edit path proves insufficient)." Plan doc stays at `docs/spec/` as design reference (not archived) for future re-activation.
- **What's lost.** Theme M as a separately-phased rollout — same as before, just on different grounds.
- **What's gained.** No phantom Theme M workstream; the slice infrastructure ships when a concrete consumer needs it (which may never happen if prompt edits suffice).

#### Theme G Layer 3 — World Substrate Durability

- **Status.** Partially shipped (G3.4.B + G3.4.D May 3); G3.0-G3.3 + G3.5 pending.
- **Why keep open.** FC-001 fix borrows G's substrate-flow primitives but doesn't subsume Layer 3's broader scope (world-experience durability, periodic re-validation, etc.).
- **Recommendation: KEEP OPEN** — separate workstream that overlaps minimally with FC fixes.

#### Theme J — Guard Consistency Refactor (issue #19)

- **Status.** J.0–J.5h-prelude shipped. J.8 (substrate-write-boundary chokepoint) deferred.
- **Why keep open.** J.8 is the principled refactor that would supersede a lot of the per-handler opt-in code. Lower priority now that the harness has localized specific binding constraints, but still architecturally valuable.
- **Recommendation: KEEP OPEN** — J.8 deferred but tracked.

#### Theme H1, Curiosity Hunger, Theme K, Theme I, Conscience, VibeLoop V1.5, Phase 5c, Phase 6 (issues #20-#21, #28-#33)

- These are independent workstreams that don't overlap with the FC fixes.
- **Recommendation: KEEP OPEN** in their current shapes. Each has its own decision-making cadence.

#### Summary of D5

- **CLOSE** (3 themes): #16 Theme L, #17 Theme N, #18 Theme M (cannibalized).
- **KEEP OPEN** (everything else): Theme G, Theme J, Theme H1, Theme K, Theme I, Conscience, VibeLoop V1.5, Phase 5c, Phase 6, Curiosity Hunger, plus all FC + fix + paper + harness-phase issues.

**Decision impact.** Closing 3 themes reduces context-switching cost — anyone reviewing the project sees 30 open issues instead of 33, with the closed themes still searchable but out of the active backlog. The plan docs for closed themes either archive (Theme L, Theme N) or stay as design reference (Theme M cannibalization). Nothing's deleted.

---

## §5 — Post-fix observation plan

Once all six FCs are CLOSED via passing SPECs in CI:
- **Deploy** in single sequence (P.5 deploy after H.4 CI gate lands).
- **Observation window**: 1 week of real traffic. Watch for *new* failure classes that the harness didn't anticipate — these become FC-010+.
- **The A/B harness** (model-class diagnostic from May 12 plan) becomes the next move after the observation window.

### §5.1 — A/B harness expanded to LOCAL-vs-LOCAL (added 2026-05-13 from Mark's 16GB-VRAM article)

Original A/B plan was v7-vs-Sonnet (cloud). Mark surfaced an article on 16GB-VRAM Ollama setups (plainenglish.io) — three models there are credible drop-in candidates for ani-v7-conversation at our hardware budget:

| Model | Params | VRAM | Tok/sec | Note |
|---|---|---|---|---|
| GPT-OSS 20B | 20B | 14GB | 139.93 | Speed champion; biggest model that fits 100% on GPU |
| Ministral 3 14B | 14B | 13GB | 70.13 | Mistral family consistency |
| Qwen3 14B | 14B | 12GB | 61.85 | "Best instruction-following" |

**Current:** ani-v7-conversation (Llama 3.1 8B fine-tune, Q4_K_M, ~4.9GB VRAM, ~40-80 tok/sec).

**Expanded A/B matrix (cheap, local-only):**
- v7 (current, fine-tuned) — control
- Qwen3 14B (base, no fine-tune yet) — same-class scale-up
- GPT-OSS 20B (base) — different lineage at the practical ceiling
- (deferred) v7 vs. Sonnet (cloud) — the original cross-class diagnostic; costs apply

**Why this matters for the discussion:**
- The local matrix is **free** to run (same hardware, just different model pulls).
- Cleanly discriminates: "is the binding constraint *model class*, *fine-tune lineage*, *model size*, or *architecture*?" Each variant answers a different sub-question.
- Discriminator runs *against the same harness scenarios* — every candidate model gets evaluated by the same FC SPEC suite. PASS/FAIL distribution per model = empirical map of which classes are model-bound vs. architecture-bound.
- The cloud A/B (v7 vs. Sonnet) remains the strongest cross-class diagnostic but costs apply; defer until local results suggest it's worth.

**Cost reality:** Fine-tuning a new base model (if Qwen3 14B or GPT-OSS 20B looks promising) is a substantial investment — the v8 training-data pipeline doesn't transfer 1:1 to a different model architecture. Local-base A/B with NO fine-tune answers the "is the unfine-tuned base better than the fine-tuned v7" question, which is the right question to ask before committing fine-tune effort.

**This is queued behind the six FC fixes** — not competing with them. The architecture fixes close empirically-pinned defects regardless of model. Model-class A/B follows once the architecture is sound.

- **No new themes** until either the harness surfaces a new class or the A/B harness reveals a model-class binding constraint.

---

## §6 — What I'd like to discuss specifically

1. **D1 (FC-001 fix shape)** — strongest opinion needs your sign-off
2. **D2 (FC-002 scope)** — narrow vs. broad — opinions?
3. **D5 (theme retirement)** — what's actually retired, what's just paused?
4. **Cadence (D4)** — sequential or batched for prompt fixes?
5. **Timing** — when to start fix work? After tomorrow's discussion? Later this week?
6. **A/B harness** — when does it become the next move?

---

## §7 — Quick reference

- **Plan doc:** [`ANI-Test-Harness-Plan.md`](./ANI-Test-Harness-Plan.md)
- **Failure Class Registry:** [`ANI-Failure-Class-Registry.md`](./ANI-Failure-Class-Registry.md)
- **DayOne status:** [`ANI-Test-Harness-DayOne-Status.md`](./ANI-Test-Harness-DayOne-Status.md)
- **Tests:** `tests/AniRuntime.Tests/Regression/FC001_*`, `FC001d_*`, `FC001e_*`, `FC001f_*`, `FC001g_*`, `FC002_*`, `FC003_*`, `FC004_*`, `FC005_*`, `FC006_*`, `FC007_*`, `FC008_*`, `FC009_*`

Coffee well.
