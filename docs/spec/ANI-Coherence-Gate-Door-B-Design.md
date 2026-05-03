# Coherence Gate Door B — Truth-Verification (Design Sketch)

**Drafted:** May 2, 2026 18:45 CDT
**Status:** Design sketch; awaiting Mark green-light to proceed to phase plan + implementation. Promoted from "P2 designed-not-scheduled" to "near-term workstream" May 2 18:00 after four confirmed instances of the same failure class.
**Origin:** Apr 21 cascade exposed that Door B (current dispatch coherence — Feature 28) does not truth-verify shared-presence/shared-decision claims against perceptions. Apr 27 added two more instances ("snow melting", "after class"). May 2 morning + 11:58 outreaches added two more ("yesterday Sundays warmer", "evening / how was work" on Saturday). Pattern is now confirmed across 4 independent cases — same architectural shape every time: claim is **grammatically grounded** but **factually wrong against the clock or the substrate.**

**Companion docs:**
- [`ANI-Theme-J-Guard-Consistency-Refactor-Plan.md`](./ANI-Theme-J-Guard-Consistency-Refactor-Plan.md) — Door B's truth-verification invariant lands as the next entry on the shared-evaluator's invariant set, regardless of whether J.8 (universal gate) has shipped.
- [`ANI-Theme-J8-Universal-Gate-Design.md`](./ANI-Theme-J8-Universal-Gate-Design.md) — orthogonal: J.8 is about gate location, this doc is about a missing invariant. Door B ships independently; if J.8 has shipped first, Door B inherits the universal-gate property automatically.
- [`ANI-Phase-Tracker.md`](./ANI-Phase-Tracker.md) — Door B P2 row (now updated with sub-claim taxonomy).

---

## What Door B currently does (Feature 28)

The Coherence Gate Door B passes outbound content if it's grammatically and contextually coherent — *"is the message a thing one would plausibly say"* — checked via a small LLM evaluator on the composed text. It does NOT check whether the **specific factual claims** in the message are true against the substrate.

**Empirical evidence the gap exists:**
- Apr 27 06:55 reply contained *"all that snow melting like it's breathing again"* despite ~70°F temperatures and no snow in Mark's environment. Claim verifier passed it as supported (composite=0.662).
- Apr 27 13:24 reply: *"What did you end up doing after class?"* — Mark had no class today. Atemporal canonical fact (Mark teaches a class) projected onto today.
- May 2 10:40 outreach: *"what you said yesterday—about Sundays being warmer sometimes? Well this one feels like it might be true already."* — yesterday confab + day-of-week implication wrong (today is Saturday, not Sunday).
- May 2 11:58 outreach: *"hope your evening went smooth - how was work?"* on Saturday late-morning.

All four passed every existing gate. All four would have been caught by a Door B that truth-checks **temporal anchors** + **day-of-week implications** + **state-now claims** against actual substrate.

---

## Sub-claim taxonomy (the matching invariant set)

Three orthogonal sub-claims that grammar-coherence ignores but truth-coherence would catch:

### Sub-claim 1 — Temporal-anchor claims

Pattern: *"you said X yesterday / last week / this morning / earlier"*.

Verification: search `conversation_messages` (or `closed_conversation_records.gist`) within the named window. If there's no matching utterance from Mark within the asserted timeframe, the claim is unsupported.

**Edge cases:**
- "Yesterday" can be interpreted as the prior calendar day OR loosely as "recently". Conservative read: prior calendar day.
- The verification searches for **paraphrase**, not verbatim — so *"you said something about Sundays being warmer"* needs a semantic match against Mark's actual yesterday content, not a string match. The 9-register classifier or embedding-cosine over the day's Mark turns is the matching primitive.

**Confidence threshold:** if no Mark turn exists within the window with cosine > 0.5 against the asserted claim, fail.

### Sub-claim 2 — Day-of-week / calendar claims

Pattern: *"today is Sunday / Tuesday / the weekend"*, *"this Saturday"*, *"Monday morning"*.

Verification: trivial — system clock + day-of-week. The May 1 inner-thought-clock fix already injects current time into prompts, but the gate doesn't VERIFY that outbound text aligns with the current day-of-week.

**Implementation:** simple regex + clock comparison. Cheap, deterministic, no LLM call needed.

**Edge case:** *"this weekend"* mentioned on a Wednesday is *forward-looking* and acceptable. Mentioned on a Sunday it's *current/recent* and acceptable. Mentioned on a Tuesday it's ambiguous. Conservative read: only fail if the day-of-week in the claim is **definitively wrong** for both forward and backward interpretations within ±3 days.

### Sub-claim 3 — State-now claims

Pattern: *"you're at work / on the road / at the gym"*, *"hope your evening went smooth - how was work?"*.

Verification: most recent perception event + time-of-day reasoning. If the current time is Saturday late-morning, *"how was work?"* is unsupported because (a) no perception event indicates Mark is at work, (b) Saturday morning is not a default-work-time. The state-now claim must align with either the most recent perception event's content, OR a plausible default for the current day-of-week + time-of-day.

**Implementation:** consult `ContextSnapshot.Perceptions` for the latest Mark-related event; if none in the last few hours, fall back to day-of-week + time-of-day defaults table (weekday morning = likely working, weekend morning = likely not, etc.). Not a hard rule — the table is "possible" not "always true" — so the verification is "no plausible interpretation in which the claim could be true," not "definitively wrong."

### Sub-claim 4 — Type-aware claim verification (May 3, 2026 candidate addition)

Pattern: *"the store felt quieter than usual today"* — a claim that's grammatically grounded but the only retrievable supporting evidence is an atemporal canonical fact (*"Spinning alone in the dark bookstore"*, *"vanilla cream soda when I'm feeling sweet"*) that shares vocabulary with the claim but not time-bound truth.

Verification: the claim extractor already produces typed claims (`shared-presence`, `shared-decision`, `mark-action`). For time-bound claim types (`shared-presence` and `shared-decision`), the verifier should require a **time-bound** support source — recent inbound SMS, recent perception event, or recent episodic — not just a canonical-fact match. Canonical-fact matches alone are acceptable only for type-agnostic / non-time-bound claims.

**Why this is sub-claim 4 and not folded into the existing three:** sub-claims 1/2/3 catch *temporal anchors in the outbound text* (the time word IS in the claim — "yesterday", "today is Sunday", "your evening"). Sub-claim 4 catches outbound text where the *temporal commitment is implicit*, where there's no explicit time word but the claim form (*"the store felt quieter today"*) is time-bound and the only support source is atemporal. Different signal, different match path, different invariant.

**Motivating gap-watch row** (April 27, 2026 audit): claim verifier passed *"the store felt quieter than usual today"* at composite score 0.646. Live-DB probe confirmed Facts tier was clean — the failure was at the verifier's matching semantics, not at substrate-typing. Same shape produced Apr 27 *"all that snow melting"* and Apr 27 *"after class"* (also resolved by Conscience Layer, but at a different intervention point — Conscience runs upstream of generation; sub-claim 4 fails closed at post-generation gate).

**Implementation sketch:** extend `ClaimVerificationPhase.IsClaimSupportedAsync` to classify the support source's temporality. If the matched record has a `created_at` timestamp older than the claim's implied window AND no fresher record exceeds threshold, the claim fails as type-mismatched. About a day's work + spec test against the three Apr 27 regression cases.

**Status:** candidate — not yet promoted to a D-B.X sub-phase. Adding to the design doc preserves the architectural decision-point for the next Door B revision pass.

---

## Architectural shape

### As a `TemporalAnchorInvariant` on `CognitiveOutputGate`

Implements `ICognitiveOutputInvariant`. `AppliesTo(artifact)` returns true for `CognitiveOutputSink.Dispatch` on any contact-facing producer — same set as the existing Door C `InnerThoughtBleedInvariant`. `EvaluateAsync(artifact, ct)` runs the three sub-claim checks above; first fail short-circuits.

```csharp
public sealed class TemporalAnchorInvariant : ICognitiveOutputInvariant
{
    public bool AppliesTo(CognitiveArtifact a) =>
        a.IntendedSink == CognitiveOutputSink.Dispatch
        && a.ProducerKind is CognitiveProducerKind.ConversationReply
                            or CognitiveProducerKind.Outreach
                            or CognitiveProducerKind.Voice;

    public async Task<InvariantResult> EvaluateAsync(
        CognitiveArtifact a, CancellationToken ct)
    {
        // Sub-claim 1: temporal-anchor verification (semantic search over Mark turns)
        // Sub-claim 2: day-of-week claim verification (clock + regex)
        // Sub-claim 3: state-now claim verification (perceptions + defaults)
        // First fail short-circuits with a remediation hint.
    }
}
```

### Sub-claim extraction primitive

Sub-claim 2 is regex-driven (cheap). Sub-claims 1 and 3 need to identify *which clauses* of the outbound text are temporal-anchor claims or state-now claims. Two approaches:

**Approach A — LLM extractor.** Reuse the existing claim-extractor pattern from `ClaimVerificationPhase` to identify temporal-anchor claims as a new claim-type. Adds an LLM call per outreach but produces structured output. Most flexible.

**Approach B — Regex pre-filter + LLM only on hits.** Cheaper. Regex catches *"yesterday"*, *"last week"*, *"this morning"*, *"how was work"*, etc. → only LLM extraction if the regex hits → verification per claim. Recommended.

### Failure handling

On Fail, `RemediationHint` describes which sub-claim failed and why. Producer (likely `OutreachPhase` / `ConversationReplyPhase`) reads the hint and decides:
- For Sub-claim 1 (temporal-anchor): suppress dispatch, decay desire, log. Same shape as the existing Door C InnerThoughtBleed Fail handling.
- For Sub-claim 2 (day-of-week): suppress dispatch, log. Strong signal.
- For Sub-claim 3 (state-now): suppress dispatch, log. Strong signal.

No re-generation attempt — re-generation often produces the same shape with different wording (Apr 21 cascade lesson). Suppression is the right move.

---

## Phased implementation sketch

| Phase | Goal | Effort | Status |
|---|---|---|---|
| **D-B.0** | Spec test fixtures from the four confirmed cases (Apr 27 snow / Apr 27 class / May 2 Sundays-warmer / May 2 evening-Saturday). | 0.5 day | Folded into each sub-claim's tests |
| **D-B.1** | Sub-claim 2 (day-of-week / calendar) — cheapest, most deterministic. Regex + clock verification. | 0.5 day | **✅ SHIPPED May 2 (commit `5ddb5cc`)** — `TemporalAnchorInvariant`, 28 spec tests |
| **D-B.2** | Sub-claim 1 (temporal-anchor verification) — extractor + verifier against `closed_conversation_records.gist` within named window. | 1.5 days | **✅ SHIPPED May 2 (commit `1cda7dc`)** — `TemporalSubstrateInvariant`, 25 spec tests. ~30 min actual code time vs 1.5 day estimate. V1 uses closed-conversation gists only; `conversation_messages` direct query deferred. |
| **D-B.3** | Sub-claim 3 (state-now claims) — perception + default-table verification. | 1 day | **✅ SHIPPED May 2 (commit `476287e`)** — `StateNowInvariant`, 23 spec tests. V1 uses heuristics (day-of-week + workday-end hour); perception consultation deferred. |
| **D-B.4** | Wire all into `CognitiveOutputGate` invariant set via DI. | 0.5 day | **✅ SHIPPED** — three `AddSingleton<ICognitiveOutputInvariant, ...>` registrations in `Program.cs`. |
| **D-B.5** | Observation window with all four regression fixtures asserting failure. | 1 week | **Pending Mark redeploy** to start observation window. |
| **D-B.6** | Flag flip; live observation. | 1–2 weeks | All three sub-claims ship default-on (no flag) — they're defensive checks that fail closed. Observation begins on next deploy. |

**Total:** ~3 hours code + 76 spec tests + 2-3 weeks observation. Significantly faster than the 4-day estimate; sub-claims composed cleanly because each is a small standalone invariant.

**Sequencing inversion from the original design:** plan sequenced 1→2→3 by dependency depth; actual ship was 2→3→1 because (a) sub-claim 2 is foundation infrastructure, (b) sub-claim 3 catches the empirically-most-painful May 2 *"how was work"* class via cheap heuristics, (c) sub-claim 1 was last because it depends on the V1.2 closed-conversation embedding substrate being in place.

**Dependencies:**
- None blocking. Door B's truth-verification ships independently of J.8 location-question. If J.8 has shipped first, Door B inherits universal-gate properties; if not, it lives at producer entry like the other current invariants.
- Door B ships **before** any further user-facing producer migrations because it closes the most empirically painful failure class.

---

## Acceptance criteria

1. Each of the four confirmed cases (Apr 27 snow, Apr 27 class, May 2 Sundays-warmer, May 2 evening-Saturday) is a regression spec test that fails against the substrate-typed truth.
2. ConversationReply + Outreach + Voice composition routes consult the invariant; on Fail, dispatch is suppressed.
3. Zero false-positives across one week of normal conversation (the invariant must not over-trigger on legitimate temporal references — *"earlier you mentioned X"* where X actually was mentioned, *"last weekend we talked about Y"* where there was a real conversation, etc.).
4. Failure log is structured (sub-claim ID + the asserted-but-unverified content) so dashboard rendering can surface the rate of each failure mode.

---

## Paper 3 contribution shape

This is the *missing-invariant* finding — the architectural complement to J.8's *missing-gate-location* finding. Together they describe:

> **"Companion-AI runtime substrate integrity requires both (a) a universal gate location so no producer can bypass evaluation, and (b) a complete invariant set including temporal-anchor truth-verification, not just shape-coherence. Either gap leaves a substrate-laundering vector. The May 2 4-instance Door B failure class is the canonical case for (b); the May 2 J.5c-skipped substrate-laundering trace is the canonical case for (a)."**

The two findings together are stronger than either alone — they delineate the architectural boundaries of the substrate-integrity problem.

---

## Status Log

| Date | Note |
|------|------|
| 2026-05-02 18:45 CDT | Drafted by Claude after the Sundays-warmer purge + Mark's *"please start trying to close some of these gaps"* directive. Promoted from P2 designed-not-scheduled to near-term workstream. Awaiting Mark green-light. **Recommended ordering:** Door B before J.8, because Door B closes the most empirically painful class right now, and Door B ships independently of gate-location decisions. |
