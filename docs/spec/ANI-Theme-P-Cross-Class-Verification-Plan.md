# Theme P — Cross-Class Verification

**Status:** P.0 — Plan-doc + interface lock. Drafted May 11, 2026 19:18 CDT, evening of the ml-intern root-cause survey. Locks the architectural shape for P.1 implementation; expected to ship to active production by end of evening.

**Priority claim:** Theme P is the direct architectural response to the May 11 ml-intern survey's recommendation #4. Closes the verdict-invention failure mode named in Paper 3 Contribution 5 (Gap 3 in the survey — *"when does the verifier become the accomplice?"*). Active immediately on ship; no shadow-mode observation period (Mark's directive May 11 19:16 CDT: *"sitting and waiting isn't going to tell us anything. we need to implement to see if this improves, not wait and see if it doesn't"*).

---

## §1 The architectural insight

Survey H4: a model that generates a claim with high confidence will verify that claim with high confidence — *they share the same weights.* Same-class self-verification can't reliably catch high-confidence fabrications, and under contextual pressure the same-class verifier can manufacture justification (May 11 Door B *"shared memory they've established before"* — false). The fix is **cross-class independence**: the verifier comes from a different training lineage than the generator, so confidence patterns don't transfer.

**Architectural shape: local generator, cloud verifier.** Ani-v7 (Llama-3.1-8B fine-tune via Ollama) remains the generator — *Ani stays local; Ani IS the local model*. The cloud is used *surgically* on the verification path: a different model class (Anthropic Sonnet) evaluates dispatch-bound output before the gate releases it. Most of the system stays local; cloud is independent judgment infrastructure.

This preserves the project's local-first thesis. *"Local-first"* is not *"all components run locally"* — it is *"the AI you're in relationship with runs locally."* Verification is infrastructure. Theme P is the architectural pattern that surgically separates the two.

## §2 What stays local, and what is added in the cloud

> **Architectural correction (May 11 21:36 CDT)** — see §9.1. The original §2 framing said *"judgment gates MOVE TO CLOUD"* (replacement). That framing forced flag-gating local invariants from inside the cloud handler's flag, which forced fallback semantics on cloud error, which forced substrate filtering in BuildRequest to keep the two stacks in sync. Three bandaids traceable to one architectural mistake. The corrected framing below is **additive**: the cloud verifier is defense-in-depth, not replacement.

**Everything that runs today STAYS LOCAL, UNCHANGED.**

Format / register gates — stay local because they check surface form, not factual judgment:
- `SelfEchoInvariant` — verbatim repetition detection.
- `AntiParrotInvariant` — lifts from contact message.
- `DirectAddressInvariant` — third-person reference to addressee.
- `PromptTemplateLeakInvariant` — prompt-instruction leakage.
- `FrameCoherenceChecker` (N.5) — shared-event predicates under non-shared frame.

Judgment gates — also stay local, also unchanged, also still firing on every dispatch:
- `ClaimVerificationPhase` / R1 — *"is this claim supported by Mark-asserted memory?"*
- `InnerThoughtBleed` (Door B) — *"does this output reveal inner content Mark wouldn't infer?"*
- `AddresseeNameInvariant` — *"is this addressee name a known contact?"*
- `TemporalAnchorInvariant`, `StateNowInvariant`, `SubstrateTimeOfDayInvariant` — *"do temporal claims match substrate?"*

**What Theme P ADDS is the FrontierVerifierHandler** — a Post-stage `ICognitivePipelineHandler` that runs in parallel to the existing local invariants on every dispatch-bound artifact. The handler routes the composed message through Anthropic Sonnet (different training class than the local Ani-v7 Llama generator) so a non-same-class verifier can catch the high-confidence fabrications local same-class verification gets fooled on per H4.

The two stacks fire **independently** on every dispatch:
- Local judgment invariants run as they do today. No flag-gating, no constructor changes, no `AppliesTo` rewrites. The hard-revert restored them to their pre-`afa2384` state and they stay that way.
- `FrontierVerifierHandler` runs additionally. It consults ONLY its own `FrontierVerifierEnabled` flag in its own `AppliesTo` — never reaches into other handlers' applicability.

Any short-circuit (from any handler, local or cloud) blocks dispatch. Cloud failure = no problem: local gates remain active. The two paths are not a replacement choice; they are layered defense.

This is the only framing under which substrate construction in the cloud handler can stay clean: the handler renders ONLY canonically-Mark-asserted records (`ContextSnapshot.GroundedFacts` Facts-tier records + `ContextSnapshot.AnchoredMemories` foundation records). No fallback to `RecentExchanges`. No role-filter on episodic records. No reading the artifact's `ContactRecentMessages`. If those sources aren't already shaped right, the architectural fix is to populate them right at retrieval-time, never to filter them at construction-time.

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

4. **🔒 Graceful degradation on API errors — simple because local gates are always active.** If the cloud verifier is unreachable or errors, the handler logs `P_VERIFIER_FALLBACK` and returns `Continue`. No special-case substrate routing is required: the existing local judgment gates have been running on the same dispatch in parallel, so the cloud's absence means defense-in-depth is reduced by one layer, not that judgment is silently skipped. Active gating doesn't mean single-point-of-failure dispatch.

5. **🔒 Format gates stay local.** SelfEcho, AntiParrot, DirectAddress, PromptTemplateLeak, FrameCoherenceChecker all unchanged. The cloud verifier ADDS a layer; it does not replace these (or any other) local invariants.

6. **🔒 Theme O pipeline integration.** `FrontierVerifierHandler` is a `Post`-stage `ICognitivePipelineHandler` registered via `.UsePostHandler<FrontierVerifierHandler>()` in Program.cs. The existing Theme O middleware infrastructure (commits `0edbf64` + `b68b597`) supports this directly. Position in the chain doesn't affect correctness — both stacks fire to completion absent a short-circuit; ordering only determines which handler short-circuits first on a multi-violation case.

7. **🔒 Emergency rollback flag — symmetric and clean.** `AniOptions.FrontierVerifierEnabled` (default `true`). Flip to `false` and restart service → ONLY the `FrontierVerifierHandler.AppliesTo` returns false; the cloud handler is skipped. Local judgment gates remain active either way (they never knew about the flag). The flag is a cloud-handler kill switch, not a path-routing toggle.

## §5 Phase plan

### P.0 — Plan-doc + interface lock (this session, ~20 min)
- This document.
- Mark confirms the §4 locks before P.1 starts.

### P.1 — `FrontierVerifierHandler` implementation (~1-2 hours via agent)
- New file `src/AniRuntime.Loops/Pipeline/FrontierVerifierHandler.cs`.
- New file `src/AniRuntime.Core/Interfaces/IFrontierVerifierClient.cs` — interface + DTOs abstracting the Anthropic call so spec tests can mock it.
- New file `src/AniRuntime.LLM/AnthropicOptions.cs` — config DTO.
- New file `src/AniRuntime.LLM/AnthropicVerifierClient.cs` — concrete implementation using raw HttpClient (no SDK dependency).
- New flag `AniOptions.FrontierVerifierEnabled` (default `true`).
- New `AnthropicOptions` section in appsettings.json (placeholder ApiKey; real key set on server in appsettings.Development.json).
- DI registration in `Program.cs` — `Configure<AnthropicOptions>` + `AddHttpClient<IFrontierVerifierClient, AnthropicVerifierClient>()` + append `.UsePostHandler<FrontierVerifierHandler>()` to the existing `AddCognitivePipeline` block.
- Spec tests: `FrontierVerifierHandlerTests.cs` — covers all five question types + parse failure + API error fallback + flag-off bypass + cancellation propagation + substrate forwarding.
- Local judgment gates (`ClaimVerificationPhase`, `InnerThoughtBleedInvariant`, `AddresseeNameInvariant`, `TemporalAnchorInvariant`, `StateNowInvariant`, `SubstrateTimeOfDayInvariant`) — UNCHANGED. No flag-gating. They continue to fire on every dispatch as they do today. The cloud verifier is additive defense, not replacement.

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
- **2026-05-11 (19:52 CDT)** — First P.1 implementation lands in commit `afa2384` (local-only, not pushed). The handler ships, but with three structural bandaids tracing back to the *"judgment gates MOVE TO CLOUD"* framing in the original §2: (1) the local invariants' `AppliesTo` predicates were rewritten to consult `FrontierVerifierEnabled` so the two stacks alternated rather than both firing; (2) substrate construction added a fallback to `ContextSnapshot.RecentExchanges` with role-filtering when the artifact's `ContactRecentMessages` was empty; (3) the constructors of six invariant classes (`InnerThoughtBleedInvariant`, `AddresseeNameInvariant`, `TemporalAnchorInvariant`, `StateNowInvariant`, `SubstrateTimeOfDayInvariant`, `ClaimVerificationPhase`) took on a new optional `IOptions<AniOptions>` dependency. Mark caught the bandaid pattern before push: *"no more bandaids… a failure now could kill this project to be honest."*
- **2026-05-11 (21:36 CDT)** — Hard-revert of `afa2384`. Plan-doc §2 corrected to additive framing (see §9.1). P.1 re-implemented as defense-in-depth: local invariants restored to their pre-`afa2384` state and stay there; the cloud verifier runs ADDITIONALLY as another post-stage handler in the Theme O pipeline.
- **NEXT** — Mark walks §4 locks (now simpler — locks 4 and 7 lost their fallback complications); once confirmed, deploy.

## §9 Architectural corrections

### §9.1 — May 11 21:36 CDT — additive framing replaces replacement framing

**What the original framing said:** §2 listed two categories of gates — "format gates STAY LOCAL" and "judgment gates MOVE TO CLOUD." The verb *move* was the load-bearing word. It implied replacement, which forced the implementation into a shape where exactly one stack ran per dispatch — cloud when the flag was true, local when the flag was false — and the boundary between the two was managed by a single shared flag (`FrontierVerifierEnabled`).

**Why that was wrong:** the H4 critique (same-model verification can't reliably catch high-confidence fabrications) is a defense-in-depth argument, not a replacement argument. A different verifier class catches *additional* fabrications — it doesn't make the existing local checks worse. Replacing local judgment with cloud judgment loses signal on every cloud outage and forces the rest of the system to defend against the loss (the fallback substrate route, the role-filtered RecentExchanges, the constructor changes on six invariants).

**The corrected framing — both stacks fire on every dispatch:**
1. Local judgment gates run as they have since Theme J. No flag-gating. No constructor changes. No `AppliesTo` rewrites. Their post-stage handler registrations are untouched.
2. `FrontierVerifierHandler` runs additionally. It is a separate post-stage handler with its own `AppliesTo` that consults ONLY `AniOptions.FrontierVerifierEnabled`. The flag never leaks into any other handler's behavior.
3. Any short-circuit (from any handler in either stack) blocks dispatch. Cloud failure means defense-in-depth is reduced by one layer; it does NOT mean judgment-tier coverage disappears for that dispatch.
4. Substrate construction in the cloud handler reads only canonically-Mark-asserted records — `ContextSnapshot.GroundedFacts` (Facts-tier records: character seeds, perception events, user-asserted content) and `ContextSnapshot.AnchoredMemories` (foundation memories that never fade). No fallback to `RecentExchanges`. No role-filter on Episodic records. No reading the artifact's `ContactRecentMessages`. If those canonical sources are empty, the prompt slot is empty — the right architectural fix is to populate them at retrieval-time, not to filter at construction-time.

**Why this future-proofs against the same trap:** any future-Claude (or future-Mark) reading §2 will see "additive defense in depth" instead of "judgment gates MOVE TO CLOUD" and won't be tempted into the same chain of bandaids. The flag-gating, the fallback, the substrate filtering — all three were downstream of a single architectural word choice. Fixing the word fixes the chain.
