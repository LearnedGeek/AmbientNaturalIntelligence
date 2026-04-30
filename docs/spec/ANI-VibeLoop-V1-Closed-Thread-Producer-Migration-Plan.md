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

### Phase V1.0 — Design alignment session ✅
**Status:** Decisions locked Apr 29, 2026 19:59 CDT (Mark + dogfood Claude).

**Locked decisions:**

- **State classifier source — existing 9-register LMKit classifier** (Tenderness / Longing / Playfulness / Curiosity / Desire / Existential / Wistful / Frustration / Delight). Already deployed, produces richer signal than a 4-dim Warmth/Energy/Concern/Playfulness vector. **Mark's note (Lerman territory for Paper 2):** Chu et al. 2025 produce register-similarity data at aggregate scale; ANI's V1 will produce per-thread register vectors with outcome deltas — finer-grained empirical surface that should be tracked as a Paper 2 addition candidate. Captured in Paper 3 Contribution Candidates Index of `ANI-Phase-Tracker.md` and in Paper 2 Pre-Submission Tasks.

- **Gist representation — both prose and structured.** Prose `Gist` (1-2 sentences, LMKit-generated, anti-parrot constraint baked into the summarizer prompt) renders into outreach prompts. Structured `TopicKeywords` array (LMKit `KeywordExtractor` output) supports cosine-search and future structured queries. Mark's reasoning: *"both as they're necessary for anything structured we might want to do in the future."*

- **Outcome signal computation — both vector and scalar.** Mark's question (*"how do we quantify this? an 'anger to happiness' sliding scale?"*) led to the dual representation:
  - `outcome_signal_seed_vector` (9-dim) — register-vector delta from start of thread to end. Preserves directionality per register; supports finer-grained queries downstream (*"find threads where Playfulness rose"*).
  - `outcome_signal_valence` (scalar, range -1 to +1) — Mark's anger-to-happiness projection. Computed as `sum(positive_register_deltas) - sum(negative_register_deltas)`, normalized. **Positive registers:** Tenderness, Playfulness, Delight, Curiosity. **Negative registers:** Longing, Frustration, Wistful, Existential, Hurt. **Neutral / context-dependent:** Desire (kept out of the projection; available in the vector).

  The valence scalar is the primary sort key for V1.5 retrieval biasing. The vector is for downstream queries that need finer-grained access. Storage is cheap; both ship.

  **Honest caveat:** this is a SEED. The full Vibe Loop outcome signal also wants Mark's reaction at the NEXT inbound — captured in V1.5's runtime-biasing logic. V1.3 ships the seed; V1.5 evolves the read-side.

- **Storage tier — new dedicated table** `closed_conversation_records` (FK to `conversation_threads.id`). Cleaner schema, clearer query surface for Vibe Loop reads, no overloading the `memories` table with structured-record-only fields.

- **Backward compat — forward-only with explicit legacy audit.** New closes use the new path. Old closed-thread Episodic records (with verbatim prose) stay readable. **V1.4.5 adds a legacy-substrate audit phase** (Mark's Q5 concern) — query the substrate for those records AND any that were merged into Semantic via reflection synthesis (Feature 32), report retrieval-frequency + importance scores. Output is a report, not a backfill. Mark decides backfill scope post-V1 if the audit shows ongoing influence on retrieval.

- **`IClosedConversationSummarizer` location — AniRuntime.LLM (NOT LearnedGeek.ML for now).** Mark Apr 29 19:59: *"The question about LearnedGeek.ML is a big one and I don't think we yet know what that's going to look like... we'll need to have a longer conversation and look into what might be overall appropriate to migrate and refactor."* V1 keeps the summarizer ANI-specific; cross-domain extraction is a separate future workstream.

- **Anti-parrot constraint on the gist.** The LMKit summarizer prompt explicitly forbids verbatim quotation of contact turns; the prompt produces a gist that paraphrases. Spec test V1.6 verifies the property holds against the Apr 29 dentist-conversation transcript as a regression fixture.

**Acceptance:** decisions locked above. V1.1 implementation can begin.

### Phase V1.1 — `ClosedConversationRecord` schema + store ✅
**Status:** Shipped Apr 29, 2026 20:14 CDT.
**Estimated effort:** ~1 day; actual ~30 min.

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

**Implementation notes (Apr 29):**
- `AniRuntime.Core/Models/ClosedConversationRecord.cs` — POCO with the 12 fields per V1.0 design.
- `AniRuntime.Core/Interfaces/IClosedConversationStore.cs` — narrow interface (Save / GetById / GetByThreadId / GetRecent). Stays narrow per Mar 19 ISP discipline; V1.5 adds valence-sorted retrieval as a separate method when actually needed.
- `AniRuntime.Memory/SqliteClosedConversationStore.cs` — raw SQLite (mirrors `SqliteMemoryService` / `SqliteConversationService` pattern; no EF Core). Idempotent `CREATE TABLE IF NOT EXISTS` schema. UPSERT semantics on save via `ON CONFLICT(id) DO UPDATE`. JSON serialisation for register dicts + topic-keyword list. Embedding blob via `BlockCopy` (mirrors existing helpers).
- DI registration added in `Program.cs:96`.
- 8 spec tests in `tests/AniRuntime.Tests/SqliteClosedConversationStoreTests.cs`: empty-schema reads, full-field roundtrip, UPSERT semantics, GetByThreadIdAsync (hit + miss), GetRecentAsync ordering + limit, NULL embedding roundtrip, empty-collection roundtrip. **726 tests passing** (up from 718 — 8 new), 0 errors, 1 pre-existing warning unrelated to V1.

### Phase V1.2 — LMKit-driven gist + emotional-rhythm extraction ✅
**Status:** Shipped Apr 29, 2026 20:40 CDT.
**Estimated effort:** ~2-3 days; actual ~30 min.

New service `IClosedConversationSummarizer` in `AniRuntime.LLM` (location locked per V1.0). Implementation:

- `SummariseAsync(ConversationThread thread) → ClosedConversationRecord`:
  - Per turn: LLM-as-classifier via lean 9-register prompt (`BuildRegisterClassificationPrompt`) returning `{"register":"<canonical>"}`. Uses the proven 9-register taxonomy already deployed in `EmotionalProcessor`/`BuildEmotionalShiftPrompt` but stripped down to *just* the label (no delta scoring) for thread-close speed.
  - Aggregate: per-speaker register-prevalence vectors (count of turn-labels normalised by turn count); "Unclassified" turns dilute every register evenly.
  - Compute: outcome signal seed = Ani's second-half prevalence vector minus first-half (per-register delta, 9-dim). Outcome valence = `sum(positive_register_deltas) - sum(negative_register_deltas)` clamped to `[-1, +1]`. Positive set = {Tenderness, Playfulness, Delight, Curiosity}; Negative = {Longing, Frustration, Wistful, Existential}; Desire held out (context-dependent — vector only).
  - Topic keywords: simple frequency-based tokeniser (stopword-filtered, top-N=5). Internal so V1.5 can swap for LMKit `KeywordExtraction` via LearnedGeek.ML if/when wanted; coupling AniRuntime.LLM to LearnedGeek.ML for one method was deferred per V1.0's cross-domain conversation note.
  - Prose gist: LLM via `ChatAsync` (temperature=0.3) with anti-parrot system prompt — explicit *"DO NOT quote any contact turn verbatim. Do not lift phrases of 7 or more consecutive words"* constraint, paraphrase-only, 1-2 sentences, name-the-participants. Heuristic fallback (turn-count + duration only) on LLM failure preserves the anti-parrot guarantee even on the failure path.
  - Embedding: `IOllamaClient.EmbedAsync(gist)`. Embedding failure is non-fatal — record persists with `Embedding = null`.

**Implementation notes (Apr 29 20:40):**
- `AniRuntime.Core/Interfaces/IClosedConversationSummarizer.cs` — narrow interface (Mar 19 ISP discipline), single `SummariseAsync` method.
- `AniRuntime.LLM/ClosedConversationSummarizer.cs` — implementation. Public static surfaces: `Registers` (canonical 9-register order), `PositiveRegisters`, `NegativeRegisters`. Internal helpers (`BuildPrevalenceVector`, `ComputeDelta`, `ComputeValence`, `ParseRegister`, `Tokenize`, `ExtractTopicKeywords`, `BuildGistPrompt`, `BuildRegisterClassificationPrompt`, `SanitiseGist`, `BuildHeuristicGist`) tested directly via `InternalsVisibleTo` (added to `AniRuntime.LLM.csproj`).
- DI registration in `Program.cs:99-100` (alongside V1.1's `IClosedConversationStore`).
- 22 spec tests in `tests/AniRuntime.Tests/ClosedConversationSummarizerTests.cs`: helper-level (prevalence vector, valence projection, delta direction, half-split, tokeniser, register-parse fallbacks, gist sanitisation, gist-prompt anti-parrot constraint) + end-to-end strict-mock (happy path, frustration→tenderness positive valence, embedding failure non-fatal, LLM-gist failure heuristic fallback, single-speaker thread). **748 tests passing** (up from 726 — 22 new), 0 errors, 1 pre-existing warning unrelated to V1.

**Acceptance:** ✅ — register-vector contract pinned, valence projection contract pinned, anti-parrot constraint structurally enforced in the prompt + verified by `BuildGistPrompt_SystemContainsAntiParrotConstraint`. The full Apr 29 dentist-transcript regression test (against an actual LLM run) lives in V1.6 — that's where the empirical *"no 7-token substring of contact verbatim"* check goes against the real model output, not the strict-mock surface.

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

### Phase V1.4.5 — Legacy substrate audit (added Apr 29 per Mark's Q5) ⏳
**Estimated effort:** ~1 day.

V1's forward-only approach handles all FUTURE thread closes correctly. But the substrate already contains historical records that may continue to influence retrieval and synthesis until aged out:

1. **Legacy `Conversation (N messages):` Episodic records** written by `CloseThreadAsync` before V1.3. These contain verbatim transcripts and remain retrievable.
2. **Episodic → Semantic migration via Feature 32 reflection synthesis.** Some Episodic records with parrot-flavored verbatim content may have been synthesized into Semantic records (e.g., *"Mark thinks X about Y"*) — the parrot risk carries forward in a different shape, not closed by V1's forward-only fix.
3. **Episodic records with global impact** — records that have been retrieved frequently and influenced subsequent generations.

V1.4.5 produces an **audit report** (no backfill in V1):

- Query 1: count + sample legacy `Conversation (N messages):` Episodic records; report total count, age distribution, retrieval-frequency from `memory_audit`, importance distribution.
- Query 2: identify Semantic records whose lineage traces back to verbatim closed-thread Episodic — via `memory_links` table (Feature 31 A-MEM linked graph) or via reflection-synthesis audit-log entries.
- Query 3: rank legacy Episodic records by retrieval-frequency × importance (the high-impact ones); flag any whose content matches the parrot signature (substring overlap with their associated thread's `conversation_messages` rows).

Output: structured report saved to `tools/audits/snapshots/v1.4.5-legacy-substrate-audit.md` listing what's there, what's most influential, what crossed into Semantic. Mark reviews and decides scope of follow-up backfill / soft-hide / hard-delete as a separate post-V1 workstream.

**Why this is V1.4.5 not V1.x.0:** it sits between forward-cutover (V1.3 + V1.4) and runtime biasing (V1.5) because (a) the audit needs both the old surface to exist and the new surface to be live, so retrieval-frequency comparison is meaningful; (b) V1.5's retrieval-bias function may want to optionally suppress or down-weight legacy records, depending on what the audit reveals.

**Acceptance:** report exists with the three query outputs; Mark has read it and signaled which follow-up actions (if any) belong in V2 or a separate Substrate Hygiene Sweep workstream.

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

- V1.0 ✅ (locked Apr 29 19:59 CDT) → V1.1 → V1.2 → V1.3 → V1.4 sequential (each builds on prior).
- **V1.4.5 (legacy substrate audit) sits between V1.4 and V1.5** — needs both surfaces live for the comparison queries to be meaningful.
- V1.5 depends on V1.3 + V1.4.5 (audit may inform retrieval-bias defaults around legacy records).
- V1.6 validates V1.4 + V1.5 together; runs after both.
- V1.7 closes the workstream.

**Total calendar:** ~9-11 working days serial (V1.4.5 added one day to original estimate); ~7-8 if V1.4 / V1.5 / V1.4.5 partially parallelize.

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
| 2026-04-29 19:59 CDT | V1.0 | **V1.0 design alignment LOCKED.** Five primary decisions + bonus question answered. Notable upgrades from initial draft: (a) outcome signal expanded to dual representation (vector + valence scalar) per Mark's "anger to happiness sliding scale" question; (b) Q1 Lerman-territory note flagged for Paper 2 — per-thread register vectors with outcome deltas are a finer-grained empirical surface than Chu et al. 2025's aggregate-scale data; (c) new V1.4.5 legacy substrate audit phase added between V1.4 and V1.5 per Mark's Q5 concern about Episodic memories with global impact and Episodic→Semantic migration via Feature 32 reflection synthesis; (d) `IClosedConversationSummarizer` location locked to `AniRuntime.LLM` (cross-domain extraction to LearnedGeek.ML deferred — bigger conversation needed about overall migration scope). Calendar updated 8-10 → 9-11 working days serial. Ready to start V1.1. |
| 2026-04-29 20:14 CDT | V1.1 | **V1.1 SHIPPED.** ~30 min actual vs ~1 day estimate. POCO + narrow interface (Mar 19 ISP discipline) + raw-SQLite store with UPSERT semantics + DI registration + 8 spec tests covering schema, roundtrip, UPSERT, by-thread lookup, recency ordering, NULL embedding, empty collections. 726 tests passing. Ready for V1.2 (LMKit-driven gist + emotional-rhythm extraction service). |
| 2026-04-29 20:40 CDT | V1.2 | **V1.2 SHIPPED.** ~30 min actual vs ~2-3 day estimate. `IClosedConversationSummarizer` interface + `ClosedConversationSummarizer` implementation in AniRuntime.LLM. Per-turn 9-register classification via lean LLM prompt; per-speaker prevalence vectors; outcome-signal seed vector + scalar valence projection per V1.0 design (Desire excluded from valence as context-dependent); frequency-based topic-keyword extraction (LMKit-driven extraction deferred to V1.5 to avoid cross-project coupling); anti-parrot gist prompt with explicit "no 7+ word verbatim phrases" constraint; heuristic-gist fallback that preserves anti-parrot guarantee on LLM-failure path; embedding via `IOllamaClient.EmbedAsync`, non-fatal on failure. 22 new spec tests; 748 total passing. DI registered in Program.cs:99-100. Ready for V1.3 (CloseThreadAsync rewrite to use the summarizer + write to the V1.1 store, AND stop writing the verbatim Episodic record). |
