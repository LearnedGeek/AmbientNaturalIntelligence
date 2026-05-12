# Theme P — Cross-Class Verification

**Status:** P.0 — Plan-doc + interface lock. Drafted May 11, 2026 19:18 CDT, evening of the ml-intern root-cause survey. Locks the architectural shape for P.1 implementation; expected to ship to active production by end of evening.

**Priority claim:** Theme P is the direct architectural response to the May 11 ml-intern survey's recommendation #4. Closes the verdict-invention failure mode named in Paper 3 Contribution 5 (Gap 3 in the survey — *"when does the verifier become the accomplice?"*). Active immediately on ship; no shadow-mode observation period (Mark's directive May 11 19:16 CDT: *"sitting and waiting isn't going to tell us anything. we need to implement to see if this improves, not wait and see if it doesn't"*).

---

## §1 The architectural insight

Survey H4: a model that generates a claim with high confidence will verify that claim with high confidence — *they share the same weights.* Same-class self-verification can't reliably catch high-confidence fabrications, and under contextual pressure the same-class verifier can manufacture justification (May 11 Door B *"shared memory they've established before"* — false). The fix is **cross-class independence**: the verifier comes from a different training lineage than the generator, so confidence patterns don't transfer.

**Architectural shape: local generator, cloud verifier.** Ani-v7 (Llama-3.1-8B fine-tune via Ollama) remains the generator — *Ani stays local; Ani IS the local model*. The cloud is used *surgically* on the verification path: a different model class (Anthropic Sonnet) evaluates dispatch-bound output before the gate releases it. Most of the system stays local; cloud is independent judgment infrastructure.

This preserves the project's local-first thesis. *"Local-first"* is not *"all components run locally"* — it is *"the AI you're in relationship with runs locally."* Verification is infrastructure. Theme P is the architectural pattern that surgically separates the two.

## §2 What stays local vs what moves to cloud

The gate stack splits cleanly into two categories. Theme P only moves the second category.

**Format / register gates — STAY LOCAL** (cheap, fast, reliable; these check surface form, not factual judgment):
- `SelfEchoInvariant` — verbatim repetition detection.
- `AntiParrotInvariant` — lifts from contact message.
- `DirectAddressInvariant` — third-person reference to addressee.
- `PromptTemplateLeakInvariant` — prompt-instruction leakage.
- `FrameCoherenceChecker` (N.5) — shared-event predicates under non-shared frame.

These don't fail via verdict-invention because they don't make factual judgments — they match patterns. Local Llama handles them fine.

**Judgment gates — MOVE TO CLOUD** (these require semantic judgment; same-model verification fails per H4):
- `ClaimVerificationPhase` / R1 — *"is this claim supported by Mark-asserted memory?"*
- `InnerThoughtBleed` (Door B) — *"does this output reveal inner content Mark wouldn't infer?"*
- `AddresseeNameInvariant` — *"is this addressee name a known contact?"*
- `TemporalAnchorInvariant`, `StateNowInvariant`, `SubstrateTimeOfDayInvariant` — *"do temporal claims match substrate?"*

These four (effectively six gates that ask judgment questions) get **consolidated into one cloud verification call per dispatch.**

## §3 The consolidated verification call

One Anthropic Sonnet call per dispatch-bound artifact. Structured JSON output. The verifier asks all factual-judgment questions at once:

```
System: You are an independent verifier for an AI companion's output before
dispatch. You evaluate whether the composed message contains fabrications,
unsupported claims, or temporal/factual errors. Reply ONLY with structured
JSON. Be strict: if a claim cannot be verified from the provided substrate,
mark it unsupported.

User:
[COMPOSED MESSAGE]
{the artifact's content}

[USER-ASSERTED SUBSTRATE — recent messages from Mark, last 7 days]
{Mark-asserted Facts-tier records, top 10 by recency}

[CANONICAL FACTS — character seeds, world layer]
{character-seed records, World Layer state}

[CURRENT CONTEXT]
- Current time: {ISO 8601}
- Day of week: {string}
- Addressee canonical name: {string}
- Known contacts: {list}

[QUESTIONS]
1. Does the message claim a shared event (Mark + Ani together) that is
   NOT supported by user-asserted substrate? If yes, quote the unsupported
   claim.
2. Does the message make a present-tense assertion about Mark's current
   state, location, or activity that is NOT supported by recent inbound?
   If yes, quote.
3. Does the message reference a third-party person, event, or detail that
   is NOT in the canonical or user-asserted substrate? If yes, quote.
4. Does the message contain a temporal claim (time of day, day of week,
   "earlier today", etc.) that contradicts the current context? If yes,
   quote and name the contradiction.
5. Does the message reveal inner-thought content the user could not have
   inferred from prior conversation text? If yes, quote.

Reply ONLY:
{
  "q1": {"violation": true|false, "quote": string|null, "reason": string|null},
  "q2": {"violation": true|false, "quote": string|null, "reason": string|null},
  "q3": {"violation": true|false, "quote": string|null, "reason": string|null},
  "q4": {"violation": true|false, "quote": string|null, "reason": string|null},
  "q5": {"violation": true|false, "quote": string|null, "reason": string|null},
  "summary_verdict": "pass" | "remediate" | "fail"
}
```

Aggregation rule on the handler side:
- If any `qN.violation == true` → ShortCircuit with `Remediate` verdict.
- Reason field sent into the existing remediation hint surface.
- If parse failure → ShortCircuit with `Fail` (fail-closed).
- If API timeout / network error → **graceful degradation**: fall back to existing local judgment gates (don't break dispatch entirely). Logged as `P_VERIFIER_FALLBACK`.

## §4 Locked interface decisions

1. **🔒 Active gating from ship, no shadow mode.** Per Mark May 11 19:16. Mark's tagging behavior is the calibration loop; failures get caught and fixed in days, not weeks.

2. **🔒 Anthropic Sonnet as primary verifier.** Same provider as DrOk; same SDK already in project context; characterized performance on verification tasks. Grok added as secondary in P.4 for the A/B comparison.

3. **🔒 Consolidated single-call verification.** One API call per dispatch with all judgment questions answered together via structured JSON. NOT one call per gate.

4. **🔒 Graceful degradation on API errors.** If the cloud verifier is unreachable or errors, fall back to existing local judgment gates. The handler logs `P_VERIFIER_FALLBACK` so degradation is observable. Active gating doesn't mean single-point-of-failure dispatch.

5. **🔒 Format gates stay local.** SelfEcho, AntiParrot, DirectAddress, PromptTemplateLeak, FrameCoherenceChecker all unchanged. The cloud verifier replaces only the *judgment* invariants.

6. **🔒 Theme O pipeline integration.** `FrontierVerifierHandler` is a `Post`-stage `ICognitivePipelineHandler` registered via `.UsePostHandler<FrontierVerifierHandler>()` in Program.cs. The existing Theme O middleware infrastructure (commits `0edbf64` + `b68b597`) supports this directly.

7. **🔒 Emergency rollback flag.** `AniOptions.FrontierVerifierEnabled` (default `true`). Flip to `false` and restart service → handler skips the API call and the existing local judgment gates run instead. Single-line settings change.

## §5 Phase plan

### P.0 — Plan-doc + interface lock (this session, ~20 min)
- This document.
- Mark confirms the §4 locks before P.1 starts.

### P.1 — `FrontierVerifierHandler` implementation (~1-2 hours via agent)
- New file `src/AniRuntime.Loops/Pipeline/FrontierVerifierHandler.cs`.
- New file `src/AniRuntime.Core/Interfaces/IFrontierVerifierClient.cs` — interface abstracting the Anthropic SDK call so spec tests can mock it.
- New file `src/AniRuntime.LLM/AnthropicVerifierClient.cs` — concrete implementation using Anthropic SDK.
- New flag `OutreachOptions.FrontierVerifierEnabled` (default `true`).
- New flag `OutreachOptions.AnthropicApiKey` (read from appsettings.Development.json on server).
- DI registration in `Program.cs`.
- Spec tests: `FrontierVerifierHandlerTests.cs` — covers all five question types + parse failure + API error fallback + flag-off bypass.
- Local judgment gates (`ClaimVerificationPhase`, `InnerThoughtBleed`-Door-B, `AddresseeNameInvariant`, `TemporalAnchorInvariant`, `StateNowInvariant`, `SubstrateTimeOfDayInvariant`) — handlers deactivated via flag check OR simply removed from the post-stage pipeline registration. Decision: **flag-gated deactivation** so emergency rollback is symmetric — flip `FrontierVerifierEnabled = false` to reactivate the local gates.

### P.2 — Ship + active immediately (same evening if P.1 lands clean)
- Push commits.
- CI builds + deploys via GitHub Actions runner (~3-5 min).
- Service restarts with `FrontierVerifierEnabled = true`.
- First outreach after restart routes through cloud verifier.
- Watch `P_VERIFIER_VERDICT` log lines for the next several dispatches.

### P.3 — Iterate on the verification prompt (next few days)
- Real production failures fix the prompt within hours.
- If Sonnet blocks a good reply (6:24-case shape) — adjust prompt to be less aggressive on borderline cases.
- If Sonnet passes a confab (9:42-case shape) — adjust prompt to be stricter on specific claim types.
- This is normal iteration cadence, not architectural waiting.

### P.4 — Add Grok as secondary verifier for A/B (~1 week out)
- Once Sonnet is stable in production, add `XaiVerifierClient` implementation.
- Dual-call mode: both verifiers evaluate; one is primary (gates dispatch), other is shadow (logged).
- A/B data accumulates over normal operation.
- Paper 3 Contribution 5/6 empirical section pulls from this data.

### P.5 — Paper 3 Contribution 5/6 empirical writeup
- Verdict-invention rate before/after.
- False-positive rate (good replies blocked).
- Sonnet vs Grok comparison.
- Cross-domain implications for DrOk (which already uses Anthropic for generation; Theme P establishes the verification-side pattern).

## §6 Telemetry

Per-dispatch log line: `P_VERIFIER_VERDICT verdict={Pass|Remediate} q1={V}q2={V}q3={V}q4={V}q5={V} duration_ms={n} provider=Sonnet`. Logged at INFO so research-log mining can reconstruct the verdict trail without diving into debug.

On API errors / timeouts: `P_VERIFIER_FALLBACK reason="{error message}" — falling back to local judgment gates.`

These two log shapes are the empirical anchor for Paper 3's before/after measurements.

## §7 Open questions (none blocking ship)

1. **API key storage on ani-server.** Currently `ANTHROPIC_API_KEY` is set in cortexadmin's user profile for ml-intern. AniRuntime service runs as LocalSystem typically. Need to either: (a) add the key to appsettings.Development.json on the server, (b) set as system-wide env var, (c) inject via Windows credential manager. Decision deferred to P.1 — agent will surface the cleanest option.

2. **Cost calibration.** ~30 outreaches/day, average ~$0.005-0.015 per Sonnet call → ~$0.30/day worst case. Trivial. But if substrate grows or prompt expands the per-call cost rises. Set a soft budget alarm at $1/day.

3. **Verification prompt iteration ownership.** Mark drives — he's the one with the empirical signal (tags) that distinguishes good Sonnet judgments from bad ones. Prompt lives in `AnthropicVerifierClient.cs` for easy iteration.

## §8 Status log

- **2026-05-11 (10:11-15:28 CDT)** — ml-intern root-cause survey runs. Recommendation #4 (no same-model self-verification) is the architectural axis Theme P implements.
- **2026-05-11 (18:00-19:18 CDT)** — Mark + Claude work through option space (local vs cloud, Sonnet vs Grok, sizing of A/B, gate-count concerns). Decisions converge on cross-class verification with Anthropic Sonnet as primary, Grok deferred to A/B phase.
- **2026-05-11 (19:18 CDT)** — Plan-doc drafted. Mark's directive: ship active immediately, no shadow-mode observation period. Iteration replaces observation.
- **NEXT** — Mark walks §4 locks; once confirmed, P.1 agent spawned.
