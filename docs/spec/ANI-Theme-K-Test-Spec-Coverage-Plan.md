# Theme K — Test Spec-Coverage Migration (TDD + Strict Mocks)

**Tracked in:** [#28](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues/28)
**Drafted:** April 28, 2026
**Status:** Phase K.2 shipped (IMemoryService strict-mock migration). K.3 queued.
**Origin:** Apr 28 silence-policy regression diagnosis. NO test in the suite covered the LastContactInbound invariant; the bug was latent for weeks. Mark's framing: *"I think we're acting like junior developers here and writing code, then writing tests to match. We should be taking a TDD approach... we should also be using mockbehavior strict on all tests."*
**Companion doc:** `~/.claude/TESTING-STRATEGY.md` §20 (Tests Pin the Spec, Not the Code)

---

## Why This Is a Theme, Not a One-Shot

The Apr 28 regression — admin tags silently disabling the silence policy by updating `LastContactInbound` — slipped past the test suite because:

1. **No test existed** for *"admin commands MUST NOT update LastContactInbound."* The spec was load-bearing (Paper 1) but not pinned by any assertion.
2. **Mocks were loose**, so the absence of a setup didn't fail the test — it just returned default values that happened to satisfy whatever the test asserted on.

These two failures compound. A strict mock without TDD-style spec tests still misses invariants (you can't assert what you didn't think to test). TDD-style spec tests with loose mocks still let regressions through (the unset call returns a default that satisfies the assertion). Both must move together.

The migration touches every test file in the suite. Doing it per-mock-surface keeps each step a clean, reviewable change rather than a months-long branch.

---

## Phase Structure

### Phase K.0 — Policy Documented ✅
**Status:** Shipped Apr 28, 2026.

`~/.claude/TESTING-STRATEGY.md` §20 added with the policy, the canonical case (Apr 28 silence-policy regression), the setup-order trap, the migration ladder, and naming conventions for spec-tests-vs-code-tests.

The Apr 28 architectural fix (admin commands routed at perception source) was the first work to follow the new policy. Two new test files — `TwilioInboundPerceptionSourceAdminTests` (5 tests) and `SqliteConversationServiceTests` (6 tests) — both use `MockBehavior.Strict` from the start.

### Phase K.1 — `IConversationService` strict-mock migration ✅
**Status:** Shipped Apr 28, 2026.

`IConversationService` is the smallest, most-mocked surface in the project that isn't already strict somewhere. It's the right pilot for the migration because:
- Only 7 methods. Bounded blast radius.
- The Apr 28 regression touched it directly. Migration completes the story Mark asked for.
- The two new test files added Apr 28 already use it strictly — they prove the surface is migratable.

**Concrete steps:**

1. **Inventory every `Mock<IConversationService>(...)` instantiation.**
   - Expected sites (from prior context): `CognitiveCycleProcessorTests`, `OutreachPhaseTests` (if exists), and the conversation tests added today.
2. **Convert each to `MockBehavior.Strict`.**
3. **Run the test suite. Failures will be of two shapes:**
   - **Missing setup** (most common) — strict found a real call the test never declared. Add the setup with the value the test actually relies on. *This is the win:* every such addition documents an interaction that was previously untested.
   - **Wrong setup ordering** (the Apr 28 trap) — a later setup overwrites an earlier one. Reorder so test-local setups run after factory setups.
4. **Where missing setups reveal an unspecified spec, add a TDD-style spec test for it.** Don't just paper over the missing setup; ask whether the call should happen at all and pin the answer.

**Acceptance:** all `Mock<IConversationService>` instantiations carry `MockBehavior.Strict`. Test suite passes. Any newly-discovered spec gaps have spec tests added.

**K.1 result (Apr 28):** Inventory found 4 sites — `TwilioInboundPerceptionSourceAdminTests` (already strict from K.0), `SqliteConversationServiceTests` (uses real SQLite, no mock), `ContextBuilderStructuredSummaryTests`, `VoiceTurnPipelineTests`, and `CognitiveCycleProcessorTests`. All three loose mocks converted; all 675 tests pass on the first run. No spec gaps surfaced (every call site already had its setup declared) — outcome consistent with the K.1 hypothesis that the smallest mock surface would be the easiest first migration. Confidence-builder for K.2 (`IMemoryService`).

### Phase K.2 — `IMemoryService` strict-mock migration ✅
**Status:** Shipped Apr 28, 2026.

`IMemoryService` is the most-mocked interface in the project (used by virtually every cognitive-cycle test).

**Migration strategy chosen — base-class strict + per-class factories already declare every call.** The alternative (leave the base loose, convert each consumer to a local strict mock) was considered but rejected: the loose `MockMemory` in `AniTestBase` is shared across ~every cognitive-cycle test, and converting per-class would have meant introducing a parallel local `Mock<IMemoryService>` next to the base mock in three files — duplicate plumbing for no contract win. Making the base strict in one edit, with the existing per-class `CreateProcessor()`/`Build()`/`CreateEngine()` factories already declaring the calls each test path needs, was the smaller and more uniform change. The K.1 result (no spec gaps surfaced in `IConversationService` because every call site was already declared) gave high confidence the same would be true here, and it was — every memory call site the cycle reaches was already explicit in some test factory. The win was not a cascade of broken tests, but the strictness itself: every interaction is now a documented contract, and any *future* call added to a cycle phase will fail every test that touches it until the test author explicitly declares whether the new call is a spec interaction or a regression.

**K.2 result (Apr 28):**

*Sites inventoried (`Mock<IMemoryService>` and narrower-interface variants):*
- `tests/AniRuntime.Tests/Infrastructure/AniTestBase.cs` — `MockMemory` (shared across `CognitiveCycleProcessorTests`, `ContextBuilderStructuredSummaryTests`, `DesireEngineTests`, `SqliteMemoryServiceTests` [inherits but does not use], `CognitiveCyclePersistenceContractTests` [new])
- `tests/AniRuntime.Tests/VoiceTurnPipelineTests.cs` — local `_mockMemory`
- `tests/AniRuntime.Tests/TimePerceptionSourceTests.cs` — `Mock<IStateStore>` (narrowed slice of `IMemoryService`)
- `tests/AniRuntime.Tests/SqliteConversationServiceTests.cs`, `tests/AniRuntime.Tests/TwilioInboundPerceptionSourceAdminTests.cs` — already strict from K.0.

*Conversions:* 3 mocks flipped to `MockBehavior.Strict` (the base + the two file-local mocks). All 675 baseline tests pass on first run with no setup additions required — every memory call the cycle reaches was already explicit in some test's factory helper, so strict surfaced no missing setups.

*Spec gaps surfaced + tests added:* The migration *did* surface a previously-uncovered architectural invariant declared in `DesireEngine.cs` source comments — *"All DesireState writes go through this class. CognitiveCycleProcessor must never call IMemoryService.SaveDesireStateAsync() directly."* No test pinned that contract. New file `tests/AniRuntime.Tests/CognitiveCyclePersistenceContractTests.cs` adds 3 TDD-style spec tests using a *separate* strict `IMemoryPersistence` mock for the processor's `persist` slot (distinct from the persistence handle injected into `DesireEngine`):

1. `RunAsync_DesireStateWrites_RoutedExclusivelyThroughDesireEngine` — pins the load-bearing invariant. The cognitive cycle's `_persist` mock has NO `SaveDesireStateAsync` setup; if the processor ever reaches around `DesireEngine`, strict mode raises.
2. `RunAsync_PersistsEmotionalState_ExactlyOncePerCycle` — pins Phase 0's emotional-state write count.
3. `RunAsync_InnerThought_PersistedThroughProcessorPersistence` — pins inner-thought persistence on the processor's persistence channel directly (the existing `RunAsync_AlwaysSavesInnerThought` test asserts the call lands on the composite `MockMemory`; this one asserts it lands on the *processor's* `IMemoryPersistence` slot specifically, which is a stronger architectural claim).

*Total tests after K.2:* 678 passing, 0 skipped, 0 failures, 0 warnings (test project).

### Phase K.3 — `IOllamaClient` strict-mock migration ⏳
**Status:** Queued.

The LLM mock surface. Likely smaller than `IMemoryService` but with subtle setups (chat vs. inner monologue vs. JSON modes). Migration order: K.2 → K.3 because K.2 shakes out the broader migration patterns first.

### Phase K.4 — Remaining mock surfaces ⏳
**Status:** Queued.

Sweep up: `IConversationGateState`, `IDiagnosticService`, `IIntentRouter`, `IChannelResolver`, `IAniAction`, `ISessionNotifier`, `IPerceptionSource`, `IHttpClientFactory`, `IClaimVerification`, etc. Smaller surfaces, mechanical conversions.

### Phase K.5 — Invariant audit (the part Mark asked for) ⏳
**Status:** Queued. The end state.

After every mock is strict, do the audit Mark named: *"verify all tests are testing all cases appropriately."*

**Audit method:**
1. Walk each architectural invariant from Paper 1 (silence policy, withdrawal, hard gates, three-way scoring, etc.). For each, confirm there is a test asserting it.
2. Walk each gate / phase / detector and confirm its contract is tested at the spec level (what it MUST do, what it MUST NOT do — both).
3. Walk each `// Apr X, YYYY:` comment in the codebase that describes a fix. If the comment names a regression, there must be a test pinning the fix.

Output: a list of invariants currently un-tested. Each becomes a spec test in subsequent commits.

**Acceptance:** the spec audit completes with zero un-pinned invariants in the load-bearing categories above. New finding becomes a candidate Paper 3 process-note (*"running the substrate through its own audit catches regressions before deploy"*).

---

## Sequencing & Dependencies

- K.0 ✅ → K.1 ✅ → K.2 ✅ → K.3 (next) → K.4 → K.5
- **No cross-theme dependencies.** Test migration is isolated infrastructure work; doesn't block or get blocked by Theme J / G / etc.
- **Cadence is Mark's call.** Each phase is independently reviewable and shippable.

---

## What This Theme Does Not Do

- **Does not change production code.** Spec tests added during migration may reveal bugs (and those should be fixed in their own commits), but the migration itself is test-only.
- **Does not introduce new test frameworks.** Stays on xUnit + Moq + FluentAssertions per `TESTING-STRATEGY.md`.
- **Does not retrofit `[Theory]` everywhere.** Spec tests use whichever shape is clearest — `[Fact]` with one assertion, `[Theory]` with table-driven cases, doesn't matter. The point is the assertion, not the syntax.

---

## Paper 3 Contribution Candidate

The migration log itself is a candidate process-note for Paper 3:

> *"Test methodology drift in long-lived AI-pipeline projects: how loose-mock + code-first testing produces a suite that passes while the system regresses. Apr 28 silence-policy regression as the canonical instance — the bug existed for weeks because no test pinned the invariant; the bug surfaced only because Mark tagged a single outreach as garbage and the trace happened to be archaeologically reachable."*

Worth holding the methodology observation through K.5 and writing it up with the audit results.

---

## Status Log

| Date | Phase | Note |
|------|-------|------|
| 2026-04-28 | K.0 | Policy documented in `~/.claude/TESTING-STRATEGY.md` §20. First two test files written under the new policy: `TwilioInboundPerceptionSourceAdminTests` (5 strict tests), `SqliteConversationServiceTests` (6 strict tests). Both shipped with the architectural fix in commit `2437b8c`. |
| 2026-04-28 | K.1 | Phase started. Inventory + conversion + spec-gap pass on `IConversationService`. |
| 2026-04-28 | K.1 | Phase shipped. 4 sites inventoried, 3 conversions (1 already strict). All 675 tests pass. No spec gaps surfaced. |
| 2026-04-28 | K.2 | Phase shipped. `IMemoryService` mocks flipped to `MockBehavior.Strict` at the base-class level (`AniTestBase.MockMemory`) plus two local mocks (`VoiceTurnPipelineTests._mockMemory`, `TimePerceptionSourceTests.DefaultStateStore`). All 675 baseline tests pass with strict mode — every memory call the cycle reaches was already explicit in test factories. Migration surfaced one previously-unpinned architectural invariant from `DesireEngine.cs` source ("CognitiveCycleProcessor must never call SaveDesireStateAsync directly"); 3 new TDD-style spec tests added in `CognitiveCyclePersistenceContractTests.cs` using a separate strict `IMemoryPersistence` mock for the processor's `persist` slot. Total: 678 tests, 0 failures, 0 warnings. |
