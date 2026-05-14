# Test Harness Day-One Status — 2026-05-13

**For:** Mark's tonight-review.
**Author:** Claude.
**TL;DR:** Foundation laid; first failing regression scenario empirically proves a previously-recurring class is open via TDD discipline; one diagnostic finding from the day's work changed what we think the binding constraint of FC-001 actually is.

---

## §1 — What shipped today

### Documentation (canonical artifacts)
- [`docs/spec/ANI-Test-Harness-Plan.md`](./ANI-Test-Harness-Plan.md) — the load-bearing plan (drafted earlier this morning at your direction)
- [`docs/spec/ANI-Failure-Class-Registry.md`](./ANI-Failure-Class-Registry.md) — canonical enumeration of nine open failure classes (FC-001 through FC-009) with evidence anchors, supposed-fix history, reproduction recipes, and links to scenario files
- This status document — `ANI-Test-Harness-DayOne-Status.md`

### Harness scaffolding (production code unchanged)
- [`tests/AniRuntime.Tests/Regression/RegressionScenarioBase.cs`](../../tests/AniRuntime.Tests/Regression/RegressionScenarioBase.cs) — base class for regression scenarios: real `SqliteConversationService` against per-test in-memory SQLite, strict mocks for collaborators that don't need to be real for the scenario under test, helper methods for thread/message setup
- New folder: `tests/AniRuntime.Tests/Regression/` — segregated from existing unit tests; the regression-class harness lives here

### Scenarios authored
- [`tests/AniRuntime.Tests/Regression/FC001_ActiveThreadContinuity_Tests.cs`](../../tests/AniRuntime.Tests/Regression/FC001_ActiveThreadContinuity_Tests.cs) — 3 scenarios for FC-001 at data-layer scope
- [`tests/AniRuntime.Tests/Regression/FC003_SelfEchoThreadContinuation_Tests.cs`](../../tests/AniRuntime.Tests/Regression/FC003_SelfEchoThreadContinuation_Tests.cs) — 2 scenarios for FC-003 at invariant scope

### Persistent memory
- `feedback_harness_first_directive.md` — saved for future Claude instances; documents the no-themes/no-code-changes directive and lifecycle of the harness work

---

## §2 — Empirical results

Test suite delta: **1398 → 1403** (+5 new). Run 2026-05-13.

| Scenario | Result | Status meaning |
|---|---|---|
| FC001a — data-path round-trip | **PASS** | Data layer correctly round-trips a single Ani message |
| FC001b — multi-message ordering | **PASS** | Data layer preserves chronology |
| FC001c — Ani-then-Mark production sequence | **PASS** | Data layer correctly returns both messages on `GetActiveThreadAsync` |
| FC003a — opener repetition in active thread | **FAIL (by design)** | Confirms FC-003 OPEN — SelfEchoInvariant short-circuits on opener-repetition that SPEC says should be allowed during active-thread continuation |
| FC003b — full-content parrot (control) | **PASS** | Invariant correctly catches genuine parrot — control confirms the discipline isn't accidentally asking the invariant to be permissive across-the-board |

**Net signal: FC-003 is empirically OPEN with a failing harness scenario. The SPEC is documented. Fix work is deferred per the harness-first directive; when the fix lands, the test will go green without modification, proving empirical closure.** That's the convergence mechanism working as designed.

---

## §3 — Diagnostic finding worth reading

I expected FC-001 to fail at the data layer (windshield case had no prior outreach in next-reply's substrate — looked like a basic round-trip bug). It didn't. All three FC-001 scenarios PASS with synthetic data through the same `AddMessageAsync → GetActiveThreadAsync` round-trip the production code uses.

That means **FC-001 is real (production failure happened May 12 21:35–21:51 CDT) but it's NOT at the layer I named in my comprehensive review earlier today.** The bug is higher in the stack:
- ContextBuilder integration (snapshot construction)
- `_compressor.CompressIfNeededAsync` (MemGPT-style compressor at `ConversationReplyPhase.cs:194`)
- Semantic-search-keyed retrieval substrate (the separate pool that runs alongside chat history)
- Prompt construction (whether the Ani prior message reaches the final Ollama payload)

This is TDD discipline producing real value on day one. Three scenarios PASS = three sub-hypotheses about where FC-001 lives ruled out. The next scenarios to author (FC-001d/e/f) target the higher-stack layers. This is also the *first concrete instance* of the failure-class-localization workflow paying off: my pre-test diagnosis would have led to a fix at the wrong layer.

**Possible re-framing.** The May 12 windshield failure may have been a compound of FC-003 (self-echo blocking continuation after Ani's prior outreach echoed itself in composition) + FC-006 (verifier accepting Ani-owns-a-windshield as a Pass verdict) + FC-004 (the windshield confab fed back into a 23:23 outreach decision as established fact) rather than primarily FC-001. The FCR has been updated with this reframing as a diagnostic note.

---

## §4 — Where the harness goes next

Per the Test Harness Plan §6 phases:

**H.0 — Enumeration — COMPLETE.** Nine classes captured in the FCR with evidence.

**H.1 — Scenario authoring — STARTED, partial.** FC-001 (data-layer scope) + FC-003 (invariant scope) done. Remaining for H.1:
- FC-001d/e/f — higher-stack scenarios to localize FC-001's actual binding layer
- FC-002 — attribute-ownership confab at verifier scope (needs IFrontierVerifierClient mock pattern)
- FC-004 — substrate self-poisoning at retrieval scope (needs real SqliteMemoryService)
- FC-005 — source attribution missing in replies at reply-path scope
- FC-006 — verifier accepts ownership violations at AnthropicVerifierClient prompt scope
- FC-007 — temporal claim fabrication at invariant scope (probably small effort given existing invariants)
- FC-008 — pronoun/addressee swap at AddresseeNameInvariant scope (also small)
- FC-009 — outage-awareness fails during outage at FIT scope

**H.2 — FIT layer — NOT STARTED.** Ollama / SQLite / Anthropic / network failure injection.

**H.3 — SIT layer — NOT STARTED.** Cross-component substrate flow integration tests.

**H.4 — CI integration — NOT STARTED.** Will wire harness into existing GitHub Actions workflow.

**H.5 — Initial baseline — Partially done.** First two classes have empirical results. Remaining seven classes will produce theirs as scenarios are authored.

**H.6 — A/B harness (model-class diagnostic) — Deferred per plan §6. Only after H.0–H.5 are operational.**

---

## §5 — Operating-mode discipline I'm holding

While building the harness:

- **No production code changes** — even though several diagnostic findings today are tempting fix targets (FC-003 invariant logic, FC-001 higher-stack localization). Per the harness-first directive, those go into a Discovered-Issues list until the harness is operational and CI-gated.
- **TDD purity** — each scenario's assertion describes the SPEC the system should meet, not the current behavior. If the current code fails, the scenario is RED and the class is OPEN; the fix is deferred.
- **Fabricated test data** — every scenario uses synthetic content (e.g., `"FC001-FIXTURE: synthetic outreach about a fabricated windshield note"`). No production-data dependency.
- **Strict-mock policy per Theme K** — collaborators that don't need to be real for the scenario under test are mocked strictly.

---

## §6 — Discovered-Issues (per the plan's discipline)

Bugs surfaced during harness build go here, NOT into code changes:

1. **FC-001's binding layer is not the data layer.** Higher-stack scenarios (FC-001d/e/f) needed to localize properly. Will author tomorrow.
2. **FC-003 confirmed open at invariant level.** SelfEchoInvariant has no active-thread awareness; the fix likely requires either a metadata flag on `CognitiveArtifact` (e.g., `IsActiveThreadReply`) or position-aware token-run detection (opener-tolerance) or per-thread opener-count tracking. Decision deferred.
3. **Pre-existing flaky test:** `DebouncedUtteranceTests.AddSegment_SingleSegment_FiresAfterDebounce` is timing-dependent and intermittently fails in the full test run (passes when run alone). Voice-path test; not introduced by harness work. Worth flagging as a candidate for stabilization separately, but well outside the harness scope.

---

## §7 — End of day

Coming back to: a real plan doc, a real registry, a real harness scaffold, one fully-functional regression scenario that empirically proves a failure class is OPEN, and three more scenarios that localized FC-001 away from where I thought it lived. The convergence mechanism is starting.

The pattern from now on: every failure class gets a scenario before any fix lands. Scenarios in CI gate the project. Themes don't restart until classes are closing. The loop has somewhere to converge.
