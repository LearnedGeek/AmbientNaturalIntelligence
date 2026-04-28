# Theme K — Test Spec-Coverage Migration (TDD + Strict Mocks)

**Drafted:** April 28, 2026
**Status:** Phase K.1 in progress (IConversationService strict-mock migration)
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

### Phase K.2 — `IMemoryService` strict-mock migration ⏳
**Status:** Queued. Largest surface; do it after K.1 patterns are validated.

`IMemoryService` is the most-mocked interface in the project (used by virtually every cognitive-cycle test). Migration is more invasive than K.1, but the Apr 28 spec-test methodology will already be muscle-memory by then.

Substeps:
1. Convert one test file at a time (start with `DesireEngineTests` or `EmotionalStateTests` — small).
2. As each file converts, audit its assertions for spec-coverage gaps.
3. When all files for a single test class are clean, mark it migrated in this doc.

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

- K.0 ✅ → K.1 (in progress) → K.2 → K.3 → K.4 → K.5
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
