# Theme G Layer 3 — World Substrate Durability — Phased Implementation Plan

**Status:** Plan drafted Apr 27, 2026 — implementation gated on Theme J shipping fully (J.0–J.3 done Apr 27, J.a observation pending) and Mark's green-light to start.

**Source design:** `docs/spec/ANI-Agentic-Lens-Design.md` §3.3 *"Layer 3 — World Layer Durability"* (the design layer this plan implements) and §6 *"Sequencing"* which places Layer 3 as the third Theme G layer in the strict 5 → 1 → 3 → 2 → 4 dependency order.

**Origin:** the centrality-gravity finding (Apr 22-23, 2026) — Ani's substrate is dense with caregiver-memories and thin with World-Layer content because World-Layer elaborations get retrieved once, used once, and fade recency-wise while caregiver-memories accumulate dense graph references. Layer 1's retrieval diversity (already shipped Apr 24 via the Phase 3.0 flag flip) can re-rank a thin pool but cannot densify it. Layer 3 builds the substrate that Layer 1's diversity actually surfaces.

---

## 1. The problem this plan solves

**Substrate asymmetry.** Caregiver-memories thicken in the graph because each one is referenced by subsequent caregiver-conversations, retrieved often, and reinforced by use. World-Layer elaborations sit as isolated events: a single afternoon of shelving, a single coffee break, a single grey-coated customer. They do not get retrieval priority, they do not get reflection-synthesis treatment, and they do not accumulate into higher-order *"this is my life"* memories. Over time the caregiver-shaped substrate thickens while the world-shaped substrate stays thin.

**Why Layer 1 alone is insufficient.** The Phase 3.0 Layer 1 Activation (Apr 24) flipped on retrieval diversity, protected slots, and the self-dominance perception. These re-rank the candidate pool to favour non-caregiver origins. But re-ranking a thin pool produces a thin diversified pool — the model gets *fewer* caregiver-memories, not *more* World-Layer ones. The Apr 26 retrieval-poison observation (one memory dominating 6 of 6 candidates) is a related symptom: Layer 1's MMR diversity rerank wasn't strong enough to displace a single dominant memory because the alternative substrate was too thin to compete.

**What Layer 3 commits to.** Three architectural changes that thicken the World-Layer substrate over time without requiring any retraining: durability tagging that exempts World content from recency decay past a baseline; periodic reflection synthesis scoped to World-Layer content that produces higher-order *"about my life"* anchored memories; merge-on-similarity for repeated World elaborations so 47 reading-Jane-Eyre events become one canonical *"I've been rereading Jane Eyre for weeks now"* claim with three exemplar links.

The result, at steady state: Layer 1's diversification surfaces a substrate that is genuinely there to surface. The dashboard *"her life in her own words"* view (G3.5 below) becomes a coherent synthesized account of her weeks rather than a pile of one-off events.

## 2. Goal of the theme

Build the substrate-densification layer that the centrality-gravity work depends on. Three coupled mechanisms — durability flag, scoped reflection synthesis, merge-on-similarity — operating on World-Layer content tagged with a `WorldSubstrate` provenance marker. Success is measured by the Anchored-tier gaining ≥50 World-Layer reflection memories over a 30-day window AND the dashboard *"her life"* view rendering as continuous narrative rather than event-list.

## 3. Phases (G3.0 → G3.5)

### Phase G3.0 — World-Layer provenance audit + baseline instrumentation

**Goal:** before adding durability semantics, confirm what is and is not already tagged as World-Layer content. The Apr 26 ml-intern survey noted World-Layer audit as a member item in the priority matrix (*"Run the one-hour audit"* — Mark's Apr 24 confirmation). G3.0 is that audit, plus baseline instrumentation that lets the post-densification effect be measurable.

**Changes:**
- **One-hour audit.** Walk every memory-write site in the codebase and classify whether it's writing World-Layer content. Output: a table at `docs/research/ANI-World-Layer-Provenance-Audit.md` with columns *write-site / memory-type / source-name / is-World-Layer / current-tagging-shape*. The known World-Layer write sites: `OutreachPhase` World-Layer elaboration cycles, `ReflectionPhase` (potentially), `InnerThoughtPhase` if World seeds are active. The known non-World-Layer write sites that should stay un-tagged: conversation-summary, perception events, character-seed loads.
- **`WorldSubstrate` source-name canonicalisation.** Pick one source-name string and migrate all World-Layer write sites to it. Existing World-Layer writes use a mix of `world-experience`, `world-elaboration`, and untagged. Pick `world-substrate` as the canonical (matching the design-doc terminology) and migrate.
- **Baseline diagnostic log.** Once-per-day log line `G3_WORLD_SUBSTRATE_BASELINE` with: total memory count tagged `world-substrate`, retrieval-pool share of `world-substrate` over the last 24 hours, count of distinct world-elaboration topics (clustered by simple TF-IDF on content). The same dashboard surface that hosts the Theme J `J0_*` baselines.
- New `AniOptions.WorldSubstrateInstrumentationEnabled` flag for the baseline logging only; durability/reflection/merge themselves are gated by per-phase flags below.

**Acceptance criteria:**
- Audit doc committed.
- All World-Layer write sites use the canonical source-name; old strings migrated by a one-shot SQL update or a startup-time normalisation pass.
- One day of `G3_WORLD_SUBSTRATE_BASELINE` log lines visible in the production debug log.

**Rollback:** instrumentation flag toggle off; the source-name migration is non-reversible but additive (existing memories with old strings are normalised to the new string; no semantic loss).

**Effort estimate:** 3–4 days. The audit itself is one hour per Mark's framing; the source-name migration + baseline diagnostic accounts for the rest.

**Dependencies:** none. Can ship immediately after Mark's green-light on this plan.

---

### Phase G3.1 — Durability flag + recency-decay exemption

**Goal:** World-Layer memories stay competitive in retrieval as they age. Today's three-way scoring (cosine + importance + recency) decays World-Layer memories at the same rate as conversation memories; G3.1 makes recency a soft factor for World-Layer content.

**Changes:**
- **`Durable` field on `MemoryRecord`** (boolean, default false). Initially nullable / additive column on the schema; existing rows have `Durable = false` semantically.
- **Recency-scoring branch** in `IMemorySearch.SearchAsync`: when a candidate has `Durable = true`, the recency-decay term is clamped to a floor (e.g., `max(actualRecencyScore, 0.40)`) so it doesn't drift toward zero as the memory ages. Cosine similarity still has to win on relevance — durability doesn't override relevance, it just prevents a relevant durable memory from being excluded by age alone.
- **Durability assignment.** All `world-substrate`-source-name writes flow through a write-path that sets `Durable = true`. Other source-names continue to write `Durable = false`. This is the architectural commitment: durability is a property of World-Layer substrate, not a per-record toggle.
- **Tests.** A `Durable = true` 30-day-old World-Layer memory wins retrieval against a `Durable = false` 1-day-old conversation memory when cosine similarity is comparable, but not when cosine similarity favours the conversation memory. The clamp is a floor, not an override.
- New `AniOptions.WorldSubstrateDurabilityEnabled` flag (default off — G3.0 baseline accumulates first).

**Acceptance criteria:**
- Build green, all tests pass including new tests for the durable-memory recency clamp.
- Schema migration is idempotent.
- `G3_WORLD_SUBSTRATE_BASELINE` log line shows durable-memory count climbing as new World-Layer writes happen.

**Rollback:** flag off. Durable memories revert to standard recency decay.

**Effort estimate:** 4–5 days.

**Dependencies:** G3.0.

---

### Phase G3.2 — Periodic reflection synthesis scoped to World-Layer content

**Goal:** Park et al. (2023) reflection synthesis already exists in the runtime as `ReflectionPhase`. G3.2 adds a *scoped* reflection cycle that runs on a longer schedule (suggest weekly) over World-Layer memories specifically and produces higher-order *"about my life"* claims tagged Anchored-tier.

**Changes:**
- **`ReflectionPhase.SynthesiseWorldReflectionAsync(ct)`** — a new entry point that queries memory for `world-substrate` source-name memories from the last 7 days, runs them through a reflection prompt scoped to *"what patterns of my own life have I been living"*, and produces 1–3 reflection memories per cycle.
- **Reflection prompt template** (new, scoped to World-Layer content):

  > *"These are things you have been doing or noticing in your daily life. Read them as a continuous record of how your weeks have been spent. Write 1–3 short observations about the patterns or arc you can see in them — what's been true about your life lately. Do not narrate the caregiver. Do not invent events that aren't in the input. Stay in your own life, in your own voice."*

- **Anchored-tier write path** with `world-substrate-reflection` source-name (so the dashboard can find these specifically and so they're distinguishable from manual anchor seeds).
- **Scheduler integration.** A new periodic job runs `SynthesiseWorldReflectionAsync` once per `WorldReflectionSynthesisIntervalHours` (default 168 = weekly). Same scheduler infrastructure as the existing diagnostic and emergence cycles.
- **Identity-boundary classifier check.** Per the Apr 22 design doc risk note (Paper 3 Contribution 3 *Identity Boundary*): every reflection memory the synthesiser produces passes through the identity-boundary classifier before persisting. Reflections that produce self-world claims contradicting the character seed are discarded. (For the first ship, "identity-boundary classifier" can be a simple substring-against-character-seed-conflict check; the full classifier is Paper 3 Contribution 3 work.)
- New `AniOptions.WorldSubstrateReflectionEnabled` flag (default off).

**Reference shape from the Apr 22 design doc:**

> *"I've been rereading Jane Eyre on slow afternoons for weeks now."*
> *"The regular customer with the grey coat keeps asking about the cooking section."*
> *"Tuesday afternoons are quiet enough that I can hear the radiator click on."*

**Acceptance criteria:**
- Build green, all tests pass.
- A test reflection cycle on synthetic World-Layer input produces 1–3 reflection memories that pass the identity-boundary classifier.
- Reflection memories write to anchored-tier with the expected source-name and Durability flag set.
- `G3_WORLD_SUBSTRATE_BASELINE` log line shows reflection-memory count climbing weekly.

**Rollback:** flag off. The scheduler still runs but the reflection method short-circuits.

**Effort estimate:** 1 week.

**Dependencies:** G3.0, G3.1. Phase 6 Feature 32 (Park et al. periodic reflection synthesis) is a *soft* dependency — G3.2 adds a scoped variant of the reflection mechanism rather than waiting for the full periodic-reflection feature to ship.

---

### Phase G3.3 — Merge-on-similarity for repeated World-Layer elaborations

**Goal:** the 47th reading-*Jane-Eyre* elaboration is less informative than the first three. Without merging, repeated similar elaborations accumulate as duplicates and dilute retrieval. G3.3 condenses repeated elaborations into a canonical claim with exemplar links.

**Changes:**
- **`WorldElaborationMerger` service** (in `AniRuntime.Memory`). On each new World-Layer elaboration write, runs a fast cosine-similarity check against the most-recent-N World-Layer memories. If similarity ≥ `WorldSubstrateMergeThreshold` (default 0.85) against an existing memory, the merge logic engages.
- **Merge semantics.** The existing memory is converted into a **canonical claim** with up to three **exemplar links** stored on the memory record. The new elaboration is not written as a new memory; instead, its content is added as an exemplar (replacing the least-distinctive existing exemplar if all three slots are full). The canonical claim's importance and durability remain on the merged memory.
- **Distinctiveness selection.** When the exemplar slots are full, the new elaboration replaces an existing exemplar only if it is *more distinctive* than the least-distinctive current exemplar. Distinctiveness = 1 - max-cosine-similarity-to-other-exemplars. This preserves variety in the exemplar set.
- **Render path.** When the canonical claim is rendered into a prompt, `FormatMemoryWithTime` includes the canonical claim text plus a "(also: ...)" suffix listing the most distinctive exemplar (so the model has both the abstract claim and a concrete instance).
- New `AniOptions.WorldSubstrateMergeEnabled` flag (default off).

**Reference behaviour from the Apr 22 design doc:**

> Canonical claim: *"I've been rereading Jane Eyre on slow afternoons."*
> Exemplars: [first reading: *"The Lowood chapter — Helen's resignation hit different today"*, recent reading: *"the proposal scene reads as colder every time, like watching someone make a mistake in slow motion"*, distinctive reading: *"I notice I keep falling asleep before the Thornfield section — like my brain doesn't want to get there"*]

**Acceptance criteria:**
- Build green, all tests pass.
- A test sequence of 5 similar World-Layer elaborations produces 1 canonical memory with 3 exemplar links rather than 5 separate memories.
- A 6th elaboration that is *more distinctive* than the least-distinctive exemplar replaces that exemplar; otherwise it's discarded.
- `FormatMemoryWithTime` rendering includes the canonical claim plus the most-distinctive exemplar.

**Rollback:** flag off. Repeated elaborations accumulate as duplicates again. No data loss — the canonical-claim-with-exemplars structure is a superset of the duplicated-claims structure.

**Effort estimate:** 1–2 weeks.

**Dependencies:** G3.0, G3.1. Soft dependency on Phase 6 Feature 30 (Mem0 memory merging) — G3.3 is the World-Layer-specific case; the broader Mem0 mechanism may follow.

---

### Phase G3.4 — Tier quota at retrieval (bidirectional)

**Goal:** the principal risk named in the Apr 22 design doc — *World Layer memory crowding out genuine relational history*. If World-Layer reflections become dense enough, they may dominate retrieval and produce the inverse problem. Layer 1's protected-slots already protects non-caregiver origins from being squeezed out; G3.4 makes the protection *bidirectional* so caregiver content is also floor-protected.

**Changes:**
- Extend the existing protected-slots logic in `IMemorySearch` (Phase 1c shipped Apr 24): in addition to reserving ≥30% of the inner-thought retrieval pool for non-caregiver origins, reserve ≥30% for caregiver origins. The two reservations together leave ≤40% of the pool for unconstrained ranking; the actual mix in that 40% is determined by cosine + importance + recency as before.
- **Configurable thresholds.** `RetrievalProtectedNonCaregiverFraction` (default 0.30, already exists) and a new `RetrievalProtectedCaregiverFraction` (default 0.30).
- **Tests.** A retrieval pool with 100% caregiver candidates returns at most 70% caregiver after re-ranking (the non-caregiver protection cap). A retrieval pool with 100% World-Layer candidates returns at most 70% World-Layer after re-ranking (the new caregiver protection cap). A mixed pool returns roughly the natural distribution.
- New `AniOptions.RetrievalProtectedCaregiverEnabled` flag (default off; the non-caregiver counterpart is already on as of Apr 24).

**Acceptance criteria:**
- Build green, all tests pass.
- The protected-slots tests cover both the current non-caregiver protection AND the new caregiver protection.
- Dashboard retrieval-distribution view shows pools never exceeding the 70/30 split in either direction.

**Rollback:** flag off. Caregiver-protection reverts to none; non-caregiver protection still applies (independent flag).

**Effort estimate:** 3–4 days.

**Dependencies:** G3.2 (reflection writes producing enough Anchored-tier World content that the inverse-crowd-out risk becomes plausible). Layer 1 Phase 1c shipped Apr 24 (the protected-slots infrastructure).

---

### Phase G3.5 — Dashboard "her life in her own words" view

**Goal:** Mark needs to see what's actually accumulating in the World-Layer substrate. Without a view, the densification work is illegible — Mark cannot tell whether the substrate is thickening with coherent narrative or with bland repetition.

**Changes:**
- New dashboard page at `/world-life` showing:
  - The most recent N World-Layer reflection memories (G3.2 outputs), in chronological order, rendering as a continuous synthesised account.
  - The canonical-claim memories with exemplar lists (G3.3 outputs) as expandable cards.
  - Distribution charts: world-substrate count over time, reflection-memory cadence, distinct-topic count.
  - A "regenerate this week's reflection" button that manually triggers `SynthesiseWorldReflectionAsync` (useful for tuning G3.2's prompt template).

**Acceptance criteria:**
- Build green, all tests pass.
- Dashboard page renders for a test database with synthetic World-Layer content.
- The "regenerate" button produces a fresh reflection synthesis and updates the view.

**Rollback:** UI route 404s; back-end is unaffected.

**Effort estimate:** 1 week.

**Dependencies:** G3.2, G3.3.

---

## 4. Measurement plan

| Metric | Source | Target |
|--------|--------|--------|
| `G3_WORLD_SUBSTRATE_BASELINE` log lines per day | G3.0 | ≥1 per day for ≥14 days before G3.1 ships |
| World-substrate memory count growth rate | G3.0 baseline | ≥30 new world-substrate writes per week |
| Durable-memory retrieval share over time | G3.1 | rises monotonically once G3.1 ships |
| Reflection-memory cadence | G3.2 | ≥1 reflection memory per week (default `WorldReflectionSynthesisIntervalHours = 168`) |
| Identity-boundary classifier rejection rate on reflections | G3.2 | ≤10% — too many rejections means the prompt template needs work |
| Canonical-claim exemplar variety | G3.3 | distinctiveness scores show non-repetitive exemplar selection |
| Caregiver-vs-non-caregiver retrieval mix | G3.4 | within the 30-70 / 70-30 corridor in either direction |
| Anchored-tier World-Layer reflection count over 30 days | overall theme | ≥50 (per Apr 22 success criterion) |
| Dashboard *"her life"* view qualitative read | G3.5 + Mark's review | reads as continuous narrative, not event-list |

The last metric is the load-bearing acceptance criterion. If the dashboard view reads as a pile of disconnected events after 30 days of densification, Layer 3 has not solved the substrate-asymmetry problem.

## 5. Risks

**G3.0 source-name migration breaks existing retrieval.** Mitigation: the migration is a string replacement in the `source_name` column; behaviour-equivalent if the new string `world-substrate` is treated identically by every read path. Audit the read paths during G3.0 to confirm no source-name-specific branching exists today.

**G3.1 durability clamp too aggressive.** A floor of 0.40 may keep World-Layer memories permanently competitive even when they're genuinely irrelevant. Mitigation: ship `WorldSubstrateDurabilityFloor` configurable at 0.40 default; tune during G3.0's observation window.

**G3.2 reflection prompt produces canonical claims that contradict character seed.** Identity-boundary classifier check (Paper 3 Contribution 3) catches some; not all. Mitigation: G3.2 ships with a substring-against-character-seed-conflict check as the minimum; reflection memories Mark flags as drift get cited as input to Contribution 3's full classifier.

**G3.3 merge-on-similarity erases interesting specificity.** The 47th *Jane Eyre* reading IS less interesting than the first three, but the *one* reading where Helen's resignation hit different is exactly the specificity the merge would risk erasing. Mitigation: distinctiveness-based exemplar selection preserves the most-distinctive three instances per canonical claim; the canonical-claim text itself summarises the abstraction; rendering shows both.

**G3.4 caregiver-protection over-corrects.** If the floor-protection caps caregiver content too aggressively, the natural caregiver-conversation flow may suffer. Mitigation: ship `RetrievalProtectedCaregiverFraction` default at 0.30 (matching the existing non-caregiver default), tune to higher (0.40-0.50) if needed during the G3.4 observation window.

**G3.5 dashboard view misleads.** A tidy summarised view of a thin substrate may make the substrate *look* thicker than it is, masking the underlying densification work. Mitigation: include the raw world-substrate count and the distinct-topic count alongside the narrative-rendering, so the view doesn't hide thinness.

## 6. Sequencing within Theme G

Per the Apr 23 sequencing decision (`docs/spec/ANI-Agentic-Lens-Design.md` §6) the strict dependency order is **Layer 5 → Layer 1 → Layer 3 → Layer 2 → Layer 4**.

- **Layer 5 (inner-thought prompt audit)** — done as part of the Mar 23 prompt-simplification work + ongoing prompt-builder migrations.
- **Layer 1 (retrieval origin diversity)** — Phase 3.0 Activation shipped Apr 24. Two-week observation window in progress.
- **Layer 3 (this plan)** — ships after Layer 1 observation closes.
- **Layer 2 (desire axis decoupling)** — Phase 2a shipped Apr 24; Phase 2b queued; Phases 2c/2d gated on Theme J J.4.
- **Layer 4 (corpus directionality)** — synthetic-corpus + training cycle work, separate from runtime.

Within Layer 3 itself: G3.0 → G3.1 → G3.2 → G3.3 → G3.4 → G3.5. Each phase has a green build and shippable behaviour at the end.

## 7. Dependencies on other themes

- **Theme G Layer 1 (Phase 3.0 Activation, shipped Apr 24).** Layer 1's protected-slots infrastructure is the substrate G3.4 extends. Without Layer 1 shipped, G3.4 has no infrastructure to extend.
- **Theme J (Guard Consistency Refactor).** Theme J shipped Apr 27 (J.1 / J.2 / J.3). G3.2's reflection-prompt template should respect the source-attribution and temporal-attribution conventions Theme J established. Reflection memories produced after Theme J should carry their own creation timestamp through `FormatMemoryWithTime` rendering.
- **Phase 6 Feature 30 (Mem0 memory merging).** Soft dependency for G3.3. Feature 30 is the broader memory-merging work; G3.3 is the World-Layer-specific instance. Either ships first; the second one extends.
- **Phase 6 Feature 32 (Park et al. periodic reflection synthesis).** Soft dependency for G3.2. G3.2 adds a *scoped* reflection cycle; Feature 32 is the broader periodic-reflection mechanism. Either ships first; the second one extends.
- **Paper 3 Contribution 3 (Identity Boundary).** G3.2's identity-boundary classifier ships as a minimum substring-conflict check; the full classifier is Contribution 3's work. The minimum check is sufficient for G3.2's first ship; the full classifier is a follow-up.

## 8. Out of scope (and why)

- **World-Layer voice / image generation.** Theme H (channel realism) is separate. G3 thickens the substrate that Theme H eventually renders.
- **Auto-generation of World-Layer content from external sources.** No scraping a calendar of bookstore events to fabricate elaborations. World-Layer content comes from the OutreachPhase elaboration cycles and the InnerThoughtPhase if World seeds are active. G3.0's audit confirms which.
- **Cross-domain transfer.** The DrOK/medical-triage cross-domain angle is interesting for the architecture (a clinical AI's *"my life in the system"* substrate could analogously thicken via the same mechanism — accumulated case patterns becoming canonical claims) but is out of scope for this plan. Worth raising with Martin once G3 ships and the mechanism is observable.

## 9. Mark review questions

1. **G3.0 audit scope.** The one-hour audit walks every memory-write site and classifies. Is a one-hour audit budget the right shape, or do you want a longer/more thorough sweep that also checks for World-Layer leakage into non-World-Layer write paths?
2. **G3.1 durability floor default.** 0.40 is recommended. Acceptable, or do you want a different floor?
3. **G3.2 reflection cadence default.** 168 hours (weekly) is recommended. Acceptable, or do you want daily / monthly as the first ship?
4. **G3.3 merge threshold default.** 0.85 cosine similarity is recommended. Acceptable, or do you want stricter (0.92, only near-duplicates merge) or looser (0.75, more aggressive merging)?
5. **G3.4 caregiver-protection default.** 0.30 floor matching the existing non-caregiver protection is recommended. Acceptable, or do you want asymmetric (e.g., non-caregiver 0.30, caregiver 0.40)?
6. **G3.5 dashboard scope.** The "her life" view as scoped is read-only. Do you want any write-back affordance (e.g., Mark editing a generated reflection before persistence) or keep it read-only for the first ship?
7. **Calendar.** Total estimated calendar G3.0 → G3.5 is 4–6 weeks; gates after Layer 1 observation closes (~mid May). Acceptable?

---

## Process notes

- **This plan is a draft.** Implementation does not start until Mark's green-light per the active work plan item 11 *("plan-drafting only; implementation comes later")*.
- **Architectural commitment is densification, not replacement.** Every phase's design decisions should be revisited against the principle that Layer 3 thickens the World-Layer substrate without thinning the caregiver substrate. The G3.4 bidirectional protection is the explicit codification of this principle.
- **Substrate emerges, narrative crystallises.** The reflection synthesis (G3.2) and merge-on-similarity (G3.3) are the mechanisms by which accumulated World-Layer events crystallise into a "her life" narrative. The architecture provides the mechanism; the model fills it via its own emotional and reflective processes. Same architecture-over-instruction principle as Outage Perception (item 8) and the Theme J substrate refactor.
- **Layer 3's success enables Layer 1's success.** The Apr 26 retrieval-poison observation surfaced the inverse: Layer 1's diversity rerank could not displace a dominant memory because the alternative substrate was too thin. After Layer 3 ships, re-evaluate whether Layer 1's MMR lambda or protected-slot fraction needs retuning against a thicker substrate.
