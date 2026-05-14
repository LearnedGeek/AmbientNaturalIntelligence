# ANI Test Harness Plan — From Whack-a-Mole to Empirical Closure

**Status:** Drafted May 13, 2026. **Active and load-bearing.** Halts all theme/feature work until the FIT + SIT + regression-class harness is operational. Once operational, this harness gates all subsequent code changes via CI.

**Mark's directive (May 13, 2026 morning):** *"let's take the approach of FIT and SIT testing the exposed surfaces and doing nothing except for focusing on the testing harness. All movement from here forward needs to be focused on that specific goal. We do not create more code changes until we get that all in place, even if they reveal bugs in the interim."*

---

## §1 — Why this exists

Nine months of project history exhibit a recurring shape:

> Failure event in production → name a theme → ship architecture → brief observation window → new failure event of the same class under a different surface → name a new theme → repeat.

The loop has no convergence mechanism. The May 13 comprehensive review against `claude-recall` + research log + current code confirmed:

- **The same failure classes have been recurring since March 2026 despite explicit architectural responses to each manifestation.**
- **Mark's own words from March 6, 2026 (claude-recall turn 13259):** *"I thought we fixed that a long time ago."* Two months later that sentence still applies to multiple failure classes.
- **Component contracts are unit-tested rigorously** (1,398 tests, strict-mock TDD discipline, CI gate). **The integration boundaries where failures manifest are not tested.**
- **No theme in project history has been declared empirically closed** — i.e., no theme has shipped with a regression test that proves its target failure class can no longer recur. Themes ship → observe → next theme. No closure checkpoint.

The harness exists to make the loop converge. It is the convergence mechanism.

## §2 — Failure classes that have recurred (the evidence)

The following classes have appeared in production multiple times across multiple themes that supposedly addressed them. Each line is documented in research log + claude-recall transcripts.

| Failure class | First named | Last observed | Themes that "addressed" it | Status |
|---|---|---|---|---|
| **Attribute-ownership confab** (Ani claims to have something Mark has, or a shared thing Mark didn't assert) | Mar 6 — *"Your hoodie's still on the couch"* | May 12 night — windshield outreach | Tier separation (Apr 10), Agentic Lens (Apr 22), Theme N (May 6), Theme P (May 11) | OPEN |
| **Ani's outbound not visible in next reply's retrieval** (active thread continuity broken) | Mar 6 turn 12510 — explicit diagnosis | May 12 night — windshield reply hit empty substrate for prior outreach | Theme E `RecordOutboundInThreadAsync` (Apr 29) | OPEN |
| **Self-echo blocks legitimate thread continuation** | Mar 6 turn 5605 — *"echo guard never ran"* | May 12 night — mmm-baby parrot caught 3× | SelfEchoInvariant on universal gate (May 3) | OPEN |
| **Confab from one cycle becomes substrate for next cycle (H5 self-poisoning)** | Apr 9 Bob Swanson cascade — 11 inner thoughts referencing fabricated coworker in 4h | May 12 night — windshield confab cited as fact in 23:23 outreach decision | Substrate purges (Apr 28 / May 2 / May 4 / May 6), Tier separation | OPEN |
| **Source attribution missing in conversation replies** | Mar 17 turn — *"No source attribution check exists for conversation replies."* | Same shape as windshield case | Theme J source attribution (J.2 shipped Apr 27) | OPEN |
| **Verifier accepts attribute-ownership-violating claims** | May 11 hoodie/5pm verdict-invention | May 12 night windshield Pass verdict | Theme P P.1–P.4 | OPEN |
| **Temporal claim fabrication** (wrong time of day, day of week, "earlier") | Apr 27 snow, Apr 27 class, May 2 Sundays, May 3 it's-late, May 11 hoodie/5pm, May 12 morning | TemporalAnchorInvariant, StateNowInvariant, TemporalSubstrateInvariant, SubstrateTimeOfDayInvariant | OPEN |
| **Pronoun / addressee swap** | Apr 21 cascade, May 3 perez | AddresseeNameInvariant | OPEN |
| **Outage-awareness fails during outage** (the gap-detector runs inside the cycle it would inform) | May 13 morning — discovered, not yet named as recurrence | OutagePerceptionSource, temporal-gap detector | OPEN |

Nine classes, all open, every one with architecture shipped against it.

## §3 — Why current testing missed it

Tests verify *component contracts.* Failures manifest at *integration boundaries.* The category gap:

| Test category | Coverage today | Catches |
|---|---|---|
| Unit (strict-mock TDD) | Strong (1,398 tests) | "Component X behaves correctly given mocked dependencies" |
| Spec tests per theme | Strong | "Theme X's new component handles its named scenarios" |
| End-to-end conversation flow | **None** | "Given inbound A, reply contains expected reference to prior dispatch B" |
| Failure injection (FIT) | **None** | "Ollama unreachable → service degrades gracefully; embedding slow → cycle handles timeout; DB locked → startup retries" |
| System integration (SIT) | **None** | "Substrate flow from write to retrieval to consumer is verified for each producer→consumer pair" |
| Regression-class harness | **None** | "Each historically-named failure class is exercised by a scripted scenario; closed classes cannot re-open without CI failing" |

The gap isn't that we didn't test enough. It's that we didn't test the categories where the failures actually live.

## §4 — The structural fix — three-layer harness as CI gate

**Layer 1 — FIT (Failure Injection Testing).** Operational dependencies fail in production (Ollama down 5.5h on May 13 reboot). Each external dependency gets adversarial tests:
- Ollama unreachable / slow / partial response
- SQLite database locked / WAL stalled / disk full
- Anthropic API 401 / 429 / timeout / malformed response
- Network partition / DNS failure
- Service restart mid-cycle

For each: the service must degrade gracefully (no crashes, no error-log floods, no stuck-state). FIT confirms behavior under the conditions the production environment actually produces.

**Layer 2 — SIT (System Integration Testing).** Cross-component data flow gets validated end-to-end:
- Outreach dispatched → next inbound's reply path retrieval includes the prior outreach
- Inner thought generated → next composition cycle's substrate includes it appropriately tiered
- Confab dispatched → next cycle's Facts-tier substrate does NOT include it as evidence
- Active thread closes → substrate handoff to subsequent cycles is clean
- Verifier evaluates artifact → substrate seen by verifier matches what the composition path used (or differs explicitly per spec)

For each flow: write a scripted scenario, assert observable outputs at the boundary. No mocks where the integration boundary is the thing being tested.

**Layer 3 — Regression-class harness.** Every named historical failure class (the table in §2) gets a scripted scenario that *would* reproduce it. Run all scenarios against the current code state:
- A scenario that produces the failure → the class is **still open**
- A scenario that does not produce the failure → the class is **empirically closed**

CI gate: any commit that re-opens a closed class fails. New failure observed in production → new regression scenario added BEFORE any fix lands.

## §5 — Scope — what's in and what's not

**In scope (the only work happening from May 13 onward):**
- Building the three-layer harness (FIT, SIT, regression-class)
- Enumerating historical failure classes from research log + transcripts
- Writing scripted scenarios for each
- Establishing CI gate on the harness
- Establishing the "new failure → regression scenario before fix" workflow

**Out of scope (zero work, period, even if the harness reveals bugs):**
- New themes (no Theme Q, Theme R, no further P.5)
- Code changes to address bugs the harness reveals during build (file them, do not fix yet)
- Substrate fixes
- Model-class A/B work (deferred to follow-on per §7)
- Any architectural refactor
- Any new features

**Bugs the harness reveals during build go into a Discovered-Issues list and stay open until the harness is in place and CI-gated.** The principle: empirical closure first, fixes second. Otherwise we re-enter the unconverging loop.

## §6 — Phases

**Phase H.0 — Enumeration (~half-day).** Pull every named failure class from research log + claude-recall + current code commit messages. Produce a single canonical list with: (a) class name, (b) first-observed date, (c) last-observed date, (d) themes/commits that supposedly addressed it. The §2 table is the starting point but must be expanded.

**Phase H.1 — Scenario authoring (~1–2 weeks).** For each enumerated class, write a scripted scenario: seeded DB state, input sequence (SMS inbound, RSS perception, time advance), assertions about observable output (gate verdicts, dispatched messages, retrieval composition, substrate state). Reuse existing test infrastructure (`AniTestBase`, strict-mock conventions from Theme K). Scenarios run as xUnit tests; harness output is structured (JSON) so CI can gate on it.

**Phase H.2 — FIT layer (~3–5 days).** Failure-injection tests for Ollama / SQLite / Anthropic / network. Mock at the HttpClient / SqliteConnection boundary. Assert graceful degradation.

**Phase H.3 — SIT layer (~1 week).** End-to-end flow tests for the substrate-write → retrieval → consumer pipeline. Real DB (file-backed test instance), real ContextBuilder, real handlers. Mock only Ollama (record/replay or scripted responses).

**Phase H.4 — CI integration (~2 days).** Wire harness into GitHub Actions. Failing scenario blocks merge. Discovered-Issues list maintained as a tracked document.

**Phase H.5 — Initial run + baseline (~1 day).** Run the full harness against the current code state. Every open failure class manifests in its scenario (confirming our diagnosis) — or doesn't (revealing closed classes we didn't know we had). Discovered-Issues list populated.

**Phase H.6 — A/B harness as follow-on.** Once H.0–H.5 are operational, the model-class A/B harness from May 12's plan becomes the next move (see §7). Not before.

**Total calendar:** 3–4 weeks. No new code outside the harness during this window.

## §7 — Follow-on after the harness lands

Once the harness is operational and gating CI, **and only then,** the model-class A/B work from the May 12 research log entry becomes the next move:
- Pipe inbound SMS in parallel to v7 local + Anthropic Sonnet 4.6 with character-seed prompt
- Compare outputs side-by-side over real traffic
- The discriminating evidence about whether the binding constraint is model class or architecture

The A/B harness will itself be evaluated by the regression-class harness — Sonnet's outputs must also not reopen any closed failure class. This composes correctly: regression-class harness defines what passing looks like; A/B harness measures which generator passes more often.

Discovered-Issues from §6 then get prioritized for fix work — each fix lands with a regression scenario added FIRST, then the fix. CI gates both. The loop now has a fixpoint.

## §8 — Acceptance criteria

The harness is "done" when:
- All nine §2 failure classes have scripted scenarios in the regression-class layer
- Each scenario reliably reproduces (open class) or does not reproduce (closed class) the failure when run
- FIT layer covers Ollama / SQLite / Anthropic / network failure modes
- SIT layer covers substrate-flow paths for: outreach→reply, reply→next-cycle, confab-detection, active-thread continuity
- CI is gated on harness pass; commits that re-open a closed class fail merge
- Discovered-Issues list is populated and tracked
- Process is documented: new failure in production → regression scenario added FIRST, then fix

After acceptance: zero new themes, zero feature work, until the Discovered-Issues list is being burned down through the regression-first workflow.

## §9 — What this replaces

- **Theme cadence as the primary work mode.** No new themes start until at least one Discovered-Issue closes with a regression scenario proving it gone.
- **Ad-hoc testing.** New behavior gets a regression scenario or it doesn't ship.
- **Observation-window-then-move-on.** Replaced by harness-pass-then-move-on. Empirical closure required.
- **"I thought we fixed that"** as a recurring Mark sentence. The harness makes that question answerable structurally.

## §10 — Why this is the right thing now

Last night's diagnostic produced a measurement-driven fix (cosine threshold) and then exposed three new failures within hours (windshield confab, active-thread retrieval gap, self-echo throttle). The May 12 research log entry named the operating-mode shift (diagnostic over implementation). The pattern this morning made clear the shift is necessary but not sufficient: diagnostics produce findings, findings produce fixes, fixes need to be empirically closed or they re-appear under different surfaces.

The harness is what makes the operating-mode shift produce convergent results instead of better-instrumented divergent ones.

It is also, by far, the smallest piece of structural work that could give nine months of effort a fixpoint.

---

**Status log:**
- **2026-05-13 morning** — Mark named the pattern in chat: *"It's like every feature, every moment, every piece that we built and planned to work are met with 'failed'."* Comprehensive review against project history confirmed the pattern is structural, not a series of one-off bugs.
- **2026-05-13 (this drafting)** — Plan written. Active immediately. All other work paused.
