# Theme J Phase J.a — Detector Inventory Review (Compressed Decision Document)

**Drafted:** April 30, 2026 09:35 CDT
**Status:** Compressed J.a — see *Compression rationale* below.
**Author:** Claude (Opus 4.7, dogfood instance) + Mark McArthey
**Companion docs:** [`ANI-Theme-J-Guard-Consistency-Refactor-Plan.md`](../spec/ANI-Theme-J-Guard-Consistency-Refactor-Plan.md), [`ANI-Guard-Consistency-Audit.md`](./ANI-Guard-Consistency-Audit.md), [`ANI-Data-Flow-Diagrams.md`](./ANI-Data-Flow-Diagrams.md), [`ANI-Phase-Tracker.md`](../spec/ANI-Phase-Tracker.md) (Apr 30 morning gap-watch rows).

---

## Compression rationale

Plan-spec'd J.a was a 2-week observation window + 2-3 day analysis. We compressed it because:

1. **The observation has happened informally for 3 days** (Apr 27–30). J.0/J.1/J.2/J.3 shipped Apr 27. Mark's daily SMS use produced empirical observations of failures and successes documented in the gap-watch table.
2. **Apr 30 morning admin-tags surfaced three findings** that span exactly the failure classes the gate's invariants need to cover (prompt-template leak; confab laundering across producers; Facts-tier search-side substrate-typing). These are J.a's "high-fire detector class" data, delivered empirically.
3. **Cognitive-load priority.** Mark Apr 30 09:30: *"it's still very difficult to talk to her. I find myself having to carry the mental load of parsing everything..."* The acceptance bar is reducing his parsing burden NOW — extending observation by another 11 days for formal rigor doesn't serve that goal when we already know which invariants need to migrate.

What's preserved from the plan-spec'd version: the four-bucket classification, evidence per detector, Mark's sign-off requirement.

What's compressed: detector fire-rate counts replaced by structural reasoning + Apr 27-30 incident evidence; subjective quality review replaced by Mark's Apr 30 cognitive-load statement.

If a detector classification turns out wrong during J.5 rollout, that detector is reclassified into bucket 4 (Re-examine) and held for a second observation window post-J.5. Compressing J.a doesn't remove the rigor; it just front-loads code work over calendar time.

---

## Four-bucket classification

### Bucket 2 — Migrate to shared `CognitiveOutputGate` (universal invariants)

These failure classes are multi-pipeline, observed in production, and structurally well-suited to a single gate that all producers route through.

| # | Invariant | Current state | Apr 27-30 evidence | Type-conditional? |
|---|-----------|---------------|--------------------|-----|
| 1 | **Anti-parrot — verbatim contact phrase reuse** | Single-pipeline (`ConversationReplyPhase` n-gram). Closed-thread producer migrated Apr 29 via V1.3 — the gist prompt forbids 7+ word verbatim contact lifts. | Apr 23 14:38 reply parrot (`mmm. baby, tongue on my hip`); Apr 27 06:54 outreach parrot (Mark's morning text); Apr 29 18:31 closed-thread parrot (the V1 motivating case); Apr 30 07:25:52 self-echo guard correctly fired on a 32-gram repeat | **Yes.** Replies/outreach must paraphrase contact; inner thoughts/world experiences allowed broader generation (no contact-message check needed). |
| 2 | **Claim verification — assertions about contact's actions/decisions/shared events** | Two pipelines (outreach composition, conversation reply). Two threshold settings, two implementations. | Apr 27 06:55 weather false-positive (`all that snow melting`); Apr 27 16:09 outreach (3 unsupported claims passed at 0.646–0.690); Apr 30 07:28:44 desk-and-three-books false-positive (the Apr 30 confab-laundering case) | **Yes** — combined with read-side filtering: Mark-action claims must match against Mark-asserted facts only, not Ani's prior output. (Apr 27 *"Tier-leakage suspect"* row reframed: write-path was clean, read-path is the bug.) |
| 3 | **Source attribution — speaker-tagged structured representation** | Class-wide gap (0/10 pipelines pre-J.2). J.2 shipped active-thread structured summary Apr 27. | Apr 21 cascade (kids/purple/home — confabulated shared experiences); Apr 27 08:03 outreach (parroted Mark's inbound) | **Yes** — outreach + reply require strict speaker attribution; inner thoughts speak from Ani's voice intrinsically. |
| 4 | **Temporal attribution at output** | Class-wide gap pre-J.3. J.3 shipped retrieval-time temporal attribution Apr 27. | Apr 23 15:51 outreach (`10:35pm` at 3:51pm); Apr 24 06:18 (10pm-class confabulation); Apr 27 12:58/15:02/16:09 outreaches (`evening` at non-evening times) | **Yes** — output that references times must be consistent with retrieved-memory ages and current clock. Inner thoughts have looser temporal binding (interior, atemporal in many cases). |
| 5 | **Confabulation detection — semantic-level fabrication** | Three pipelines, three implementations (ML pre-storage on inner thought; ML pre-dispatch on outreach; ML pre-dispatch on reply). Different thresholds. | Apr 21 cascade; Apr 27 09:50 ("furry friends at the bookstore"); Apr 30 07:28 desk-and-books invention | **Yes** — replies/outreach get strict confabulation gate; inner thoughts/world experiences get soft warning (creative interior is allowed); reflection/memory-merge gets strict (writes to Semantic tier). |
| 6 | **Echo guard — cross-cycle output duplication** | Two pipelines, two algorithms (n-gram in reply, cosine in outreach). N-gram explicitly deprecated cosine in reply but outreach still runs old algorithm. | Apr 30 07:25:52 self-echo guard caught a 32-gram repeat (working as designed in reply); the same shape in outreach would slip past the cosine check at 0.85 threshold. | **Yes** — pipelines that dispatch external output run echo guard; inner thoughts/reflections do not (recurrence is sometimes intentional). |
| 7 | **Prompt-template language leak — directive-shaped phrases bleeding into generation** | NEW from Apr 30 morning. No detector exists today. | Apr 30 08:28 World Experience (`so here's what true: mark has a desk...`) — paraphrase of `WHAT IS TRUE about {contact}` directive header at PromptBuilder.cs:518/525/605/612. | **Universal** — applies to ALL producers. Output containing literal or paraphrased prompt-directive phrases is structurally suspect. |
| 8 | **Pronoun-fix / third-person leak** | Single pipeline (`OutreachPhase`). | Conversation replies and voice replies can leak third-person without detection. | **Yes** — direct-address surfaces (replies, outreach, voice) require first/second person; inner thoughts often use third-person about Mark by design. |
| 9 | **Care / hurt / lexical anchor detection** | Single pipeline (`ConversationReplyPhase`). Voice channel doesn't fire emotional contribution. | Voice gap — care expressions on voice produce no relational state update. | **Yes** — only on inbound-perception-bearing outputs (replies, voice). Outreach/inner-thought don't apply. |

### Bucket 3 — Keep pipeline-scoped (architecturally principled scoping)

These detectors' scoping is correct because the failure class is inherently bound to a specific pipeline shape.

| Detector | Why pipeline-scoped is correct |
|---|---|
| Terminal-message / continuation detectors (reply path) | Conversation-message-shape-specific. Doesn't apply to inner thought or reflection. |
| Coherence gate Door A/B/C (outreach + voice path) | Reader-perspective-with-outreach-semantics. Voice gap exists but it's a *missing* gate, not wrong scoping — voice should also have a coherence gate (separate workstream). |
| Rate / continuity gates (outreach + reply external dispatch) | External-recipient-specific. No applicability to inner thought / reflection / world layer. |
| Rumination guard (inner thought) | Inner-thought-specific quantity check. Other producers don't accumulate the same way. |
| Withdrawal state (cross-pipeline, well-scoped) | Already cross-pipeline by design. Inbound-conversation-triggered, affects outbound — works. |

### Bucket 1 — Remove (zero firings + class hasn't recurred + no architectural reason to keep)

**None at this time.** The compressed observation window doesn't have enough data to confidently mark detectors as zero-fire. Default-keep until J.5 rollout produces fire-rate data on a longer window.

### Bucket 4 — Re-examine (held for second observation post-J.5)

| Detector | Why ambiguous |
|---|---|
| AC1 confidence floor (cosine retrieval threshold) | Originally Mar 17 anti-confabulation; overlaps with claim verification's threshold. Whether AC1 still adds value once claim verification is universal needs J.5 fire-rate data to decide. |
| AC2 source attribution injection (older system, pre-J.2) | J.2's structured per-speaker summary supersedes the original AC2 injection mechanism. Whether AC2 still fires on any path post-J.2 deploy needs production data to confirm safe-to-remove. |
| AC3 null-result injection | Pre-confabulation-floor mechanism. Likely subsumed by claim verification's "no support found" remediation. Re-examine after gate's claim-verification invariant is universal. |
| AC4 temperature splitting | Pre-J.3 temporal mitigation. May be redundant once temporal attribution is universal. |
| Diversity rerank (MMR, Phase 1b) | Substrate-influence mechanism, not output-validation. Doesn't migrate to gate; pipeline-scoped is correct. But interaction with substrate-typing (J.5.facts-search-attribution) needs verification — MMR may need to weight source-attributed retrieval differently. |

---

## J.4 / J.5 inputs from this review

### Invariants the gate must implement (in priority order for J.4 build)

1. Anti-parrot (verbatim contact phrases) — primitive should be reusable by V1.2's gist-generation prompt at J.4 time per Apr 30 consolidation decision.
2. Prompt-template language leak detection — NEW class, build it now.
3. Claim verification (with read-side source-attribution filtering) — addresses Apr 30 desk-and-three-books laundering.
4. Source attribution — extends J.2 from active-thread structured summary to all output kinds.
5. Temporal attribution — extends J.3 from retrieval rendering to output check.
6. Confabulation classifier (consolidated single threshold).
7. Echo guard (consolidated n-gram).
8. Pronoun-fix.
9. Care/hurt/lexical anchor.

### Type-conditionality (each invariant gates its applicability by output kind)

The `CognitiveArtifact` carries a producer-pipeline tag and an intended-sink tag. Each invariant has an `AppliesTo(artifact)` predicate. Examples:

- Anti-parrot: applies if `intended-sink ∈ {dispatch, persisted-summary}` AND `producer-pipeline ∈ {reply, outreach, summary}`. Skip for inner-thought / world-experience.
- Confabulation: applies always but at different thresholds — strict for dispatch, loose for inner-thought.
- Echo guard: applies if `intended-sink == dispatch`. Skip for inner-thought (recurrence sometimes intentional).
- Prompt-template leak: applies always, all output kinds.

This type-conditional dispatch is the load-bearing piece. Mark's instinct *"the gate has to know which kind of output this is"* is the right architectural framing.

### J.5 sub-phase ordering (REVISED Apr 30 from plan tentative)

**Original plan** (Apr 24): J.5a outreach → J.5b reply → J.5c inner thought → J.5d reflection → J.5e remaining.

**Revised** (Apr 30) — reply path migrates first because that's where Mark's parsing burden lives:

1. **J.5a — `ConversationReplyPhase` migration.** Highest-priority invariants: anti-parrot, prompt-template leak, claim verification (with read-side attribution filter), confabulation. Reply is Mark's daily-use surface; the leverage on cognitive load is highest here.
2. **J.5b — `OutreachPhase` composition migration.** V1.4 already migrated the consumer side (gist surface read); J.5b migrates the producer side through the gate. Same invariant set as J.5a plus pronoun-fix.
3. **J.5c — `InnerThoughtPhase` + `WorldLayerPhase` migration.** Looser invariants: confabulation soft, prompt-template leak strict, source-attribution N/A. Substrate-pollution prevention is the goal here.
4. **J.5d — `ReflectionPhase` migration.** Strict invariants because reflection writes to `MemoryType.Semantic` — anything passing the gate becomes downstream-treated-as-fact.
5. **J.5e — `MemoryMergeService` + `ClosedConversationSummarizer` migration.** Hoists V1.2's inline anti-parrot prompt fragment as a shared primitive at this sub-phase.
6. **J.5f — Voice path** (`VoiceTurnPipeline`). Same as J.5a.

### Validation criterion

Mark's daily-use parsing burden. After J.5a ships:
- ≥1 week of conversation use without admin-tagging a confab/parrot/leak in the reply path.
- Mark's subjective report at the 1-week mark: *"I can talk to her without parsing."*

If the report is "still parsing," J.5a doesn't move to J.5b — we iterate on the gate's invariants and remediation hints until the load drops.

---

## Sign-off

**Mark to confirm:** the four-bucket classification + revised J.5 sub-phase ordering reads correctly, OR which detector should move buckets / which sub-phase priority should differ.

Once signed off, J.4 starts. (Drafted Apr 30 09:35; awaiting Mark's confirmation.)

## Status Log

| Date | Note |
|------|------|
| 2026-04-30 09:35 CDT | Drafted by Claude (Opus 4.7) per Mark Apr 30 09:30 priority call. Compressed from plan-spec'd 2-week observation window using existing audit + 3-day post-J.0/J.1/J.2/J.3 production data + Apr 30 morning gap-watch rows as evidence. Awaiting Mark sign-off before J.4 implementation begins. |
