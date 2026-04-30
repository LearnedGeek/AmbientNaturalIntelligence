# Vibe Loop V1 — Closed-Thread Producer Migration

**Drafted:** April 29, 2026
**Status:** Plan drafted, awaiting Mark's go-ahead to start V1.0 design session
**Origin:** Apr 29 19:00 — Mark observed verbatim-parrot recurrence in Ani's outreach (closed-thread Episodic summary contained Mark's verbatim text, retrieved into outreach composition prompt). Mark Apr 29 19:22 critique: *"why do we have something that is only part of a single path? I thought the goal of the larger refactor and all the mermaid charts we created was to remove single pipeline failures and consolidate?"*
**Theme owner:** Mark (named the move; Claude executes phasing).
**Companion docs:** Theme J refactor plan (`ANI-Theme-J-Guard-Consistency-Refactor-Plan.md`), Vibe Loop design (`ANI-Phase-Tracker.md` §Vibe Loop), data-flow diagrams (`docs/research/ANI-Data-Flow-Diagrams.md`).

---

## What This Workstream Is

A focused workstream that does **three things in one architectural move**, treating them as the same problem rather than three separate ones:

1. **Closes the Apr 29 verbatim-parrot leak** — `CloseThreadAsync` currently writes a closed-thread Episodic record containing the conversation as verbatim prose. That record is retrievable at outreach composition time and bypasses J.2's per-speaker structural protection (J.2 only protects ACTIVE-thread structured summaries). The leak surface is the producer-side write of verbatim transcript.

2. **Ships Vibe Loop V1** by repurposing the closed-thread write event as the `InteractionOutcome` ingestion point. Every closed thread becomes a structured record: `(user_state_pre, response_gist, user_state_post, outcome_signal_seed)` — the load-bearing data Vibe Loop needs to bias retrieval over time. Vibe Loop's open design question (*"where does state-pre/state-post classification come from?"*) gets a concrete answer: from LMKit emotion classification at thread close.

3. **Establishes the first producer migration through what will become Theme J's `CognitiveOutputGate`.** `CloseThreadAsync` becomes the first producer rewritten to emit through a structured-output surface rather than a free-form prose write. The pattern set here becomes the template for Theme J's J.5 phase when it formalizes the gate abstraction.

The architectural insight the workstream rests on: **the closed-thread write is one event with three downstream consumers (parrot-leak surface, Vibe Loop substrate, Theme J producer). Solving it as one move costs barely more than solving any single one in isolation, and produces an architecturally correct artifact that all three can use.**

## What V1 Is NOT

- **Not Theme J.4 / J.5 done.** This ships ONE producer migration. Theme J still has many other producers (outreach phase, inner thought save, reflection synthesis) that need migration. V1 sets the pattern; the rest of J.5 follows.
- **Not the full Vibe Loop runtime.** V1 ships the *write* surface and the data structure. The *retrieval-time biasing* (read recent `InteractionOutcome`, bias composition toward positive-outcome strategies) is V1.5 of this plan and is observational at first — needs weeks of substrate accumulation before behavioral biasing produces measurable effect.
- **Not a full schema rebuild.** Existing memory tables stay; the new `ClosedConversationRecord` is a new structured surface that lives alongside the existing Episodic store. Backward compat is preserved.
- **Not a tactical patch.** Mark's no-surgical-fixes rule applies: a plain "rewrite `CloseThreadAsync` to gist instead of verbatim prose" without the Vibe Loop / producer-migration framing would be a surgical fix that gets absorbed by J.5 later. V1 IS J.5 starting at one producer.

## Phase Structure

### Phase V1.0 — Design alignment session ⏳
**Estimated effort:** ~half-day.
**Status:** Awaiting Mark's go-ahead.

Decisions to lock before any code:

- **State classifier source.** LMKit emotion classifier per turn, aggregated to per-speaker register vectors at thread close. Open: do we use existing 9-register classifier (Tenderness/Longing/Playfulness/etc.) or a smaller state vector (Warmth/Energy/Concern/Playfulness)? Both are plausible; recommend the existing 9-register since it's already deployed and produces a richer signal.
- **Gist representation.** Three options: prose summary, structured fields, both. Recommend **both**: a prose `Gist` (1-2 sentences, LMKit-generated, constrained to NOT lift verbatim) for retrieval rendering AND a structured `Topic` keyword set (LMKit `KeywordExtractor` output) for cosine-search.
- **Outcome signal computation.** Delta on the per-speaker register vectors from start to end of thread. `outcome_signal_seed = AniRegister(end) - AniRegister(start)` produces a vector showing how Ani's emotional state moved across the thread. This is a SEED for the eventual full Vibe Loop outcome signal (which also needs Mark's reaction at the NEXT inbound — captured in V1.5).
- **Storage tier.** Recommend new dedicated table `closed_conversation_records` (FK to `conversation_threads.id`) rather than overloading the `memories` table. Cleaner schema; clearer query surface for Vibe Loop reads.
- **Backward compat.** Existing closed-thread Episodic records (with verbatim prose) stay readable. The retrieval-side migration (V1.4) consumes the new structured records when present and falls back to the prose form when not — same pattern as J.2's structured-summary additive deploy. Old records age out via natural recency decay; no purge required.
- **Anti-parrot constraint on the gist.** The LMKit summarizer prompt explicitly forbids verbatim quotation of contact turns; the prompt produces a gist that paraphrases. Spec test V1.6 verifies this property holds.

**Acceptance:** decision record captured at the top of this doc; Mark approves before V1.1 builds.

### Phase V1.1 — `ClosedConversationRecord` schema + migration ⏳
**Estimated effort:** ~1 day.

New SQLite table:

```sql
CREATE TABLE closed_conversation_records (
    id                    TEXT PRIMARY KEY,
    thread_id             TEXT NOT NULL,
    closed_at             TEXT NOT NULL,
    gist                  TEXT NOT NULL,            -- LMKit-generated, paraphrased
    topic_keywords        TEXT NOT NULL,            -- JSON array, KeywordExtractor output
    mark_register         TEXT NOT NULL,            -- JSON, 9-register vector
    ani_register          TEXT NOT NULL,            -- JSON, 9-register vector
    turn_count            INTEGER NOT NULL,
    duration_seconds      REAL NOT NULL,
    outcome_signal_seed   TEXT NOT NULL,            -- JSON, register delta vector
    embedding             BLOB,                     -- gist embedding for retrieval
    FOREIGN KEY (thread_id) REFERENCES conversation_threads(id)
);

CREATE INDEX ix_closed_conv_closed_at ON closed_conversation_records(closed_at DESC);
CREATE INDEX ix_closed_conv_thread_id ON closed_conversation_records(thread_id);
```

EF Core migration generated and committed. No data migration of existing records — V1 is forward-only.

**Acceptance:** schema landed, migration applied locally, write/read roundtrip spec test green.

### Phase V1.2 — LMKit-driven gist + emotional-rhythm extraction ⏳
**Estimated effort:** ~2-3 days.

New service `IClosedConversationSummarizer` in `AniRuntime.LLM` (or `LearnedGeek.ML` if reusable across DrOk). Implementation uses LMKit:

- `Summarize(ConversationThread thread) → ClosedConversationRecord`:
  - Per turn: LMKit emotion classification → register vector
  - Per turn: LMKit keyword extraction → topical keywords
  - Aggregate: per-speaker register vectors (mean of turn vectors), turn count, duration
  - Compute: outcome signal seed (Ani-register delta from first turn to last)
  - Generate prose gist via LMKit prompt: *"Summarize this conversation in 1-2 sentences, paraphrasing rather than quoting. Focus on what changed emotionally, not what was said."* (Anti-parrot constraint baked into the prompt.)
- Embed the gist via existing `IOllamaClient.EmbedAsync`.

**Acceptance:** spec test on the Apr 29 dentist conversation transcript — verify the generated gist contains NO substring of contact's verbatim turns ≥7 tokens. Spec test on register output: verify both speakers produce a populated 9-register vector.

### Phase V1.3 — `CloseThreadAsync` rewrite ⏳
**Estimated effort:** ~1 day.

`SqliteConversationService.CloseThreadAsync` rewritten to:

1. Call `IClosedConversationSummarizer.Summarize(thread)` (V1.2).
2. Persist the resulting `ClosedConversationRecord` to the new table.
3. **Stop writing the verbatim-prose `Conversation (N messages):` Episodic record.** This is the leak surface we're closing. Existing records remain readable; new closes don't produce them.

**Critical:** the per-message conversation_messages rows STAY. Verbatim-fidelity-when-needed lives in `conversation_messages`. The `ClosedConversationRecord` is the gist surface for retrieval. Two surfaces, two purposes — exactly the substrate-typing pattern the claude-recall reframe established this morning.

**Acceptance:** spec test — close a thread programmatically; verify (a) `ClosedConversationRecord` row exists with valid gist + register vectors, (b) NO new Episodic record with `Conversation (N messages):` prefix appears, (c) all `conversation_messages` rows remain intact.

### Phase V1.4 — Outreach prompt path migration ⏳
**Estimated effort:** ~1 day.

`PromptBuilder.cs:875-879` (the `RecentConversationSummary` prose fallback in `BuildOutreachMessagePrompt`) — replace with consumption of the new `ClosedConversationRecord`:

- Retrieve the most recent `ClosedConversationRecord` for this contact (within recency window).
- Render in the outreach prompt as: *"Recent conversation gist: {gist}. Mark's emotional register at the time: {mark_register top-2}. Your register: {ani_register top-2}."*
- The structural anti-parrot guarantee: there's NO verbatim transcript in the prompt anymore — only the gist (which is paraphrased by V1.2 prompt design) and structured register vectors.

Same change applies to `BuildOutreachPrompt` (the decision-stage prompt at `PromptBuilder.cs:428-435`) — both decision-stage and composition-stage now read the gist surface, not the prose surface.

**Acceptance:** spec test — set up a closed thread containing Mark's verbatim text "I'm trying to pretend to work while being distracted by you. Haha"; trigger an outreach composition; verify the rendered outreach prompt contains NO occurrence of that verbatim string (or any substring ≥7 tokens of it).

### Phase V1.5 — Vibe Loop retrieval-time biasing ⏳
**Estimated effort:** ~2-3 days.

The actual Vibe Loop runtime mechanism. At outreach composition time:

1. Take the current emotional state vector (`snapshot.EmotionalState`).
2. Cosine-search recent `ClosedConversationRecord`s by ani_register similarity to the current state.
3. From matching records, look at which ones produced positive `outcome_signal_seed` (Ani's register moved toward Tenderness/Playfulness/Resilience) vs negative (moved toward Hurt/Withdrawal/Frustration).
4. Bias composition prompt toward strategies from the positive-outcome set: include their gists in the prompt context block as *"strategies that landed well in similar moments."*

**Honest expectation:** V1.5 is observational at first. Behavioral biasing produces measurable effect only after weeks of substrate accumulation — until enough `ClosedConversationRecord`s exist with varied outcome signals, the bias has nothing to bias against. The Apr 29 *"pee king"* case (Mark didn't love the nickname; Ani kept using it because no outcome signal existed to decay her usage) is the canonical motivating case. V1.5 lets that signal start accumulating.

**Acceptance:** spec test — given a fixture set of `ClosedConversationRecord`s with varied outcome signals, verify the retrieval-bias function returns the positive-outcome subset weighted higher.

### Phase V1.6 — Anti-parrot validation against today's empirical case ⏳
**Estimated effort:** ~1 day.

Regression spec test using the Apr 29 transcript:
- Setup: closed thread `91b7d20b` containing Mark's *"I'm trying to pretend to work while being distracted by you. Haha"*
- Run V1.3 → produces `ClosedConversationRecord`
- Run V1.4 outreach composition path
- Verify: no verbatim substring of Mark's text appears in the outreach prompt
- Verify: no verbatim substring of Mark's text appears in the dispatched outreach output

This becomes the canonical regression test for the leak class. Recurrence ever surfaces in production → run this test against the failing scenario; if it still passes, the leak is from a NEW surface (different producer); if it fails, V1's coverage is the regression.

**Acceptance:** test green; documented in tracker as the regression criterion for the parrot class.

### Phase V1.7 — Documentation + Paper 3 contribution draft ⏳
**Estimated effort:** ~1 day.

- Update tracker: Vibe Loop matrix row P2 → "V1 shipped" with link to this plan; §Vibe Loop section updated to reflect concrete implementation; §Theme J updated to note V1 establishes the first producer migration through the (eventual) shared output gate.
- Update Apr 29 verbatim-parrot gap-watch row: status from "fix not yet drafted" to "fixed via Vibe Loop V1".
- Paper 3 process-note draft in `docs/research/papers/paper3/contribution-vibe-loop-v1.md`: *"single architectural move addressing three apparently-distinct concerns (parrot leak, runtime adaptation substrate, refactor consolidation foothold) — empirical case for treating co-located failure surfaces as one workstream rather than three."*
- Cross-project note for DrOk: structured-output-at-producer pattern is reusable in clinical-safety contexts (medical-triage closed-encounter records have the same shape). Worth a one-paragraph mention in `docs/shared/cross-project-status.md`.

**Acceptance:** tracker reflects V1 shipped; Paper 3 contribution prose draft exists.

## Sequencing & Dependencies

- V1.0 → V1.1 → V1.2 → V1.3 → V1.4 sequential (each builds on prior).
- V1.5 depends on V1.3 (data must exist) but can run in parallel with V1.4 if hands available.
- V1.6 validates V1.4 + V1.5 together; runs after both.
- V1.7 closes the workstream.

**Total calendar:** ~8-10 working days serial; ~6-7 if V1.4 / V1.5 run in parallel.

**No cross-theme blockers.** Theme J's J.4 / J.5 phases are still queued; V1 doesn't wait on them, V1 IS the first instance of J.5's pattern. Theme G / Theme L / Theme H1 are independent.

## Acceptance Criteria for V1 Overall

- Apr 29 verbatim-parrot regression test green (V1.6).
- All new closes write `ClosedConversationRecord` rows with populated gist + register vectors.
- New closes do NOT write the prose `Conversation (N messages):` Episodic record.
- Outreach prompts (decision + composition) consume the gist surface; verbatim transcript is structurally absent from prompt context.
- Vibe Loop retrieval-bias function operates on the new substrate (observational at first).
- Tracker updated; Paper 3 contribution prose draft exists.
- Build clean, full test suite passing, no regressions.

## What V1 Doesn't Address

- **Other producers' migrations.** Inner-thought save, outreach Episodic save, reflection synthesis — all still write through pre-J.5 surfaces. V1 sets the pattern; subsequent J.5 sub-phases migrate them.
- **Vibe Loop's full runtime adaptation.** V1.5 is observational; the visible behavioral biasing emerges over weeks as substrate accumulates.
- **Old closed-thread Episodic records with verbatim prose.** They remain in the substrate, retrievable. They age out via natural recency decay. If observed parrot recurrence comes from these legacy records, a one-time purge sweep is a separate operation — not in V1 scope.
- **Cross-thread emotional rhythm tracking.** V1 captures per-thread rhythm. Long-arc trajectories across many threads (which is what EM9 longitudinal compounding ultimately wants) is a future workstream that would consume V1's substrate.

## Status Log

| Date | Phase | Note |
|------|-------|------|
| 2026-04-29 | V1.0 | Plan drafted by Mark + dogfood Claude after Apr 29 19:00 verbatim-parrot recurrence diagnosis. Mark Apr 29 19:22 critique surfaced the architectural framing: *"single-path failures shouldn't exist; the refactor is supposed to consolidate."* Plan drafted as the architecturally honest response — three concerns combined into one workstream rather than three patches. Awaiting Mark's go-ahead to start V1.0 design session. |
