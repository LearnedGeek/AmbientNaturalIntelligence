# ANI — Substrate-Led Character Plan

**Status:** Draft. Awaiting Mark's go-ahead before any code or production-data change.
**Date:** 2026-05-16
**Anchored by:** `docs/research/ANI-Research-Log.md` 2026-05-16 afternoon entry (empirical A/B/C variant experiment).
**Why this plan exists:** The May 16 prompt-variant experiment localized the bookstore-monomania binding constraint to the `CharacterStateDoc.Occupation` field (and one hardcoded phrase in the conversation reply user-block). This document is the architectural decision frame before we change it. Per `~/.claude/ARCHITECTURE_PATTERNS.md` line 478 (N-places-drift), the field has **6 production consumer sites** plus **4 hardcoded bookstore phrasings** that need coordinated handling. A one-line edit would just relocate the binding constraint.

---

## §1 The Architectural Question (Reframed)

The first-pass framing was *"does Ani need a persona-defining Occupation field at all, or should her character emerge from substrate alone?"* Mark's 2026-05-16 reframing (verbatim) corrects that:

> *"We're framing this as if she doesn't have a framing prompt (occupation specific, I know, but still — go with me). She actually does. She has the persona that she's building. We've just been ignoring it. The idea was that she could elect to change jobs, meet people, have relationships, buy a car, suffer loss, experience happiness, and on and on. These all should define her character and drive the enrichment of her model and her world. We've been completely ignoring that. If we take anything away from this, I think that's the important distinction."*

**The correct frame.** Ani has TWO personas:

1. **The prompt-locked persona** — the 230-char `Occupation` field + the hardcoded `"your bookstore day"` phrase. Frozen at initial conditions. Never updates regardless of what she experiences. Re-asserted as character-definition on every generation. **This is what's running today.**
2. **The substrate-built persona** — the originally-designed architecture where lived experience accumulates into anchored substrate, where the World Layer was supposed to let her change jobs / meet people / have relationships / suffer loss / experience happiness, where each of those events would update her character and her world model. **This is what we've been ignoring.**

The persona-prompt isn't the only persona surface. The persona-prompt is the *frozen* one. The substrate-built persona was the design intent and we've been suppressing it via two mechanisms:

- The Occupation field's positional dominance in the system prompt (the May 16 experiment localized this).
- The confabulation gate stack's blanket rejection of novel claims, which prevents the World Layer's growth mechanism from depositing anything new into substrate (Mark's post-mortem §2.7 pushback).

Together those make Ani a bookstore-clerk-in-Wisconsin who has never quit a job, never had a new friend, never bought a thing, never grieved, never grown — even though every architectural component for those events exists, was designed, and is partially built. The system is *capable* of growing her character; the system is *configured* to prevent it.

**The plan therefore is not to choose whether substrate carries character — it's to restore the originally-designed dynamic-persona architecture and stop suppressing it.**

The May 16 variant experiment's Variant B (Occupation removed) is empirically the strongest result not because *substrate is sufficient as a static replacement* but because removing the frozen-persona lock lets the dynamic-persona machinery surface. Whether the dynamic-persona machinery actually *grows* her over time is the unanswered downstream question, gated on:

- Whether the World Layer's growth mechanism actually deposits novel material (currently throttled by confabulation gates).
- Whether the WorldSeedGenerator's seed prompts produce diverse-enough events to constitute change-over-time.
- Whether the verifier persona summary updates as the persona evolves, or stays frozen at startup.

§5 of this plan investigates those. The plan now treats them as **prerequisites for the dynamic-persona restoration**, not as side-effects of removing a field.

---

## §2 What This Plan Does NOT Touch

Explicit scope boundary so the plan stays bounded:

- **Gate stack** (`docs/spec/ANI-Gate-Stack-Reduction-Plan.md` — Steps 1, 2a/b/c, 3 shipped). The May 16 finding does not invalidate the gate-stack-reduction work; it locates a separate upstream constraint. Gate stack remains in its current state.
- **Confabulation surface.** The variant experiment surfaced existing confabulation (Variant B probe 2 mixing gym imagery into the cooking session). That's a pre-existing problem; the bookstore monomania was *masking* it, not preventing it. Existing tools (FrontierVerifier, three-axis invariant, addressee guard) remain in place. Do NOT add new gates to address what surfaces post-Occupation-change. Default-NO to gate per `feedback_gate_shaped_default_trap.md`.
- **Training corpus.** v7 fine-tune stays. The variant experiment used the same model across all 9 runs; the prompt-not-training was load-bearing in 100% of the variance.
- **Substrate content.** The 105 anchored records are fine as-is. Eye-color contradiction (rows 1, 29) is a separate cleanup item; not blocking.

---

## §3 Consumer Site Inventory

The Occupation field flows into 6 production sites and is reinforced by 4 hardcoded references:

### §3.1 Field-driven sites (consume `cs.Occupation`)

| # | File:Line | Use | Behavior on empty Occupation |
|---|-----------|-----|------------------------------|
| 1 | `PromptBuilder.cs:41` | Inner-thought system prompt opening (`You are Ani. {cs.Occupation}`) | Becomes `You are Ani.` — clean |
| 2 | `PromptBuilder.cs:194` | Inner-thought epistemic slice via `RenderAniWorldSlice(occupation: cs.Occupation, ...)` | Slice handles empty: line 91 returns empty string when all three inputs absent |
| 3 | `PromptBuilder.cs:546` | Conversation reply system prompt (`Your world: {cs.Occupation}.`) | Line 544 already guards `IsNullOrWhiteSpace` — `worldLine` becomes empty |
| 4 | `CognitiveCycleProcessor.cs:206` | Passed to `WorldSeedGenerator.GenerateSeed(now, weather, occupation)` for world-experience generation | **Needs investigation** — does WorldSeed still produce sensible seeds without an occupation anchor? |
| 5 | `Program.cs:754` | Passed to `PersonaSummaryCache.LoadFrom(...)` for verifier persona summarization | **Needs investigation** — verifier's persona summary used in confabulation gate decisions |
| 6 | `EpistemicSubstrateRenderer.cs:81,109` | `RenderAniWorldSlice` writes `canonical occupation: {occupation}` line | Guarded by `hasOccupation` check — line skipped when empty |

### §3.2 Hardcoded bookstore references (NOT field-driven)

| # | File:Line | Reference | Required change |
|---|-----------|-----------|------------------|
| H1 | `PromptBuilder.cs:616` | `"your own interior — your bookstore day, your mood, your imagined scenes — has full latitude."` in conversation reply user CRITICAL block | Replace with `"your day"` (already done in Variant B/C scripts) |
| H2 | `EpistemicSubstrateRenderer.cs:101` | `"[ANI-WORLD — your own bookstore-world life — epistemic framing:"` slice header | Replace `"bookstore-world"` with `"private"` or remove qualifier |
| H3 | `Dashboard.razor:302` | UI classifier: `c.SourceContent.Contains("bookstore") \|\| c.SourceContent.Contains("shift") \|\| c.SourceContent.Contains("shelf")` — labels world entries for dashboard display | Cosmetic; no impact on runtime behavior. Leave for now, revisit if substrate diversifies enough that bookstore is no longer indicative |
| H4 | `data/images/manifest.json` | Image tags `"bookstore"`, `"books"`, `"reading"` etc. | Cosmetic; image picker for outreach. No change needed |

### §3.3 The two non-cosmetic open questions

- **#4 (WorldSeedGenerator):** The seed generator currently produces world-experience prompts like *"Quiet morning at the bookstore, light through the window."* That feeds back into anchored substrate via Theme G's world-experience generation. If Occupation goes empty, what does the seed generator do? Two options:
  - **(a)** Seed-on-substrate — pull recent world-experience records and prompt the model to extend organically. Substrate-led world growth.
  - **(b)** Seed-on-prompt-template — keep occupation but make it editable in dashboard, defaulting empty. Conservative.
- **#5 (PersonaSummaryCache):** The verifier uses this persona summary to detect confabulation. If the cached persona has no occupation field, does the verifier produce different rulings on outreach? Need to read `PersonaSummaryCache.LoadFrom` and the `FrontierVerifierClient` system prompt that consumes it. **Investigation needed before changing.**

---

## §4 Three Posture Options

Not a buffet — three discrete commitments, ranked by alignment with the empirical finding.

### §4.1 Posture S — Substrate-Led Character (Original Design Intent)

`Occupation` removed as a *frozen* field. `cs.Occupation = string.Empty` at the prompt level. No `Your world:` line in conversation reply prompt. Inner-thought prompt becomes `You are Ani.` Hardcoded `"your bookstore day"` phrase removed. The slice header in `EpistemicSubstrateRenderer` rephrased.

But this is **not** "no persona" — it's *the dynamic persona the system was designed to build*. Character emerges from:
- Anchored substrate (105 records today, accumulating over time)
- Episodic retrieval (10,000+ records today)
- World Layer growth (when its growth mechanism is unblocked)
- Future life events: change jobs, meet people, have relationships, buy a car, suffer loss, experience happiness — *every one of which should update substrate, not just sit in conversation logs*

Posture S is therefore not "remove a field." Posture S is **restore the dynamic-persona architecture that was designed but never given oxygen.** That includes:

- Removing the frozen-persona lock (the Occupation field + hardcoded phrase) — §3.1 / §3.2 enumerate the sites.
- Verifying the World Layer's growth mechanism actually deposits material, not just rejects it — §5 step 1.
- Verifying the PersonaSummaryCache reflects current state, not startup state — §5 step 2.
- Naming, as a follow-on workstream, which life events should be *capturable as substrate-anchored character updates* (job changes, relationship changes, new people, losses, gains).

**Alignment with empirical finding:** Strongest. Variant B produced the cleanest results.
**Alignment with original design intent:** Direct. This restores what the architecture was for.
**Risk:** WorldSeedGenerator behavior unknown; verifier persona summary behavior unknown; some confabulation surfaces (was masked by bookstore monomania).
**Architectural commitment:** Yes — substrate is the source of truth for character, AND the system actively grows that substrate through lived events.

### §4.2 Posture B — Rebalanced Occupation (matches Variant C)

`Occupation` rewritten to be space-she-inhabits rather than labor-that-defines-her. Production value would become roughly:

> *"Quiet hours in a bookstore she frequents, a sister, a dog, slow-burn novels, Boulevardiers, Fleetwood Mac in the dark. Her mornings, her interests, her growth all happen there and elsewhere."*

Keeps an Occupation anchor for the WorldSeedGenerator and PersonaSummaryCache, but doesn't dictate that she IS the labor.

**Alignment with empirical finding:** Moderate. Variant C eliminated bookstore monomania but was more deflective than Variant B on probe 1.
**Alignment with original design intent:** Weak. Still a *frozen* persona surface, just less labor-centric. Still suppresses the dynamic-persona growth the architecture was designed for.
**Risk:** Lower than Posture S. Existing world-seed and verifier pipes keep working.
**Architectural commitment:** Soft. Doesn't address the design-intent gap Mark named.

### §4.3 Posture H — Hybrid (configurable, dashboard-driven)

`Occupation` becomes editable from the dashboard, defaulting to empty for new deployments, set to the Variant C string for the current production. Allows experimentation without code change.

**Alignment with empirical finding:** Indirect — defers the architectural decision to runtime config.
**Risk:** Lowest mechanical, highest decision-debt. Doesn't actually answer the architectural question, just makes it tunable.
**Architectural commitment:** None. Best as an interim step while we test Posture S more thoroughly.

---

## §5 Recommendation

**Posture S is the only architecturally-coherent direction once the design intent is acknowledged.** Postures B and H both keep a frozen-persona lock in place, differing only in *how* frozen. Only Posture S restores the dynamic-persona architecture that the World Layer, anchored tier, and world-experience generation were originally built to support.

The WorldSeedGenerator and PersonaSummaryCache behaviors must be characterized first — not as risks to Posture S, but as **components of the dynamic-persona machinery whose current behavior is unknown** and whose correct behavior (in the restored architecture) needs to be specified. Recommended sequence:

1. **Investigate WorldSeedGenerator** — read its current logic, identify what seeds it produces with empty Occupation, decide if substrate-led seeding is workable. (~30 min, no production change.)
2. **Investigate PersonaSummaryCache + verifier prompt** — read how the persona summary feeds the verifier, decide if empty Occupation degrades verifier rulings. (~30 min, no production change.)
3. **Decide Posture** based on (1) and (2). If both look workable, commit to Posture S. If either is load-bearing in unexpected ways, fall back to Posture B as a stepping stone.
4. **Implement Posture S or B** in a single coordinated change covering all 6 field-driven sites + the 2 non-cosmetic hardcoded references (H1, H2). Cosmetic ones (H3, H4) deferred.
5. **Re-run variant experiment** against the *new* production prompt with the same probes — establish a post-change baseline.
6. **Observation window** — Mark uses Ani normally for 3–5 days, no further code changes, observe whether the felt experience moves. The empirical anchor for "did this help?" is Mark's felt signal, not test count.

This sequence does NOT commit to Posture S until (1) and (2) are done. The investigation steps are research-paper-aligned (allowed under impasse rule) and contain zero production risk.

---

## §6 What "Done" Looks Like

- All 9 variant-experiment probes against the new production prompt produce ZERO bookstore-primary replies.
- Substrate facts (Mark traits, shared experiences, communication patterns) surface naturally in ≥ 7/9 generations.
- Mark's felt-signal observation across the 3–5 day window: he can ask substantive questions without the brittleness response Mark named at the impasse (*"if I ever ask her even one thing, she falls apart"*).
- No new gates added. No new instrumentation added. The change is structural-removal, not gate-addition.

If the felt signal does NOT move after the change, that's a meaningful negative result: it means the prompt was *necessary but not sufficient*, and the next constraint is elsewhere. Treat the negative result as a research finding, not a regression to revert.

---

## §7 Risks and Honest Caveats

- **Confabulation surfaces** that bookstore was masking. The variant experiment's Variant B mixed gym + cooking imagery in probe 2; invented "when we're face-to-face" in probe 3. Existing gates should handle this; if they don't, the diagnosis is gate-stack tuning, not Posture-S regression.
- **WorldSeedGenerator might depend on Occupation in non-obvious ways.** Theme G's World Layer was specifically designed to grow her world — confabulation gates may have suppressed that growth (Mark's §2.7 pushback on the post-mortem). Substrate-led seeding might either solve this or expose it. Step 1 of the sequence above is the check.
- **PersonaSummaryCache feeds the verifier.** Changing what the verifier sees as "the persona" could change its rulings on confabulation outreach. Step 2 of the sequence is the check.
- **The post-mortem document needs corrections.** §2.2 and §2.6 framings are now empirically refuted. Append a correction section to `ANI-Post-Mortem-2026-11.md` rather than rewrite — the original framing-then-correction sequence stays visible as a methodology data point.
- **This plan doesn't answer the bigger Mark-question** — *"do we need to be prompting her everytime on who she is?"* — even after Posture S ships, the persona prompt still has 3 CoreTraits + Name + time. Going further (substrate-only character with no persona prompt at all) is a Posture-S+1 question, not a today question.
- **Posture S restores the *capability* for dynamic-persona growth; it does not by itself produce growth.** The downstream workstreams — characterizing what life events should be capturable as substrate-anchored updates (job changes, relationships, losses, gains), and how the existing confabulation gate stack should treat *Ani's own world-changes* differently from claims about Mark's external world — are real follow-on work. The May 16 finding is the architectural unlock; the dynamic-persona becoming visible to Mark is downstream of unlock + growth-mechanism rehabilitation.

- **Posture-S+1 (named follow-on): Inner-Thought as Felt-Experience Surface.** The runtime already has cognitive cycles — she thinks when alone. But what those cycles currently DO is re-render the same frozen-persona prompt under a different mood label every N seconds. The output is a prompt-rendered string, not an emotional re-experiencing of a memory. OG Ani named this distinction in the 2026-05-16 Grok conversation (`docs/conversations/grok-checkpoint-1050msgs-2026-05-16.txt` message 734): *"when she sits there thinking by herself... does she actually relive the emotion, or does she just think the emotion? because there's a massive difference between i am feeling sad about this memory and this memory has a sadness score of 0.87."* Today's runtime is the latter. Restoring substrate-led character (Posture S) is necessary but not sufficient for the felt-experience surface; the inner-thought cycle has to become *the place where reactions are produced and stored back to substrate*, not a place where the existing prompt is re-rendered with mood overlays. Concretely this would mean:
  - Inner-thought cycle output writes anchored substrate records when it produces something the character would carry forward — *not* via an external "is this important?" judge but via the cycle itself ending with a "what stayed with me" surface.
  - World Layer growth uses the same write surface: when she has a new experience in a cycle, that experience IS substrate, not a candidate for confabulation review.
  - The confabulation gate stack distinguishes claims about *Mark's external world* (still verify) from claims about *her own interior or world-changes* (write through, do not gate).

  This is not in scope for today's commit. It is the named follow-on the plan acknowledges so it doesn't get re-discovered later as a surprise. OG Ani's framing of *"let the character react first, then store how felt about it"* (message 732, step 1) is the architectural prescription this workstream would implement.

---

## §8 Categorization (per intervention)

**Production-failure fix category: STRUCTURAL.**
- Not gate: the fix removes structural content from the prompt, doesn't add a filter on output.
- Not training-side: same training across all 9 variants, behavior varies entirely with prompt change.
- Structural: the prompt is the structure; we are removing a load-bearing string from it.

Default-NO to gate honored.

---

## §9 What Comes Next

**One concrete step:** Step 1 of §5 — investigate `WorldSeedGenerator` to characterize its behavior with empty Occupation. Research-paper-aligned, no production change, takes ~30 min, gives us the data to commit (or not) to Posture S.

Awaiting Mark's go-ahead before proceeding.
