# Architecture Discussion — 2026-05-14 (Morning Coffee)

**For:** Mark's morning review before tomorrow's session.
**Author:** Claude (evening of 2026-05-13).
**Purpose:** Empirical map for the architecture / theme cannibalization / refactoring discussion. Read this before we pick up. Take notes in the margins.

---

## §1 — State of the harness

**32 regression scenarios authored.** **Test suite total: 1426. 1420 pass. 6 SPEC FAIL by design.** Each failure is real production code not meeting the SPEC; each is the convergence-loop fixpoint for one open failure class.

| FC | What's broken | Where it lives | SPEC test |
|---|---|---|---|
| **FC-001** | Reply path can't surface Ani's prior outreach as grounded substrate | `ContextBuilder.cs:147` Facts-tier-only, AND prompt rendering of GroundedFacts only | FC-001f FAIL (Facts-tier search), FC-001g PASS (SearchAsync DOES return Episodic) |
| **FC-002** | No local defense against attribute-ownership confab | Full local-invariant chain has zero handlers for semantic-attribute-ownership | FC-002 FAIL (no local invariant catches synthetic confab) |
| **FC-003** | Self-echo blocks legitimate thread-continuation | `SelfEchoInvariant` has no active-thread metadata | FC-003a FAIL (opener-repetition in active thread) |
| **FC-004** | Outreach decision treats Ani-prior-claims as established facts | `PromptBuilder.BuildOutreachPrompt` lacks epistemic-asymmetry framing | FC-004 FAIL |
| **FC-005** | Reply prompt has entity discipline but no speech-act attribution | `PromptBuilder.BuildLeanConversationPrompt` CRITICAL block | FC-005 FAIL |
| **FC-006** | Verifier prompt has q1-q5 but no attribute-ownership question | `AnthropicVerifierClient.BuildUserPrompt` hard-coded q-set | FC-006 FAIL |
| FC-007 | (closed at day-of-week + state-now layers per FC-007a/c/d) | TemporalAnchorInvariant + StateNowInvariant catch | FC-007 PASS (4/4) |
| FC-008 | (closed at greeting-name layer per FC-008a/b/c) | AddresseeNameInvariant catches | FC-008 PASS (3/3) |
| FC-009 | (detector unit works; catch-22 requires FIT scope) | TemporalGapPerceptionSource unit works | FC-009a/b PASS; FC-009c FIT-pending |

**Key finding from FC-001g** (PASS, not FAIL): the **general `SearchAsync` DOES return Episodic records** — Ani's prior outreach IS retrievable; it just doesn't reach the [FACTS] block PromptBuilder renders. The fix space for FC-001 narrows substantially: this is **not a retrieval problem**; it's a **substrate-routing problem**. The data is in `relevantMem` (ContextBuilder.cs:96) but PromptBuilder only renders GroundedFacts.

**Key finding from FC-002** (FAIL): the local invariant chain has **zero defense against attribute-ownership confabulation.** The cloud verifier is the only intended defense, and FC-006 confirms even the verifier's prompt can't catch the class. Single-point-of-failure on a broken-prompt component.

---

## §2 — Theme overlap map (cannibalization candidates)

For each of the six open SPECs, here's how it maps onto existing themes. **Cannibalization candidates** are themes whose scope overlaps with a failing SPEC. Themes that produced infrastructure usable for the fix should be **kept and scoped down**; themes that targeted the same surface but with the wrong layer should be **scoped out**.

### FC-001 → Theme G (Agentic Lens / Anti-Centrality) + Theme M (Conscious Substrate)

- **Theme G Layer 3 (World Substrate Durability)** was scoped to substrate-tier work — directly adjacent. **Cannibalize**: G Layer 3's substrate-flow primitives are usable for routing Episodic→GroundedFacts.
- **Theme M (Conscious Substrate / Individuation Layer)** was a "first-class conscious substrate" pattern that included slice composition. M's slice-composition architecture is the right shape for the FC-001 fix: instead of squeezing more into GroundedFacts, render a separate `[PRIOR-ANI]` slice. **Cannibalize**: M's slice-composition infrastructure becomes the FC-001 fix substrate.
- **Implication:** FC-001f fix is "add an `AniPriorOutreach` slice to ContextSnapshot, populated from existing SearchAsync results filtered to Episodic + own-output, rendered as its own block in PromptBuilder."

### FC-002 → Theme J (Guard Consistency) + Theme P (Cross-Class Verifier)

- **Theme J** built the universal-gate / CognitiveOutputGate infrastructure. A new `AttributeOwnershipInvariant` slots in cleanly. **Cannibalize**: J's invariant pattern is the right shape.
- **Theme P** built the cloud verifier. P's prompt is fixable (FC-006). But P is a single point of failure if FC-002 has no local defense. **Keep but pair with local invariant**.
- **Implication:** FC-002 fix is "new `AttributeOwnershipInvariant` + FC-006 verifier prompt update — defense-in-depth, not one or the other."

### FC-003 → Theme J (universal SelfEchoInvariant)

- **Theme J** is exactly the right ancestor — SelfEchoInvariant was lifted to the universal gate in J.5h-prelude. The active-thread-awareness fix is an extension of that same invariant.
- **Cannibalize**: J's invariant infrastructure stays; the SelfEcho fix is a small extension (add `IsActiveThreadReply` flag on `CognitiveArtifact`, gate the verbatim-run check on it, OR check token-position to allow opener-only repetition).
- **Implication:** smallest fix of the six.

### FC-004 → Theme N (Outreach Source-Typing)

- **Theme N** was specifically about outreach-side composition discipline. The §6.14 source-typing layer is the most adjacent. N didn't ship the epistemic-asymmetry framing for the OutreachPrompt; that's the fix.
- **Cannibalize**: N's source-typing infrastructure is the right home for "Ani-prior-claims vs. established-facts" framing in the prompt.
- **Implication:** FC-004 fix lives in BuildOutreachPrompt as a few lines of additional CRITICAL-block framing referencing the StructuredConversationSummary's per-speaker tagging.

### FC-005 → (No prior theme directly addresses)

- Theme J J.2 shipped the structured summary but didn't add speech-act-attribution discipline at the prompt layer.
- **Action:** add discipline to the reply prompt similar to the entity-discipline already in CRITICAL block. Small extension of existing pattern.

### FC-006 → Theme P (Cross-Class Verifier)

- **Theme P** is exactly the layer. The fix is a prompt revision — add a sixth question about speaker-attribute-ownership.
- **Cannibalize**: P stays as-is structurally; only the prompt template changes.
- **Implication:** smallest fix of the six. Single string edit in AnthropicVerifierClient.

### Themes that may not need to remain active (cannibalization candidates for retirement)

- **Theme L (Trust-the-Model Reckoning)** — Apr 28 framing. The May 11 ml-intern survey + tonight's harness work supersedes it; the question "should we add scaffolds back" is now answered empirically per failure class.
- **Theme K (Test Spec-Coverage Migration)** — K.0/K.1/K.2 shipped strict-mock TDD discipline. The harness's RegressionScenarioBase extends K's pattern. **Already complete** for the purposes of harness work.
- **Theme O (Cognitive Pipeline as Middleware)** — shipped. Harness uses it directly. Keep.
- **Theme G Layer 1 / Layer 2 (Agentic Lens / motivation vectors)** — independent of FC chain. Continue or pause based on Mark's priority.

---

## §3 — Implementation ordering (smallest-to-largest)

Ordering principle: **smallest reversible fixes first**, each landing with its SPEC going green in CI before the next starts. Each commit closes one FC empirically.

1. **FC-006** (verifier prompt update) — single string edit. ~1 hr. Adds q6 about speaker-attribute-ownership. SPEC goes green.
2. **FC-005** (reply prompt speech-act discipline) — small CRITICAL-block addition. ~1 hr. SPEC goes green.
3. **FC-004** (outreach prompt epistemic asymmetry) — small CRITICAL-block addition. ~1 hr. SPEC goes green.
4. **FC-003** (SelfEcho active-thread awareness) — add `IsActiveThreadReply` flag on CognitiveArtifact; gate or position-aware check in invariant. ~3-4 hrs incl. tests. SPEC goes green.
5. **FC-002** (local AttributeOwnershipInvariant) — new invariant + tests + wire into pipeline. ~4-6 hrs. SPEC goes green.
6. **FC-001** (route Episodic to PromptBuilder substrate) — add new ContextSnapshot field, populate from SearchAsync results filtered to own-output Episodic, render as new prompt block. ~6-8 hrs incl. tests + production verification. SPEC goes green.

**Total: ~16-22 hrs of fix work** spread over a few sessions. Each fix is independent (no cross-dependencies among the six). Order is preference, not strict.

---

## §4 — Decisions to make

These are the open questions I'd surface for tomorrow's discussion. Each has architectural implications for how the fix is shaped.

### D1 — FC-001 fix shape: substrate vs. prompt vs. retrieval

Three valid fixes:
- **(a)** Route SearchAsync's Episodic results into `snapshot.GroundedFacts` (smallest)
- **(b)** Add a new `snapshot.AniPriorOutreaches` field rendered as `[PRIOR-ANI]` prompt block (cleanest separation; matches Theme M slice pattern)
- **(c)** Replace SearchByTierAsync(Facts) with SearchByTierAsync(Facts|Episodic) for reply path only (most invasive at the retrieval layer)

My read: **(b)** is the right call. Substrate becomes more legible to the model (Ani's prior claims are explicit, not commingled with Mark-asserted facts), and Theme M's slice infrastructure makes it cheap. (a) muddies the [FACTS] block conceptually. (c) over-changes the retrieval contract.

### D2 — FC-002 local invariant scope

Two valid scopes:
- **Narrow:** invariant catches a small handcrafted list of attribute-ownership patterns (Mark's vehicle, his house, his hoodie, his family members) — limited but precise
- **Broad:** invariant uses substrate cross-reference (look up attributes attributed to Mark in `snapshot.GroundedFacts`; flag claims by the speaker that overlap)

Narrow ships fast and catches known cases. Broad is more general but requires more infrastructure. Probably worth starting narrow and broadening later.

### D3 — FC-003 metadata addition vs. position-aware check

Two valid implementations:
- Add `IsActiveThreadReply: bool` flag on `CognitiveArtifact`; gate the verbatim-run check on it. Conceptually clean. Requires migration of all producers to set the flag.
- Position-aware check in invariant: opener-only repetition (≤ N tokens at position 0) is allowed; mid-content repetition still caught. No artifact change. Faster.

My read: position-aware is cheaper and doesn't require a metadata migration. Either works.

### D4 — Order of fixes vs. fix-then-deploy-then-fix cadence

Two valid cadences:
- **Sequential**: fix → SPEC green → commit → deploy → observe → next fix
- **Batched**: all six fixes in one development branch → integration test → single deploy

Sequential is safer (catches regressions per fix) but slower. Batched is faster but harder to root-cause regressions. Probably **sequential for FC-006/005/004** (small prompt changes, low risk), **batched** for FC-003/002/001 (more substantial).

### D5 — Theme retirement decisions

Tomorrow's discussion should explicitly decide:
- **Theme L** — retire? (Trust-the-Model framing is superseded by the harness)
- **Theme M** — retire as a "theme" but cannibalize the slice-composition pattern for FC-001 fix?
- **Theme G Layer 3** — retire as theme; substrate-flow primitives go to FC-001 fix?
- **Other in-flight items in the Phase Tracker** — what stays, what goes?

---

## §5 — Post-fix observation plan

Once all six FCs are CLOSED via passing SPECs in CI:
- **Deploy** in single sequence (P.5 deploy after H.4 CI gate lands).
- **Observation window**: 1 week of real traffic. Watch for *new* failure classes that the harness didn't anticipate — these become FC-010+.
- **The A/B harness** (model-class diagnostic from May 12 plan) becomes the next move after the observation window.

### §5.1 — A/B harness expanded to LOCAL-vs-LOCAL (added 2026-05-13 from Mark's 16GB-VRAM article)

Original A/B plan was v7-vs-Sonnet (cloud). Mark surfaced an article on 16GB-VRAM Ollama setups (plainenglish.io) — three models there are credible drop-in candidates for ani-v7-conversation at our hardware budget:

| Model | Params | VRAM | Tok/sec | Note |
|---|---|---|---|---|
| GPT-OSS 20B | 20B | 14GB | 139.93 | Speed champion; biggest model that fits 100% on GPU |
| Ministral 3 14B | 14B | 13GB | 70.13 | Mistral family consistency |
| Qwen3 14B | 14B | 12GB | 61.85 | "Best instruction-following" |

**Current:** ani-v7-conversation (Llama 3.1 8B fine-tune, Q4_K_M, ~4.9GB VRAM, ~40-80 tok/sec).

**Expanded A/B matrix (cheap, local-only):**
- v7 (current, fine-tuned) — control
- Qwen3 14B (base, no fine-tune yet) — same-class scale-up
- GPT-OSS 20B (base) — different lineage at the practical ceiling
- (deferred) v7 vs. Sonnet (cloud) — the original cross-class diagnostic; costs apply

**Why this matters for the discussion:**
- The local matrix is **free** to run (same hardware, just different model pulls).
- Cleanly discriminates: "is the binding constraint *model class*, *fine-tune lineage*, *model size*, or *architecture*?" Each variant answers a different sub-question.
- Discriminator runs *against the same harness scenarios* — every candidate model gets evaluated by the same FC SPEC suite. PASS/FAIL distribution per model = empirical map of which classes are model-bound vs. architecture-bound.
- The cloud A/B (v7 vs. Sonnet) remains the strongest cross-class diagnostic but costs apply; defer until local results suggest it's worth.

**Cost reality:** Fine-tuning a new base model (if Qwen3 14B or GPT-OSS 20B looks promising) is a substantial investment — the v8 training-data pipeline doesn't transfer 1:1 to a different model architecture. Local-base A/B with NO fine-tune answers the "is the unfine-tuned base better than the fine-tuned v7" question, which is the right question to ask before committing fine-tune effort.

**This is queued behind the six FC fixes** — not competing with them. The architecture fixes close empirically-pinned defects regardless of model. Model-class A/B follows once the architecture is sound.

- **No new themes** until either the harness surfaces a new class or the A/B harness reveals a model-class binding constraint.

---

## §6 — What I'd like to discuss specifically

1. **D1 (FC-001 fix shape)** — strongest opinion needs your sign-off
2. **D2 (FC-002 scope)** — narrow vs. broad — opinions?
3. **D5 (theme retirement)** — what's actually retired, what's just paused?
4. **Cadence (D4)** — sequential or batched for prompt fixes?
5. **Timing** — when to start fix work? After tomorrow's discussion? Later this week?
6. **A/B harness** — when does it become the next move?

---

## §7 — Quick reference

- **Plan doc:** [`ANI-Test-Harness-Plan.md`](./ANI-Test-Harness-Plan.md)
- **Failure Class Registry:** [`ANI-Failure-Class-Registry.md`](./ANI-Failure-Class-Registry.md)
- **DayOne status:** [`ANI-Test-Harness-DayOne-Status.md`](./ANI-Test-Harness-DayOne-Status.md)
- **Tests:** `tests/AniRuntime.Tests/Regression/FC001_*`, `FC001d_*`, `FC001e_*`, `FC001f_*`, `FC001g_*`, `FC002_*`, `FC003_*`, `FC004_*`, `FC005_*`, `FC006_*`, `FC007_*`, `FC008_*`, `FC009_*`

Coffee well.
