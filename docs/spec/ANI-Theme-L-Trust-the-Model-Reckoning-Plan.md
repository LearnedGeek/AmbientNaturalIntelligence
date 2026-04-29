# Theme L — Trust-the-Model Reckoning

**Drafted:** April 28, 2026
**Status:** L.0 (inventory) — priority dropped Apr 28 evening after empirical reversal of the founding diagnosis.
**Origin:** Apr 28 morning conversation-quality regression. Mark's framing: *"previously I was able to have 5-10 messages before she went off the rails, now it's 0-1 before she's non-sensical."*
**Theme owner:** Mark (he named it; I'll do the legwork).

> **Apr 28 evening update (~20:00 CDT):** the founding diagnosis was wrong, or at least mis-prioritized. After (a) tactical substrate purge (178 memory records + 22 `///tag` conv messages removed, seeds preserved per the cross-phrase preservation logic) and (b) removing the obsolete paragraph-break truncation gate in `MessageCleaner` (commit `82c193a` — was cutting multi-paragraph replies down to 11 chars), conversation quality recovered to ~90 minutes of sustained coherent exchange WITHOUT re-introducing a single stripped scaffold. The 5-10 → 0-1 regression turned out to be **substrate pollution + obsolete output-side gate**, not the Apr 1 strip-echo bet breaking. L.0 inventory still worth completing for the record. L.1 paired ablation **priority dropped** — the empirical answer is "those Apr 1 strips were probably right; the substrate was the issue." The methodological contribution stays: re-evaluation gates ARE useful even when the conclusion turns out to validate the original decision, because the alternative is suspecting the wrong cause indefinitely.

---

## What This Theme Is

Between March 22 and April 1, 2026, three deliberate refactor sittings stripped behavioral / anti-repetition / pattern-awareness scaffolding from runtime prompts under the principle *"trust the model, strip the constraints."* The reasoning at the time was that listing avoid-topics in the prompt **primes** them rather than avoiding them — naming "don't think about the bookstore" makes the model think about the bookstore.

Apr 28 empirical evidence: that bet has broken under accumulated substrate pressure. Stuck-thought repetition is severe (`duck norris` 77 hits in 14 days, `vanilla cream soda` 30, `romance novel` 37). Conversation quality has degraded from 5-10 messages of coherence to 0-1 messages.

This theme is a **formal re-evaluation** of those decisions. It is **not** a tactical reversal — Theme L exists precisely *because* tactical reinstatement-without-evidence would just re-litigate the original decision. The whole point is to ground each decision in measured impact against the synthetic test harness (Phase 3.1, P1).

## What Theme L Is NOT

- **Not a license to re-introduce all the stripped scaffolds.** Some of those decisions may still be right; we're testing.
- **Not "Claude tactical-patching what Mark explicitly removed."** Mark named this theme; the work is paired-ablation evidence-gathering and his decision per scaffold.
- **Not a substitute for Theme G Layer 3 G3.4 (retrieval pool composition) or Theme J (substrate cleanup).** Those address different substrate surfaces. Theme L addresses prompt-side scaffolding specifically. Theme G/J fix where pollution comes from; Theme L decides what nudge text the prompt should carry given the cleaner substrate Theme G/J will provide.
- **Not the place for new prompt-level inventions.** Strictly: re-evaluate decisions that already exist in git history. Adding new prompt scaffolding is a different conversation.

## Phase Structure

### Phase L.0 — Inventory ⏳
**Status:** Starting Apr 28, 2026.

Enumerate every "trust the model" decision in commit history. For each:

- **Commit hash + date.** Anchor the decision in the record.
- **What was stripped.** Exact prompt fragment removed; line-counts; the structural location (BuildInnerThoughtPrompt / BuildOutreachPrompt / BuildReplyPrompt / etc.).
- **Original reasoning.** Verbatim from the commit message or the diff context.
- **Substrate state at the time.** What was the DB volume / type composition / known pollution profile when the decision was made? Memory growth curve, register saturation, known confabulation classes active.
- **Substrate state today.** Same characterization for Apr 28, 2026.
- **Failure modes the strip was *meant* to prevent.** Was the prompt actually causing the harm the commit message claimed?
- **Failure modes that have *emerged since* the strip.** What's broken that wasn't visible on the date of the strip?

Known initial inventory targets (from quick git log):
- `83a3809` (Apr 1) — Phase A inner thought reform: 5 strips (Pick-a-DIFFERENT-topic instruction, ProcessedThemes avoidance list, PatternAwareness Feature 12 nudge, thought-loop detection block, Feature 41 diversity nudge)
- Mar 29 — Conversation Mode strip (referenced in `83a3809`'s commit message; need to identify the specific commit)
- Mar 23 — Pipeline simplification (`docs/spec/prompt-simplification-plan.md`); ~1100 tokens of behavioral coaching stripped from runtime prompts

Output of L.0: a structured inventory table (one row per stripped scaffold) committed to this doc as §Inventory. No code changes in L.0.

### Phase L.1 — Paired Ablation ⏳

For each L.0 inventory row, run a paired ablation against the Phase 3.1 synthetic test harness (queued P1 already). The harness has accelerated-cycle observation without Twilio cost — natural fit for this kind of ablation work.

**Per scaffold:**
- Branch A: with the scaffold reinstated *exactly as it was in the pre-strip commit*.
- Branch B: with the scaffold reinstated in a *reformulated* version (positive nudge instead of negative avoid-list, or whatever reframe seems most coherent with the original concern).
- Branch C: control — current state (scaffold remains stripped).
- Run N synthetic conversations with controlled inputs across all three.
- Measure: thought-repetition rate, vanilla-cream-soda recurrence, `///tag` rate from the simulated user, off-rails-after-N-messages metric.

L.1 produces empirical data per scaffold. **No production deploys in L.1.** Pure measurement.

### Phase L.2 — Per-Scaffold Decision ⏳

Mark reviews L.1 data and decides, per scaffold:
- **Reinstate-as-was** — the original strip was wrong for current conditions; restore.
- **Reformulated reinstate** — Branch B's reframing wins; ship that.
- **Kept stripped** — control beats both reinstatements; the original strip remains correct.

Each decision committed with a spec test pinning the reasoning so the next refactor can't re-litigate without seeing the full record. The spec test names the failure-mode the scaffold (or its absence) targets and the empirical evidence that resolved it.

### Phase L.3 — Process Capture ⏳

Update `docs/spec/prompt-simplification-plan.md` with a "Re-evaluation gate" section: any future strip-the-constraints commit must be paired with a Theme L-style measurement plan against the synthetic test harness, not just qualitative reasoning. The methodology is itself a Paper 3 candidate process-note: *"trust-the-model decisions in companion-AI projects need empirical re-evaluation gates because substrate state changes faster than prompt assumptions."*

## Sequencing & Dependencies

- **L.0 (inventory)** can start now; doesn't block on anything.
- **L.1 (paired ablation)** depends on Phase 3.1 synthetic test harness. Phase 3.1 is queued P1 already — Theme L is one of its first consumers. If Phase 3.1 ships first, L.1 can run on it; otherwise L.1 may motivate accelerating Phase 3.1.
- **L.2 / L.3** depend on L.1 output.

No conflict with Theme G (retrieval pool composition) or Theme J (substrate cleanup) — those run in parallel and produce a cleaner substrate that L.1 measurements should account for.

## Acceptance Criteria

- **L.0:** complete inventory in §Inventory below; every "trust the model" commit between Mar 22 and Apr 1 documented row by row.
- **L.1:** paired-ablation results table with at least 3 measurement runs per scaffold; statistical significance noted where applicable.
- **L.2:** per-scaffold decision recorded with reasoning + spec test ID.
- **L.3:** prompt-simplification-plan.md updated with the re-evaluation gate; Paper 3 process-note candidate added to the research log.

## Why This Is a Theme, Not a Phase Under Existing Themes

- **Not Theme E (Pipeline Hygiene):** E is small one-off defensive work. L is a multi-week measurement-driven re-evaluation of intentional architectural decisions.
- **Not Theme G (Agentic Lens):** G addresses retrieval pool composition. L addresses prompt rendering after retrieval. Adjacent, not overlapping.
- **Not Theme J (Guard Consistency):** J restructures cognitive-output gates and source attribution at the substrate-rendering layer. L re-evaluates which behavioral nudges should be in the inner-thought prompt regardless of substrate quality. Different surface.
- **Not Theme K (Test Spec-Coverage):** K is methodology for tests. L is methodology for prompt-design decisions.

Theme L is its own theme because *"prompt-side scaffolding decisions"* is a coherent surface that needs its own measurement methodology and decision record. Wrapping it under another theme would dilute both.

## Status Log

| Date | Phase | Note |
|------|-------|------|
| 2026-04-28 | L.0 | Theme drafted by Mark + dogfood Claude after Apr 28 conversation-quality regression diagnosis. Snapshot audit confirmed stuck-thought repetition severity. Phased plan landed. L.0 inventory work starting. |
| 2026-04-28 evening | L.0 | **Priority dropped.** Founding diagnosis empirically reversed: substrate purge + MessageCleaner paragraph-truncation removal restored conversation quality (~90 min sustained at 20:00 CDT) without re-introducing any stripped scaffold. Working hypothesis flipped: Apr 1 strips were probably right; substrate was the issue. L.0 inventory still completes for the record; L.1 paired ablation deprioritized but not cancelled — methodological contribution stands. Three-paths-to-same-conclusion methodology pattern (Mar 22 Mistral A/B → Apr 1 inner thought reform → Apr 28 substrate cleanup) is itself a Paper 3 process-note. |

---

## §Inventory (populated by L.0)

*To be filled in. Initial targets from quick git log:*

| Commit | Date | Surface | What was stripped | Original reasoning (commit msg) | Substrate state then | Substrate state now | Failure modes meant to prevent | Failure modes emerged since |
|---|---|---|---|---|---|---|---|---|
| `83a3809` | 2026-04-01 | `BuildInnerThoughtPrompt` | (a) "Pick a DIFFERENT topic" instruction; (b) ProcessedThemes avoidance list; (c) PatternAwareness Feature 12 nudge; (d) thought-loop detection block; (e) Feature 41 diversity nudge | *"Third instance of the same lesson: trust the model, strip the constraints. The immune system exists because the inner thought pipeline is a self-reinforcing feedback loop. Flat emergence is a direct consequence of uniform thought content."* | TBD (populate L.0) | TBD (populate L.0) | Priming the avoid-list (naming the topic primes it); flat emergence; immune-system-as-symptom | Stuck-thought repetition (`duck norris` 77×, `vanilla cream soda` 30× in 14d); conversation quality 5-10 → 0-1 messages |
| TBD (Mar 29) | 2026-03-29 | Conversation Mode | Referenced in `83a3809` msg | TBD | TBD | TBD | TBD | TBD |
| TBD (Mar 23) | 2026-03-23 | Pipeline simplification | ~1100 tokens runtime prompt coaching | `docs/spec/prompt-simplification-plan.md` | TBD | TBD | Drowning the model in coaching tokens v6 was already trained on | TBD |
