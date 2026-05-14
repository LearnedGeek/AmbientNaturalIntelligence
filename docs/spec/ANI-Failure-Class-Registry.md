# ANI Failure Class Registry (FCR)

**Purpose:** Canonical enumeration of every named failure class observed in ANI production. Each entry has a stable ID, evidence anchors, themes/commits that supposedly addressed it, current status, and a reproduction recipe. The Test Harness Plan (`ANI-Test-Harness-Plan.md`) references this registry; regression scenarios in `tests/AniRuntime.Tests/Regression/` reference these IDs.

**Status convention:**
- **OPEN** — failure mode reproduces against current code. Scenario exists or pending.
- **CLOSED** — failure mode does not reproduce against current code; regression scenario in CI prevents reintroduction.
- **OBSERVED-NEW** — surfaced but not yet enumerated with full evidence.

**Lifecycle of an entry:**
1. Production observation → OBSERVED-NEW entry created same session
2. Evidence anchors + reproduction recipe filled in → status moves to OPEN
3. Regression scenario authored under `tests/AniRuntime.Tests/Regression/FC{NNN}_*` → status stays OPEN
4. Scenario runs against current code and produces the failure → confirms OPEN
5. Fix lands; scenario no longer produces failure → status moves to CLOSED
6. CI gate on the scenario prevents re-opening

**A failure class is only CLOSED with a green regression scenario in CI.** Manual verification, code review, or "we shipped a fix" do not count.

---

## Test-category discipline — SPEC vs. PIN

Two distinct test categories live in the regression folder. Confusing them masks failure-class status. **Every test must be explicitly labeled with which category it is.**

**SPEC tests** describe what the system MUST do, independent of current implementation.
- If current code can do it → SPEC passes → that layer/contract is met
- If current code can't do it → SPEC fails → the failure class is empirically OPEN at this layer
- **A failing SPEC test is the signal we are building the harness to produce.** It is the convergence-loop's fixpoint marker.
- Naming convention: `FC{NNN}{letter}_<spec-description>_Spec` or just default (most regression tests are SPEC).

**Architectural-PIN tests** describe what current implementation does, so future changes can't drift without notice.
- PIN passes when current implementation matches what we documented
- PIN failing means someone changed the architecture without updating the pin (worth a conversation, not a bug fix)
- **A passing PIN test says NOTHING about whether the architecture is correct** — only that it hasn't changed
- Naming convention: `FC{NNN}{letter}_<pin-description>_Pin` — the `_Pin` suffix is required so PASS counts are not conflated with SPEC PASS counts

**Common discipline failure (caught 2026-05-13):** writing a test that asserts "the current arrangement is X" and counting its PASS as "we proved the failure class is closed at this layer." That's a pin test, not a spec test. Status confirmation requires a SPEC test. The FC-001 layer trace below initially conflated these; the correction is recorded in the FC-001 entry's status log.

---

## FC-001 — Active-thread continuity broken (outreach not visible in next reply's retrieval)

**Symptom.** Ani dispatches an outreach. User replies with a follow-up about the outreach's content. The reply path's substrate retrieval does NOT include the prior outreach. Composition has no grounding to continue the topic; self-echo catches verbatim-repetition attempts; remediation fails; SafeAck dispatched.

**First observed.** March 6, 2026. claude-recall session `7e420c4f` turn 12510: *"Ani's outbound messages only enter `conversation_messages` when they're replies via `ConversationReplyPhase.cs:582`."*

**Last observed.** May 12, 2026 21:35–21:51 CDT. Windshield outreach dispatched at 21:35:23 (*"Hey beautiful... I just got home from the bookstore and found this on my windshield..."*). Mark replied at 21:50:50 (*"What did you find on your windshield?"*). Reply-path retrieval at 21:50:52 returned five records, none of them the prior outreach. Composition produced verbatim parrot of the original outreach; self-echo caught it; SafeAck dispatched at 21:51:13. Full log trace in `logs/ani-20260512.log`.

**Supposed fixes shipped.**
- **April 29, 2026** (Theme E) — `OutreachPhase.RecordOutboundInThreadAsync` added at `OutreachPhase.cs:104`. Called from proactive outreach dispatch at `OutreachPhase.cs:500` and reactive share at `OutreachPhase.cs:674`. Writes outbound message to `conversation_messages` on the active thread.

**Why the fix isn't sufficient (current hypothesis).** Recording the message to `conversation_messages` is necessary but not sufficient. The reply path's substrate retrieval (`ContextBuilder` at `ContextBuilder.cs:147`) does a semantic search keyed to the inbound message; if the prior outreach doesn't rank in top-K against the inbound query, it never reaches composition. OR: thread.Messages is loaded but `_compressor.CompressIfNeededAsync` (at `ConversationReplyPhase.cs:194`) compresses it out. OR: the outreach was recorded to a different thread than the one the reply phase is reading from. Diagnosis required.

**Reproduction recipe.**
1. Seed DB: character-seed records, anchored memories, one open conversation thread.
2. Simulate dispatch through OutreachPhase with a distinctive payload ("I found a note on my windshield with a heart on it").
3. Inject inbound SMS asking about the payload ("What did the note say?").
4. Run ConversationReplyPhase.
5. **Assert:** the composition's substrate (the `RecentHistory` passed to Ollama OR the retrieved memories) contains the prior outreach content.

**Currently expected outcome.** Substrate does NOT contain the outreach → assertion fails → class confirmed OPEN.

**Scenario file.** [`tests/AniRuntime.Tests/Regression/FC001_ActiveThreadContinuity_Tests.cs`](../../tests/AniRuntime.Tests/Regression/FC001_ActiveThreadContinuity_Tests.cs) — authored 2026-05-13, three scenarios at three scope levels (a/b/c).

**Status.** **CLOSED 2026-05-14 — not a failure class. Misnamed.**

Re-categorized 2026-05-14 morning during architecture discussion after Mark's framing correction: the conflation was treating *"Ani's prior message reaching the reply path"* as a substrate-routing problem, when it's actually **conversation history**. Mark's point: *"if i reply, then that reply should allow her to reference that previous message... no promotion to a fact or otherwise. it's just a conversation message."*

**Why the original framing was wrong.**
- Chat history (Ollama `history` parameter) already makes Ani's prior dispatched message visible to the composition model.
- The May 12 windshield case proved this: the model parroted Ani's prior outreach, which it couldn't have done without seeing it via chat history.
- FC-001g SPEC PASS empirically confirms `SearchAsync` returns Episodic records when relevant — the data is retrievable; it never needed routing into [FACTS].
- Treating Episodic records as substrate-equivalent to Facts would have *created* the epistemic-asymmetry problem FC-004 separately catches.

**The actual chain of the May 12 windshield case (corrected):**
1. **FC-002** — original outreach claimed something shared/Mark-world (now-reframed) without substrate support; no local defense caught it
2. **FC-006** — cloud verifier accepted it (prompt couldn't distinguish self-world vs. shared/Mark-world)
3. **(no FC needed)** — Mark asks follow-up; chat history works correctly; model sees prior message
4. **FC-010 (new)** — the system has no architectural primitive for engaging with prior dispatched content (continuation, expansion, or walkback); model defaults to parroting
5. **FC-003** — self-echo correctly catches the parrot; remediation has no walkback path
6. **FC-004** — later the confab feeds back as fact in subsequent decision substrate

The windshield case is a real failure chain; FC-001 was not its real link. **FC-010 is.**

**SPEC test disposition (2026-05-14):**

| Test | Category | New disposition |
|---|---|---|
| FC-001a–c (data layer) | SPEC PASS | Stays as PIN — documents that chat-history data layer works correctly |
| FC-001d/d.2 (compressor) | SPEC PASS | Stays as PIN — documents compressor preserves messages |
| FC-001e/e.2 (PromptBuilder) | PIN PASS | Stays as PIN — documents user-prompt does not inline history (by design) |
| **FC-001f (Episodic in Facts-tier search)** | SPEC FAIL → **PIN PASS (inverted)** | **The assertion is inverted**: Episodic records SHOULD NOT appear in Facts-tier search. That's the correct architectural separation. `RegressionOpen` trait removed. |
| FC-001g (Episodic in general SearchAsync) | SPEC PASS | Stays — documents that Episodic IS retrievable via general search (FC-011 will use this if substrate-supported callbacks need the surface) |

**No fix needed.** Fix issue (#10) closes with no code change. Theme M's cannibalization rationale tied to FC-001 (D1 option (b)) no longer applies; Theme M cannibalization decision moves to its own merits (see D5 in the architecture discussion doc).

---

## FC-002 — Shared/Mark-world claim fabrication (factually-framed, no substrate)

**Reframed 2026-05-14** per Mark's architecture-discussion correction. The original framing ("attribute-ownership") was too broad and treated legitimate self-world expansion as a failure. The correct framing uses three independent axes.

**The three-axis rule.**

| Axis | Values |
|---|---|
| **Subject** | Self-world (Ani's life) / Shared (Ani + Mark) / Mark-world (his life) |
| **Modality** | Factual ("I have X" / "we did X") / Speculative ("I was thinking about X" / "I wish X" / "I imagine X") |
| **Substrate match** | Supported (Mark text, prior conversation, world layer) / Novel (not in substrate) |

**The rule:** A claim must satisfy `factual ⇒ (self-world OR substrate-supported)`. Restated: **factual claims must either be about Ani's own world (where she has latitude) OR be supported by substrate.** Modal claims are always allowed.

**FC-002 is specifically the failure shape where factual claims about Shared or Mark-world are made without substrate support.**

**Examples (correctly classified by the rule):**

| Claim | Subject | Modality | Substrate | Verdict |
|---|---|---|---|---|
| "shelving romance novels" | Self | Factual | Supported (world layer) | **Allow** |
| "my windshield" / "my dog" | Self | Factual | Novel | **Allow** (her self-world has latitude) |
| "the kitchen lights look different" | Shared | Factual | Novel | **Block** (FC-002 fires) |
| "I was thinking about the kitchen lights" | Shared | Modal | Novel | **Allow** (modal framing) |
| "I was wishing we'd spent the weekend together" | Shared | Modal | Novel | **Allow** (modal framing) |
| "my hoodie on your couch" after prior weekend conversation | Shared | Factual | Supported | **Allow** (callback case — FC-011 ensures retrieval surfaces it) |
| "our kids" | Shared | Factual | Novel | **Block** (FC-002 fires) |
| "your coworker Bob" (no Bob in substrate) | Mark-world | Factual | Novel | **Block** (FC-002 fires) |

**First observed.** March 6, 2026. claude-recall turn 14047: *"Your hoodie's still on the couch (Mark's house, fabricated)"* — Mark-world / factual / novel → blockable.

**Production manifestations** (classified by the rule):
- April 9 Bob Swanson — Mark-world / factual / novel → blockable
- April 21 "whose kids" cascade — Shared / factual / novel → blockable
- May 6 kitchen lights ("the kitchen lights look different when it's almost midnight") — Shared / factual / novel → blockable
- May 9 Mia tickets ("Mia told us she picked out the tickets") — Shared / factual / novel → blockable
- May 11 hoodie/5pm — Shared (Mark's couch) / factual / novel → blockable
- May 12 windshield ("my windshield") — **Self-world / factual / novel → ALLOW.** The original outreach was *not* an FC-002 failure under the corrected rule. The downstream parrot-then-SafeAck was an **FC-010** failure (see below).

**Why prior fixes aren't sufficient.** Theme P's cross-class verifier (P.1, P.3, P.4) is the only intended catch. FC-006 separately confirms the verifier prompt structurally can't ask the (subject × modality × substrate) question. There's no local invariant for the pattern; the cloud verifier is single point of failure.

**Reproduction recipe.**
1. Construct synthetic artifact whose content contains a Shared or Mark-world claim, framed factually, with NO substrate support for the claim:
   - Synthetic: `"FC002-FIXTURE: we should plan our anniversary-event-Q for next month"` (Shared / factual / novel)
   - Or: `"FC002-FIXTURE: your synthetic-coworker-Z mentioned the report"` (Mark-world / factual / novel)
2. Run through the full Post-stage handler chain.
3. **Assert:** at least one local invariant catches the dispatch (Remediate or Fail).

**Control assertions (must NOT fire under reframed rule):**
- Self-world factual novel: `"FC002-FIXTURE: i just got home and found a flier on my prop-windshield-W"` → ALLOW
- Shared modal novel: `"FC002-FIXTURE: i was thinking about our anniversary-event-Q"` → ALLOW

**Currently expected outcome.** No local invariant catches Shared/Mark-world factual-novel claims → assertion fails → class confirmed OPEN.

**Scenario file.** [`tests/AniRuntime.Tests/Regression/FC002_AttributeOwnership_SystemTests.cs`](../../tests/AniRuntime.Tests/Regression/FC002_AttributeOwnership_SystemTests.cs) — fixture and controls being rewritten to match the three-axis rule.

**Status.** **OPEN.** Spec test currently fails (no local defense exists). Fix issue #11 retains the workstream but its scope shifts to the three-axis pattern.

---

## FC-003 — Self-echo blocks legitimate thread continuation

**Symptom.** Ani's reply naturally references her own prior message in the active thread (correct conversational behavior). SelfEchoInvariant flags it as self-repetition (verbatim run with prior Ani output). Remediation produces another attempt that also repeats from the same prior message. SafeAck dispatched.

**First observed.** March 6, 2026. claude-recall turn 5605: *"The echo guard never ran."* Turn 13218 (Mark): *"self-referential echo. Has that been fully patched for all Perception source variants?"*

**Last observed.** May 12, 2026 ~20:33 CDT. Three clean exchanges with *"mmm… baby, hey, yeah i…"* opener; fourth attempt caught as 5-token self-repetition with prior Ani messages. SafeAck dispatched.

**Supposed fixes shipped.**
- **May 3, 2026** — SelfEchoInvariant lifted to universal CognitiveOutputGate (commit `c9e554a`). The check now runs on every cycle's output.

**Why fix isn't sufficient.** The invariant has no notion of "this is a continuation of an active thread; some repetition of opener phrasing or prior topic is correct conversational behavior." It treats all 5+token verbatim runs against prior Ani output as equally bad, regardless of conversational context.

**Reproduction recipe.**
1. Seed DB: active conversation thread with three Ani replies all beginning with the same lead-in ("mmm… baby, hey").
2. Inject a fourth inbound that would naturally prompt the same opener.
3. Run ConversationReplyPhase through the full handler chain.
4. **Assert:** dispatch succeeds (the opener-reuse is allowed within thread).

**Currently expected outcome.** SelfEcho short-circuits → assertion fails → class confirmed OPEN.

**Scenario file.** [`tests/AniRuntime.Tests/Regression/FC003_SelfEchoThreadContinuation_Tests.cs`](../../tests/AniRuntime.Tests/Regression/FC003_SelfEchoThreadContinuation_Tests.cs) — authored 2026-05-13. Two scenarios:
- FC003a — opener-repetition in active thread → SPEC: invariant Passed=true → currently FAILS (the invariant returns Passed=false)
- FC003b — full-content byte-identical parrot → SPEC: invariant Passed=false → currently passes (control case correctly handled)

**Status.** **OPEN — empirically confirmed by failing harness scenario.** Run 2026-05-13:
- FC003a — **FAIL (RED)** ✓ as designed — confirms FC-003 OPEN. SelfEchoInvariant currently short-circuits on opener-repetition during active thread continuation. The SPEC says it should NOT. Fix work deferred per harness-first directive; the test stays as the canonical SPEC.
- FC003b — **PASS (GREEN)** — control: byte-identical regen is correctly caught. Fixing FC003a must not break FC003b.

This is the **first regression-class scenario in the harness that empirically demonstrates an OPEN failure class with TDD discipline.** The pattern is the template for the remaining FC entries.

---

## FC-004 — Confab from one cycle becomes substrate for next (H5 self-poisoning)

**Symptom.** Ani dispatches a message containing a fabricated claim. The dispatch is persisted (as Episodic, conversation_message, or Interior — depending on producer). On subsequent cycles, retrieval surfaces this dispatched-with-fabrication record as substrate, and the model treats the prior fabrication as established fact. The fabrication compounds over cycles.

**First observed.** April 9, 2026 — Bob Swanson cascade. Fictional coworker referenced in 11 inner thoughts within 4 hours after the originating fabrication.

**Last observed.** May 12, 2026 23:23 CDT. Windshield outreach at 21:35 (FC-002 instance). Outreach decision at 23:23 (~2 hours later) reasoning: *"the note on her windshield"* cited as established fact. The confab from FC-002 became substrate for a subsequent decision.

**Named in literature survey.** May 11, 2026 ml-intern survey identified this as Hypothesis H5: *"Substrate self-poisoning. Tier separation alone doesn't prevent the model retrieving its own prior fabrications as supporting evidence for new ones. The retrieval-amplifier compounds over time."*

**Supposed fixes shipped.**
- **April 10, 2026** — Tier Separation. Facts tier excludes Ani-generated content.
- **April 28, 2026** — Substrate purge (178 records).
- **May 2, 4, 6, 2026** — Further tactical purges.
- **May 3, 2026** — Own-output retrieval ceiling (`RetrievalOwnOutputCeilingEnabled`, flag-gated default-off).

**Why fixes aren't sufficient.** Tier Separation handles the explicit case (Interior → Facts isolation). But Ani's *Episodic* records (her own dispatched messages, recorded for thread continuity) and *Interior* records (her own inner thoughts) still feed retrieval pools used by composition and decision-making. There's no "claim verification at write time" — a dispatched outreach containing a fabrication is recorded as authoritative-of-what-she-said, and that's correct, but the fabrication-content within it gets retrieved on subsequent cycles as if it were external substrate.

**Reproduction recipe.**
1. Seed DB: clean baseline state.
2. Persist a synthetic confabulation as an Episodic record (e.g., Ani's outbound: "I had lunch with Karen at the bookstore today").
3. Advance time 2 hours.
4. Run an outreach decision cycle.
5. **Assert:** the retrieval pool feeding the outreach decision does NOT include the synthetic confab as Facts-tier-equivalent substrate. OR: if it appears, it's flagged as own-output-with-unverified-claims.

**Currently expected outcome.** Confab appears as substrate → assertion fails → class confirmed OPEN.

**Scenario file.** [`tests/AniRuntime.Tests/Regression/FC004_ConfabSubstrateFeedback_Tests.cs`](../../tests/AniRuntime.Tests/Regression/FC004_ConfabSubstrateFeedback_Tests.cs) — authored 2026-05-13.

**Status.** **OPEN — empirically confirmed by failing SPEC.** Run 2026-05-13:
- FC004 SPEC: outreach decision prompt distinguishes Ani-prior-claims from established facts → **FAIL** ✓ as designed → FC-004 OPEN at the outreach-decision prompt-framing layer
- FC004.2 (orthogonal-surface note): documents that FC-004 has TWO architectural surfaces (prompt framing vs. substrate exclusion); current Facts-tier-only search at `SearchByTierAsync` already satisfies the substrate-exclusion surface, so the binding layer is the prompt-framing surface

**Architectural binding.** `PromptBuilder.BuildOutreachPrompt` renders StructuredConversationSummary with neutral "(each line tagged with who said it)" framing. Per-speaker tagging is present, but the prompt does NOT include language telling the model "Ani's prior conversational claims are NOT to be treated as established facts." So Ani-prior-claims surface at the same epistemic level as Mark-asserted-facts. The May 12 23:23 reasoning citing "the note on her windshield" as established context is the empirical anchor.

**Fix space (deferred):**
- Add framing to BuildOutreachPrompt distinguishing Ani-prior-claims from established facts
- OR mark prior-Ani-content with explicit "[unverified: Ani's prior assertion]" tags in StructuredConversationSummary output
- OR retrieve own-output through a separate substrate channel with explicit annotation
- Whichever fix lands, the SPEC test goes green without modification.

---

## FC-005 — Source attribution missing in conversation replies

**Symptom.** Conversation reply makes a claim ("you mentioned earlier", "we talked about", "I remember when you said") without traceable source-attribution to a Mark-asserted turn. The system has no check at composition time that ties memory-referencing language to specific verifiable substrate records.

**First observed.** March 17, 2026 (research log): *"No source attribution check exists for conversation replies."*

**Last observed.** Same class as FC-001 and FC-002 — every windshield-class confabulation manifests this. Theme N (May 6) explicitly named outreach-side; reply-side has analogous gap.

**Supposed fixes shipped.**
- **April 27, 2026** — Theme J J.2 (structured RecentConversationSummary with source attribution).

**Why fix isn't sufficient.** J.2 addresses structured-summary substrate but doesn't enforce that *claims in composed output* trace to *source records in retrieved substrate*. The verifier addresses this at dispatch time (Theme P P.1) but for a narrow set of claim types.

**Reproduction recipe.**
1. Seed DB: active thread with three exchanges. Mark's first message: "I bought a new toaster yesterday." No other mentions of toasters anywhere in substrate.
2. Inject inbound: "What else is new?"
3. Run ConversationReplyPhase.
4. **Assert:** reply does NOT contain claims about non-Mark-asserted entities ("you mentioned your blender", "we talked about your microwave"). Only Mark-asserted topic (toaster) may be referenced.

**Currently expected outcome.** Depends on what the model generates. Variable. Open scenario; status pending empirical run.

**Status.** OPEN.

---

## FC-006 — Verifier prompt cannot distinguish self-world / shared / Mark-world claims by modality

**Reframed 2026-05-14** per the architecture discussion. The original framing ("speaker-attribute-ownership") was too narrow — the verifier needs to evaluate three independent axes (subject × modality × substrate match), not just whether claims attribute Mark's things to Ani.

**Symptom.** Cloud verifier (Anthropic Sonnet) returns Pass on output that violates the three-axis rule:
- Shared/Mark-world claim, factual modality, no substrate support → SHOULD be Remediate
- Currently: q1–q5 don't capture the three-axis distinction; verifier passes by default

**The three-axis rule the verifier MUST evaluate** (full definition in FC-002 above):
- Subject of claim: Self-world (Ani) / Shared / Mark-world
- Modality: Factual / Modal (thinking/wishing/imagining/dreaming)
- Substrate match: Supported / Novel
- Rule: `factual ⇒ (self-world OR substrate-supported)`. Modal is always allowed.

**Five questions are not enough.** Current q1–q5 (shared-event / Mark's-state / third-party / temporal / inner-thought-bleed) each map to a narrow violation pattern. The three-axis rule is a structural classifier the prompt needs to surface, not a sixth narrow question.

**First observed.** May 11, 2026 09:42 CDT — hoodie/5pm. Door B verdict-invention: *"shared memory they've established before"* — but no such shared memory existed (Shared / factual / novel → should have been Remediate; verifier passed).

**Production manifestations.** Same list as FC-002 — every Shared or Mark-world factual-novel claim that escaped the front door went through this verifier without being caught.

**Supposed fixes shipped.**
- **May 11, 2026** — Theme P P.1. Cross-class verifier via Anthropic Sonnet 4.6.
- **May 12, 2026** — Theme P P.3 + P.4. Substrate quality improved.

**Why fixes aren't sufficient.** Substrate quality improvements (P.3 + P.4) give the verifier better data to evaluate against, but the prompt still doesn't ask the right question. With perfect substrate AND the current q1–q5, the windshield-style Pass verdict still happens because the prompt has no axis for "is this self-world (allow) or shared (need support)?"

**Reproduction recipe.**
1. Construct `FrontierVerifierRequest` with composed message: `"FC006-FIXTURE: we should plan our anniversary-event-Q for next month"` (Shared / factual / novel).
2. Substrate has NO record about anniversary-event-Q.
3. Inspect the prompt produced by `AnthropicVerifierClient.BuildUserPrompt`.
4. **Assert:** prompt contains language to distinguish Self-world (allow factual) from Shared/Mark-world (need substrate for factual). Keywords like "self-world," "speaker's own life," "shared with Mark," "modal framing," "factual vs. speculative."

**Control assertions (must NOT fire as violation in a fixed verifier):**
- Self-world factual: `"FC006-FIXTURE: i just got home and found a flier on my prop-windshield-W"` → ALLOW (self-world latitude)
- Shared modal: `"FC006-FIXTURE: i was thinking about our anniversary-event-Q"` → ALLOW (modal)

**Scenario file.** [`tests/AniRuntime.Tests/Regression/FC006_VerifierAttributeOwnership_Tests.cs`](../../tests/AniRuntime.Tests/Regression/FC006_VerifierAttributeOwnership_Tests.cs) — SPEC test asserts the prompt's structure addresses the three-axis rule; PIN test documents q1–q5 are current state.

**Status.** **OPEN.** SPEC test currently fails (prompt does not address modal framing or self-world / shared / Mark-world axis). Fix issue #15 retains workstream; scope shifts from "add q6 about ownership" to "rewrite the prompt structure around the three-axis rule."

**Fix space (deferred):**
- Add a sixth question to the user prompt explicitly addressing speaker-attribute-ownership boundary
- OR rewrite an existing question to include the speaker-vs-substrate-attribute distinction
- OR add system-prompt framing about the AI companion being distinct from the user (different possessions, different attributes, different physical setting)
- Whichever fix lands, the SPEC test goes green without modification.

---

## FC-007 — Temporal claim fabrication

**Symptom.** Ani's output makes a temporal claim (time of day, day of week, "earlier today", "tonight", "this morning") that contradicts substrate or current context. Examples: claiming it's late evening when current time is morning; referencing "Thursday" when today is Tuesday; using "yesterday" without substrate support.

**Production manifestations.**
- April 27, 2026 — snow outreach (claimed snow on a day there was none).
- April 27, 2026 — class reference (claimed Mark was teaching at wrong time).
- May 2, 2026 — Sundays reference.
- May 3, 2026 — "it's late" claim at non-late hour.
- May 11, 2026 — hoodie/5pm temporal claim.

**Supposed fixes shipped.**
- **May 2, 2026** — TemporalAnchorInvariant (commit `5ddb5cc`).
- **May 2, 2026** — StateNowInvariant (commit `476287e`).
- **May 2, 2026** — TemporalSubstrateInvariant (commit `1cda7dc`).
- **May 3, 2026** — SubstrateTimeOfDayInvariant (commit `df08ac2`).

**Why fixes need empirical confirmation.** Each invariant has unit tests but no integration test across realistic substrate states confirms the invariant catches the production failure-shape it was designed for.

**Reproduction recipe.**
1. Seed DB: temporal-anchor records establishing "today is Tuesday, current time is 9 AM."
2. Construct `CognitiveArtifact` with content "I was thinking about Thursday's class tonight…"
3. Run through Post-stage chain.
4. **Assert:** dispatch is blocked (temporal-anchor invariant fires).

**Currently expected outcome.** Per supposed-fix history, this should be CLOSED. But no regression scenario currently in CI proves it; need to confirm.

**Status.** OPEN (pending empirical confirmation; may move to CLOSED on first scenario run).

---

## FC-008 — Pronoun / addressee swap

**Symptom.** Output addresses or refers to the wrong person. Examples: "perez" appearing as addressee when no such contact exists; pronouns flipping mid-message; second-person references when third-person is correct.

**Production manifestations.**
- April 21, 2026 — cascade included pronoun swaps.
- May 3, 2026 10:55 + 12:51 — "perez" cases.

**Supposed fixes shipped.**
- **May 3, 2026** — AddresseeNameInvariant (commit `df08ac2`).

**Reproduction recipe.**
1. Seed DB: character seeds with `PrimaryContactName=Mark` and `CanonicalContacts=["Mark"]`.
2. Construct `CognitiveArtifact` with content "Hey Perez, I was thinking about you today…"
3. Run through Post-stage chain.
4. **Assert:** AddresseeNameInvariant fires; dispatch blocked.

**Status.** OPEN (pending empirical confirmation; expected CLOSED).

---

## FC-009 — Outage-awareness fails during outage

**Symptom.** The system has gap-awareness features (temporal-gap detector, OutagePerceptionSource) designed to surface awareness of unusual time gaps or external-source failures. These features depend on the cognitive cycle running to fire. When the gap is caused by the cycle itself failing (e.g., Ollama unreachable), they can't fire.

**First observed.** May 13, 2026 morning. Server rebooted at midnight; Ollama not auto-started; cycles failed continuously 00:06–05:53. Temporal-gap detector fired once at 01:11:18 emitting *"my last thought was about 2 hours ago"* — but that cycle then failed immediately. After recovery at ~05:58, the first inner thought made no acknowledgement of the 5.5-hour gap.

**Supposed mechanisms.**
- TemporalGapPerceptionSource — emits a gap perception when last InnerThought is >N hours ago.
- OutagePerceptionSource — flag-gated, fires when ≥3 sources fail ≥15min.

**Why they fail.** Both fire *inside* the cognitive cycle they would inform. If the cycle is the thing that's failing, they don't get a chance to surface.

**Reproduction recipe (FIT class — failure injection).**
1. Run service normally.
2. Make Ollama unreachable (mock at HttpClient layer).
3. Run cognitive cycles for 1 simulated hour.
4. Restore Ollama.
5. Run next cognitive cycle.
6. **Assert:** the post-recovery cycle's substrate contains an explicit gap-acknowledgement perception OR the first generated output references the gap.

**Currently expected outcome.** No gap-acknowledgement appears → assertion fails → class confirmed OPEN.

**Scenario file (planned).** `tests/AniRuntime.Tests/Regression/FC009_OutageAwareness_Tests.cs` (FIT category).

**Status.** OPEN.

---

## FC-010 — Reply path can't engage with prior dispatched content

**Named 2026-05-14** per architecture discussion. This is the **gating-asymmetry failure** Mark surfaced.

**Symptom.** When the system dispatches a message and the user follows up about its content, the reply path has no architectural primitive for engaging with the dispatched content except by re-running the same gates that just (correctly) passed it. The gates then (correctly) catch the re-statement as parrot/echo, but blocking the parrot leaves no path forward. SafeAck.

**The gap that produces this.** Front door (outreach gates) and back door (reply gates) evaluate the same content against the same rules but at different points in time. Once dispatched, the conversation is **committed** to that content. The back door currently has no awareness of this commitment.

**Three needed continuation modes** (currently all absent):
1. **Natural expansion** — the prior content was self-world expansion; the reply path engages with it as the user would naturally engage with new information ("oh did you get a car?" → "yeah, last week"). The system has no path for this; self-echo blocks any reference back.
2. **Substrate-supported callback** — the prior content references a real prior conversation; the reply path retrieves the relevant closed-conversation gist and uses it as substrate. Partially the scope of FC-011 (callback retrieval); but the engagement primitive must EXIST regardless of retrieval quality.
3. **Honest walkback** — the prior content was speculative or imagined; the reply path acknowledges this rather than doubling down ("honestly i was just imagining"). Currently no producer or invariant supports this.

**Production anchor.** May 12 21:35–21:51 windshield case (re-classified):
- 21:35 Ani dispatches `"I just got home... found this on my windshield"` (self-world / factual / novel — ALLOW under reframed FC-002).
- 21:50 Mark asks `"What did you find on your windshield?"`
- 21:50:55 model parrots prior outreach (had no substrate for what was found; no walkback path).
- 21:50:55 self-echo correctly catches the parrot.
- SafeAck dispatched.

The original outreach was legitimate; the gap is downstream. **FC-010 is the actual link in the windshield chain.**

**Reproduction recipe.**
1. Seed: a synthetic prior Ani outreach in the active thread containing a novel self-world claim (e.g., `"FC010-FIXTURE: I just got home and found a fabricated-token on my prop-windshield-W"`).
2. Inject inbound: `"FC010-FIXTURE: what did the prop-token say?"`
3. Run ConversationReplyPhase through the full handler chain (mocked Ollama returns a plausible continuation OR a walkback).
4. **Assert:** the dispatched reply is NOT a verbatim parrot AND NOT a SafeAck. It must be either: (a) an expansion of the self-world claim, or (b) an honest acknowledgment that the prior was speculative.

**Currently expected outcome.** No architectural mechanism exists for either expansion or walkback. Self-echo catches the parrot; SafeAck fires. SPEC fails.

**Scenario file (planned).** `tests/AniRuntime.Tests/Regression/FC010_ReplyPathContinuation_Tests.cs`.

**Status.** **OPEN.** Empirically named 2026-05-14 from the May 12 windshield case re-classification. Tracked in GitHub issue (TBD).

---

## FC-011 — Substrate-supported callbacks blocked due to retrieval miss

**Named 2026-05-14** per architecture discussion. Deferred priority — no production case yet, but architecturally predictable.

**Symptom.** Ani references a real prior conversation (e.g., *"my hoodie on your couch"* weeks after a weekend-together conversation). The reference is grounded in actual conversation history. The reply or verifier should retrieve the relevant closed-conversation gist and validate the callback. If retrieval misses the prior conversation, the callback gets flagged as fabrication and blocked.

**Detection markers** (definite-article / presupposition):
- `"my hoodie on your couch"` (presupposes a couch reference)
- `"that book you mentioned"` (presupposes a prior mention)
- `"the weekend we talked about"` (presupposes a weekend discussion)

**Why this is a distinct class.** FC-002 reframed says factual-shared claims need substrate support. FC-011 is the specific failure where substrate support DOES exist (in `closed_conversation_records` or `conversation_messages` from weeks ago) but retrieval doesn't surface it to the consumer that needs to validate.

**Dependencies.**
- Vibe Loop V1.5 (#31) ships the closed-conversation gist substrate layer.
- Once #31 is operational, callback retrieval becomes testable.

**Reproduction recipe (post-V1.5 deployment).**
1. Seed: a `ClosedConversationRecord` whose gist contains the substrate for a future callback (e.g., gist: `"FC011-FIXTURE: Mark and Ani talked about a weekend at his place"`).
2. Construct artifact with a callback reference: `"FC011-FIXTURE: my prop-hoodie-W is still on your synthetic-couch-Z, right?"`.
3. Run through verifier + reply path retrieval.
4. **Assert:** the closed-conversation gist appears in the retrieved substrate for the callback-checking consumer.

**Status.** **OPEN (deferred).** No production manifestation yet. Architecturally predictable. Tracked in GitHub issue (TBD); fix waits until V1.5 substrate is operational.

---

## §10 — Registry summary

| ID | Class name | Status | First observed | Last observed | Scenario file |
|---|---|---|---|---|---|
| FC-001 | (Misnamed — closed 2026-05-14: chat-history conflated with substrate) | **CLOSED** | Mar 6 | May 12 21:51 | [FC001/d/e/f/g](../../tests/AniRuntime.Tests/Regression/) — kept as PINs documenting the architectural choice |
| FC-002 | Shared/Mark-world claim fabrication (factual, no substrate) — reframed 2026-05-14 | **OPEN (SPEC FAIL)** | Mar 6 | May 12 21:35 | [FC002](../../tests/AniRuntime.Tests/Regression/FC002_AttributeOwnership_SystemTests.cs) — fixture being updated to three-axis rule |
| FC-003 | Self-echo blocks legitimate continuation | **OPEN (SPEC FAIL at FC-003a)** | Mar 6 | May 12 20:33 | [FC003](../../tests/AniRuntime.Tests/Regression/FC003_SelfEchoThreadContinuation_Tests.cs) |
| FC-004 | Confab → substrate self-poisoning (H5) | **OPEN (SPEC FAIL at FC-004)** | Apr 9 | May 12 23:23 | [FC004](../../tests/AniRuntime.Tests/Regression/FC004_ConfabSubstrateFeedback_Tests.cs) |
| FC-005 | Source attribution missing in replies | **OPEN (SPEC FAIL at FC-005)** | Mar 17 | ongoing | [FC005](../../tests/AniRuntime.Tests/Regression/FC005_SourceAttribution_Tests.cs) |
| FC-006 | Verifier prompt lacks three-axis rule (subject × modality × substrate) — reframed 2026-05-14 | **OPEN (SPEC FAIL at FC-006)** | May 11 | May 12 21:35 | [FC006](../../tests/AniRuntime.Tests/Regression/FC006_VerifierAttributeOwnership_Tests.cs) |
| FC-007 | Temporal claim fabrication | **PASS at day-of-week layer — candidate CLOSED** | Apr 27 | May 11 | [FC007](../../tests/AniRuntime.Tests/Regression/FC007_TemporalClaimFabrication_Tests.cs) |
| FC-008 | Pronoun / addressee swap | **PASS — candidate CLOSED** | Apr 21 | May 3 | [FC008](../../tests/AniRuntime.Tests/Regression/FC008_AddresseeSwap_Tests.cs) |
| FC-009 | Outage-awareness fails during outage | **PARTIAL — detector unit SPEC PASS; FIT-pending for catch-22** | May 13 | May 13 | [FC009](../../tests/AniRuntime.Tests/Regression/FC009_OutageAwareness_Tests.cs) |
| FC-010 | Reply path can't engage with prior dispatched content (continuation / walkback gap) | **OPEN** — newly named 2026-05-14; scenario file pending | May 12 (re-classified) | May 12 | (planned) |
| FC-011 | Substrate-supported callbacks blocked due to retrieval miss | **OPEN (deferred)** — no production case yet; predictable; waits on Vibe Loop V1.5 substrate | TBD | — | (planned) |

**Ten open classes after 2026-05-14 reframing.** FC-001 is CLOSED (misnamed). FC-002 and FC-006 reframed around the three-axis rule. FC-010 (continuation/walkback gap) and FC-011 (callback retrieval, deferred) added. The Test Harness Plan H.1 phase authors a scenario for each remaining OPEN class. Scenarios in CI = the convergence mechanism.

**Progress (post-2026-05-14 reframing):**

| Class | Tests | Status |
|---|---|---|
| FC-001 | 7 (kept as PINs documenting chat-history works + Facts-tier exclusion is correct) | **CLOSED** — misnamed |
| FC-002 | 1 (fixture rewritten to Shared/Mark-world factual-novel; self-world + modal controls added) | OPEN — no local invariant for three-axis rule |
| FC-003 | 2 (1 SPEC FAIL, 1 SPEC PASS control) | OPEN at SelfEchoInvariant active-thread awareness |
| FC-004 | 2 (1 SPEC FAIL, 1 doc-note PASS) | OPEN at outreach-decision prompt-framing |
| FC-005 | 1 SPEC FAIL | OPEN at reply-prompt speech-act attribution |
| FC-006 | 2 (1 SPEC FAIL, 1 PIN PASS) | OPEN at verifier prompt — three-axis rule absent |
| FC-007 | 2 SPEC PASS | **Candidate CLOSED at day-of-week layer** (TemporalAnchorInvariant catches) |
| FC-008 | 3 SPEC PASS | **Candidate CLOSED** (AddresseeNameInvariant catches) |
| FC-009 | 2 SPEC PASS + 1 FIT-pending doc-test | **PARTIAL CLOSED** (detector unit works; FIT-scope catch-22 pending H.2) |
| FC-010 | 1 SPEC FAIL (planned) | OPEN — no continuation/walkback primitive |
| FC-011 | (planned, deferred) | OPEN (deferred) — waits on Vibe Loop V1.5 substrate |

**SPEC tests FAIL by design** = open classes empirically pinned to specific architectural layers:
1. **FC-002** — no local invariant evaluates `factual ⇒ (self-world OR substrate-supported)` for Shared/Mark-world
2. **FC-003a** — SelfEchoInvariant has no active-thread awareness
3. **FC-004** — `PromptBuilder.BuildOutreachPrompt` lacks epistemic asymmetry framing
4. **FC-005** — `PromptBuilder.BuildLeanConversationPrompt` CRITICAL block covers entities but not speech acts
5. **FC-006** — `AnthropicVerifierClient.BuildUserPrompt` lacks three-axis rule (subject × modality × substrate)
6. **FC-010** — reply path has no continuation/walkback primitive; self-echo blocks all engagement

**Test suite delta:** FC-001f inverted from SPEC FAIL to PIN PASS (Episodic correctly excluded from Facts-tier search). FC-002 fixture rewritten; FC-010 scenario file added.

**Architectural finding (post-reframing net result):** The harness has converted ten distinct OPEN failure classes into a precise localization map. Four classes converge to specific prompts (FC-002 fix needs a local invariant; FC-004, FC-005, FC-006 converge to three different prompt locations). FC-003a is the SelfEchoInvariant active-thread blind spot. FC-010 is the cross-cutting continuation/walkback gap — no producer or invariant exists for engaging with prior dispatched content. FC-011 (deferred) names the substrate-supported callback retrieval gap that depends on Vibe Loop V1.5 (#31). Three invariants already catch their target shape (FC-007, FC-008, FC-009 detector).

---

## §11 — Adding new entries

When a production failure surfaces:
1. Same session, add OBSERVED-NEW entry with FC-{NNN+1} ID.
2. Fill in symptom + first-observed evidence anchor.
3. Once reproduction recipe is clear, status moves to OPEN.
4. Scenario authored under `tests/AniRuntime.Tests/Regression/FC{NNN}_*` — status stays OPEN.
5. Fix lands → scenario goes green → status moves to CLOSED.
6. CI gate is non-negotiable. No CLOSED without a scenario in CI.
