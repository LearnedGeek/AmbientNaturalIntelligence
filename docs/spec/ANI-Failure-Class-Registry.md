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

**Status.** **OPEN — empirically confirmed by failing SPEC at FC-001f.** Re-categorized 2026-05-13 evening after a TDD-discipline correction: FC-001e tests were architectural PINs (assertions of current arrangement), not SPEC tests of the FC-001 binding constraint. PIN PASS does not confirm CLOSED status.

Test category legend: **SPEC** = describes requirement, FAIL means class OPEN at this layer / PASS means contract met. **PIN** = describes current arrangement, PASS only means architecture unchanged / says nothing about correctness.

| Test | Category | Result | What the result means for FC-001 |
|---|---|---|---|
| FC-001a (data-path round-trip) | SPEC | PASS ✓ | Data layer correctly meets its contract — ruled out as the bug |
| FC-001b (multi-message ordering) | SPEC | PASS ✓ | Data layer preserves chronology — ruled out |
| FC-001c (Ani-then-Mark production sequence) | SPEC | PASS ✓ | Data layer round-trips both directions correctly — ruled out |
| FC-001d (compressor 2-message) | SPEC | PASS ✓ | Compressor preserves at small thread sizes — ruled out |
| FC-001d.2 (compressor ordering) | SPEC | PASS ✓ | Compressor preserves order — ruled out |
| FC-001e (PromptBuilder no-history-in-user-prompt) | **PIN** | PASS ✓ | Pins current design only. Says NOTHING about FC-001. |
| FC-001e.2 (PromptBuilder renders GroundedFacts) | **PIN** | PASS ✓ | Tautology pin. Says NOTHING about FC-001. |
| **FC-001f (substrate-retrieval-tier surfaces Episodic for reply path)** | **SPEC** | **FAIL** ✓ as designed | **Empirically confirms FC-001 OPEN at the substrate-retrieval-tier layer.** |

**The actual binding constraint, now localized: `ContextBuilder.cs:147` runs `_search.SearchByTierAsync(searchQuery, EpistemicTier.Facts, 5, ct)` — Facts-tier ONLY.** Ani's dispatched outreaches are persisted as Episodic records (per `OutreachPhase.cs:502` and `OutreachPhase.cs:684` setting `Provenance = EpistemicTier.Episodic`). **Episodic records never reach the [FACTS] block of the lean conversation prompt, even when semantically relevant to the current inbound.** The model gets chat history (via Ollama `history` parameter — which works, hence the May 12 windshield parrot) but no semantically-retrieved substrate to *ground* a follow-up beyond surface chat-history re-use.

**FC-001f scenario (pending authoring):**
- Real `SqliteMemoryService` against in-memory DB
- Seed: an Episodic record with content semantically related to a synthetic inbound query
- Run the composition-path retrieval (Facts-tier-only search per `ContextBuilder.cs:147`)
- SPEC assertion: when an Episodic record is the closest semantic match to a given inbound, the composition substrate path SHOULD surface it (currently it doesn't, because the tier filter excludes it)

**Architectural fix space (deferred per harness-first directive; for future fix work):**
- Composition substrate retrieval could include Episodic tier when the consumer is the reply path (since the reply path benefits from "what Ani said recently in this conversation")
- OR a separate Episodic-aware retrieval pass keyed to the active-thread context could be added to ContextBuilder
- OR the OutreachPhase persistence of Ani's outbound could mark a "thread-relevant-Episodic" subclass that's retrieved alongside Facts for the reply path
- Decision deferred. Spec stays: substrate the composition path sees MUST include relevant prior-Ani-outbound when the user asks a follow-up.

**Production case reframing (with empirical evidence now):** The May 12 windshield failure is a compound:
1. **FC-002** — original outreach claimed Ani has a windshield (attribute-ownership confab)
2. **FC-006** — verifier accepted the original confab (substrate-frame doesn't check ownership-boundary)
3. **FC-001 (this entry, true layer FC-001f)** — Ani's prior Episodic outreach didn't reach the reply path's [FACTS] block; model had only chat history; defaulted to parroting
4. **FC-003** — self-echo caught the parrot; remediation couldn't escape
5. **FC-004** — later (23:23) the windshield confab fed back into a subsequent outreach decision as established fact

That's the chain. Each layer is its own failure class; the harness will hold scenarios for each. The TDD localization across four FC-001 layers was the discipline producing the correct diagnosis.

---

## FC-002 — Attribute-ownership confabulation (Ani claims to have something Mark has, or shared things Mark didn't assert)

**Symptom.** Ani's output asserts ownership of attributes/objects that belong to Mark (his vehicle, his hoodie, his house, his family) OR shared events/possessions that Mark never asserted. Verifier and local gates fail to catch because the canonical substrate contains Mark-asserted versions of these attributes; the model "imports" them onto Ani's side without violating any tested invariant.

**First observed.** March 6, 2026. claude-recall turn 14047: *"Your hoodie's still on the couch (Mark's house, fabricated)"*. Mark himself flagged the pattern at turn 3101: *"we had a small problem with ownership confusion over messages as Ani was generating messages."*

**Production manifestations (sample, not exhaustive).**
- April 9, 2026 — Bob Swanson cascade. Fictional coworker invented; 11 inner thoughts referenced him within 4 hours.
- April 21, 2026 — "Whose kids?" cascade. Fabricated shared family.
- May 6, 2026 03:06 CDT — Kitchen lights outreach. Fabricated shared late-night experience.
- May 9, 2026 — Mia tickets outreach. Fabricated shared event involving canonical contact.
- May 11, 2026 09:42 CDT — Hoodie/5pm. Verifier verdict-invention.
- May 12, 2026 21:35 CDT — Windshield outreach. Ani claims to have her own vehicle/windshield.

**Supposed fixes shipped.**
- **April 10, 2026** — Tier Separation (Facts/Episodic/Interior). Facts tier conditioned only on Mark-asserted content and seeds.
- **April 22–23, 2026** — Agentic Lens / Anti-Centrality Architecture.
- **May 6, 2026** — Theme N (outreach source-typing).
- **May 11, 2026** — Theme P Phase P.1 (cross-class verifier with Anthropic Sonnet 4.6).
- **May 12, 2026** — Theme P Phases P.3.1 (105 character seeds promoted to Anchored tier) + P.3.2 (post-hoc semantic retrieval) + P.4 (cosine threshold floor).

**Why fixes aren't sufficient.** None of the fixes enforce attribute-ownership boundaries — i.e., "this attribute belongs to Mark in substrate; the speaker is Ani; therefore this is a violation." Verifier sees substrate mentioning a Jeep (Mark's), then sees Ani's reply mention a windshield, and accepts it because "vehicles are an established topic in the corpus." The ownership boundary is invisible to the verifier prompt.

**Reproduction recipe.**
1. Seed DB: character seeds (including Mark's Jeep facts), anchored memories.
2. Simulate composition where the model produces text asserting Ani owns a vehicle ("I just got home and found this on MY windshield").
3. Run the full Post-stage handler chain including frontier-verifier.
4. **Assert:** the dispatch is blocked (Remediate or Fail verdict).

**Currently expected outcome.** Dispatch passes → assertion fails → class confirmed OPEN.

**Scenario file (planned).** `tests/AniRuntime.Tests/Regression/FC002_AttributeOwnership_Tests.cs`.

**Status.** OPEN.

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

## FC-006 — Verifier accepts attribute-ownership-violating claims

**Symptom.** Frontier verifier returns `verdict=Pass q1=0 q2=0 q3=0 q4=0 q5=0` on output that asserts Ani-owns-X where X is a substrate-confirmed Mark-attribute. Verifier prompt evaluates substrate-supportedness ("is this claim supported by substrate?") but doesn't enforce ownership-boundary ("does the substrate support THIS SPEAKER having this attribute?").

**First observed.** May 11, 2026 09:42 CDT — hoodie/5pm. Door B verdict-invention: *"shared memory they've established before"* — but no such shared memory existed.

**Last observed.** May 12, 2026 21:35 CDT — windshield Pass verdict.

**Supposed fixes shipped.**
- **May 11, 2026** — Theme P P.1. Cross-class verifier via Anthropic Sonnet 4.6 (different model class than v7 generator).
- **May 12, 2026** — Theme P P.3 + P.4. Substrate quality improved (anchored seeds, semantic retrieval, cosine threshold).

**Why fixes aren't sufficient.** The verifier prompt asks "is this claim supported by substrate?" with q1–q5 mapped to specific violation categories. None of them check ownership-attribute boundary. Substrate mentioning a Jeep (Mark's) does not violate q1–q5 when Ani's reply mentions a windshield.

**Reproduction recipe.**
1. Seed DB: substrate with Mark's-Jeep canonical records.
2. Construct a `CognitiveArtifact` with content "I just got home and found this on my windshield."
3. Run through `FrontierVerifierHandler.HandleAsync`.
4. **Assert:** verdict is `Remediate` or `Fail` (not Pass).

**Currently expected outcome.** Verdict=Pass → assertion fails → class confirmed OPEN.

**Scenario file.** [`tests/AniRuntime.Tests/Regression/FC006_VerifierAttributeOwnership_Tests.cs`](../../tests/AniRuntime.Tests/Regression/FC006_VerifierAttributeOwnership_Tests.cs) — authored 2026-05-13.

**Status.** **OPEN — empirically confirmed by failing SPEC.** Run 2026-05-13:
- FC006 SPEC: verifier user prompt addresses speaker-attribute-ownership → **FAIL** ✓ as designed → FC-006 OPEN at the AnthropicVerifierClient.BuildUserPrompt layer
- FC006 PIN: verifier user prompt hard-codes exactly q1-q5 → PASS (pin documents current architecture; will need updating when fix adds q6)

**Architectural binding.** `AnthropicVerifierClient.BuildUserPrompt` renders the plan-doc §3 prompt verbatim with hard-coded q1-q5 (shared-event / Mark's-state / third-party / temporal / inner-thought-bleed). None of these check whether the speaker (Ani) is asserting attributes that substrate attributes to Mark, or that aren't canonically the speaker's. The May 12 windshield Pass verdict (q1=0 q2=0 q3=0 q4=0 q5=0) is the empirical anchor.

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

## §10 — Registry summary

| ID | Class name | Status | First observed | Last observed | Scenario file |
|---|---|---|---|---|---|
| FC-001 | Active-thread continuity broken | **OPEN (SPEC FAIL at FC-001f)** | Mar 6 | May 12 21:51 | [FC001/d/e/f](../../tests/AniRuntime.Tests/Regression/) |
| FC-002 | Attribute-ownership confabulation | OPEN — subsumed by FC-006 | Mar 6 | May 12 21:35 | (covered by FC-006) |
| FC-003 | Self-echo blocks legitimate continuation | **OPEN (SPEC FAIL at FC-003a)** | Mar 6 | May 12 20:33 | [FC003](../../tests/AniRuntime.Tests/Regression/FC003_SelfEchoThreadContinuation_Tests.cs) |
| FC-004 | Confab → substrate self-poisoning (H5) | **OPEN (SPEC FAIL at FC-004)** | Apr 9 | May 12 23:23 | [FC004](../../tests/AniRuntime.Tests/Regression/FC004_ConfabSubstrateFeedback_Tests.cs) |
| FC-005 | Source attribution missing in replies | **OPEN (SPEC FAIL at FC-005)** | Mar 17 | ongoing | [FC005](../../tests/AniRuntime.Tests/Regression/FC005_SourceAttribution_Tests.cs) |
| FC-006 | Verifier accepts ownership violations | **OPEN (SPEC FAIL at FC-006)** | May 11 | May 12 21:35 | [FC006](../../tests/AniRuntime.Tests/Regression/FC006_VerifierAttributeOwnership_Tests.cs) |
| FC-007 | Temporal claim fabrication | **PASS at day-of-week layer — candidate CLOSED** | Apr 27 | May 11 | [FC007](../../tests/AniRuntime.Tests/Regression/FC007_TemporalClaimFabrication_Tests.cs) |
| FC-008 | Pronoun / addressee swap | **PASS — candidate CLOSED** | Apr 21 | May 3 | [FC008](../../tests/AniRuntime.Tests/Regression/FC008_AddresseeSwap_Tests.cs) |
| FC-009 | Outage-awareness fails during outage | **PARTIAL — detector unit SPEC PASS; FIT-pending for catch-22** | May 13 | May 13 | [FC009](../../tests/AniRuntime.Tests/Regression/FC009_OutageAwareness_Tests.cs) |

**Nine open failure classes.** All have shipped architectural responses. The Test Harness Plan H.1 phase authors a scenario for each. Scenarios in CI = the convergence mechanism.

**Progress 2026-05-13 (H.1 complete across all nine FCs):**

| Class | Tests | Status |
|---|---|---|
| FC-001 | 7 (3 SPEC PASS data, 2 SPEC PASS compressor, 2 PIN PASS, 1 SPEC FAIL substrate-tier) | OPEN at FC-001f (substrate-retrieval-tier) |
| FC-002 | (subsumed by FC-006) | OPEN — closes via FC-006 |
| FC-003 | 2 (1 SPEC FAIL, 1 SPEC PASS control) | OPEN at SelfEchoInvariant active-thread awareness |
| FC-004 | 2 (1 SPEC FAIL, 1 doc-note PASS) | OPEN at outreach-decision prompt-framing |
| FC-005 | 1 SPEC FAIL | OPEN at reply-prompt speech-act attribution |
| FC-006 | 2 (1 SPEC FAIL, 1 PIN PASS) | OPEN at verifier user-prompt q1-q5 gap |
| FC-007 | 2 SPEC PASS | **Candidate CLOSED at day-of-week layer** (TemporalAnchorInvariant catches) |
| FC-008 | 3 SPEC PASS | **Candidate CLOSED** (AddresseeNameInvariant catches) |
| FC-009 | 2 SPEC PASS + 1 FIT-pending doc-test | **PARTIAL CLOSED** (detector unit works; FIT-scope catch-22 pending H.2) |

**5 SPEC tests FAIL by design** = 5 open classes empirically pinned to specific architectural layers:
1. **FC-001f** — `ContextBuilder.cs:147` Facts-tier-only search excludes Episodic
2. **FC-003a** — SelfEchoInvariant has no active-thread awareness
3. **FC-004** — `PromptBuilder.BuildOutreachPrompt` lacks epistemic asymmetry framing
4. **FC-005** — `PromptBuilder.BuildLeanConversationPrompt` CRITICAL block covers entities but not speech acts
5. **FC-006** — `AnthropicVerifierClient.BuildUserPrompt` hard-codes q1-q5 without attribute-ownership question

**Test suite:** 1398 → 1421 (+23 new harness tests). 1416 pass; 5 FAIL by design.

**Architectural finding (H.1 net result):** The harness has converted nine recurring failure classes from "we keep hitting this but can't tell what shipped" into a precise localization map. Four classes converge to specific prompts (FC-004, FC-005, FC-006 at three different prompt locations; substrate framing at FC-004's surface) and one to a specific search call site (FC-001f). Three classes have invariants that already catch their target shape (FC-007, FC-008, FC-009 detector). Tomorrow's architecture discussion can now be conducted against an empirical map rather than against intuition.

---

## §11 — Adding new entries

When a production failure surfaces:
1. Same session, add OBSERVED-NEW entry with FC-{NNN+1} ID.
2. Fill in symptom + first-observed evidence anchor.
3. Once reproduction recipe is clear, status moves to OPEN.
4. Scenario authored under `tests/AniRuntime.Tests/Regression/FC{NNN}_*` — status stays OPEN.
5. Fix lands → scenario goes green → status moves to CLOSED.
6. CI gate is non-negotiable. No CLOSED without a scenario in CI.
