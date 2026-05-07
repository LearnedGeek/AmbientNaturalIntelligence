# Tier Interface Contract — 1-Pager

**Status:** DRAFT for Mark's morning read (May 6, 2026 evening). One artifact, one sitting. Decisions named with recommended answers + tradeoffs so reactions are fast.

**Why this exists:** the tier interface is the load-bearing contract that unblocks parallel work on Tier Separation, Theme N, Theme M.2+, and Theme G Layer 2/3. Lock the contract, the streams parallelize.

**Source design:** [`ANI-Epistemic-Grounding-Architecture.md`](./design/ANI-Epistemic-Grounding-Architecture.md). This 1-pager extracts the contract surface from that design + names the unresolved decisions.

---

## 1. The enum

`EpistemicTier { Facts, Episodic, Interior }`. Three values, mutually exclusive.

| Tier | What goes here | Retrieval semantics |
|---|---|---|
| **Facts** | Asserted reality about Mark or the external world | Default tier for *"what is true about Mark"* queries. Outreach SHARED frame queries this. |
| **Episodic** | Things Ani has said, things Mark and Ani have done together as recorded events | *"What happened recently"* queries. Conversation history. Continuity. |
| **Interior** | Ani's inner thoughts, reflections, world-self imaginings, world-experience musings | *"What is Ani holding right now"* queries. Self-context. Never returned for "what is true about Mark." |

**The architectural rule:** *generated content cannot enter Facts*. Anything Ani produces is Episodic (if it was said to Mark) or Interior (if it was kept to herself). Mark-asserted content is the only path into Facts. Bob Swanson never enters Facts. Kitchen lights never enters Facts. Today's amplifier dies.

## 2. Source → tier mapping (recommended)

Decision needed for every existing source. Counts from snapshot `ani-memory-snap-20260506-2007.db`:

| `source_name` | Count | Recommended tier | Reasoning |
|---|---:|---|---|
| `twilio-inbound` | 687 | **Facts** | Mark-asserted. Definitionally factual. |
| `character-seed` | 477 | **Facts** | Canonical character + world. Asserted-by-design. |
| `rss` | 211 | **Facts** | External-world content from real sources. |
| `weather` | 12 | **Facts** | External perception. Real-world fact. |
| `conversation` (Mark-role) | ~600 | **Facts** | Mark's messages within a thread. Mark-asserted. |
| `conversation` (Ani-role) | ~1200 | **Episodic** | Things Ani said. Real events; not facts. |
| `world-experience` | 443 | **Interior** | Ani's musings about her bookstore world. Per `ANI-Identity-Boundary-Design.md` §self-world: this is *Ani's domain*, legitimate creative latitude, but NOT factual. Goes Interior. |
| `reflection` | 1043 | **Interior** | Ani's own reflections. Self-context. |
| `silence-choice` | 70 | **Episodic** | Meta-event "I chose not to text." Real event, Ani-side. |
| `temporal-gap` | 8 | **Interior** | Ani noticing her own pacing. Self-observation. |
| **NULL** | **3934** | **Investigate before mapping** | 46% of all memories. Sample inspection shows mostly Ani's inner-thought-shaped content (Interior). Migration approach: classify by content shape, default Interior, exception list for Facts-shaped content. |

**Open question 1: NULL bucket.** Auto-classify (default Interior, regex/LLM exception list for Facts-shaped) OR manual review pass (slow but precise) OR purge with backup (radical, treats NULL as untrusted)?

**Recommendation:** auto-classify with default Interior. Sample shows the bucket is dominated by inner-thought content. Cost of misclassifying a Facts-shaped record as Interior is bounded (it just doesn't surface in "what is true about Mark" queries; it remains queryable via Interior path). Cost of the inverse is exactly the Bob Swanson failure. Default-Interior is the safe direction.

**🔒 LOCKED May 7, 2026 — Mark's call: manual review.** *"let's default interior but review these manually. it's slow yes, but we want to be sure to really move forward with certainty."* Migration sequence: build a sample-and-classify review tool (small batches, ~150-300 records/hour) → run review pass → migration script applies Mark's classifications + defaults remaining to Interior. Auto-classify path rejected; certainty over speed. ~13-26 hours of Mark's review time, scheduled per his cadence.

## 3. Cross-tier retrieval semantics

**Decision needed:** can a single retrieval query span tiers, or are queries tier-scoped?

Three options:

**A — Tier-scoped queries (strict).** Each call site declares which tier it queries. Outreach SHARED frame queries Facts only. Outreach INTERIOR frame queries Interior only. No mixing.
- Pro: cleanest semantics; tier identity is inviolate; easiest to reason about.
- Con: call sites must always pick a tier; no "give me everything" path.

**B — Tier-priority composite (graceful).** Queries return composite scores across tiers but with explicit tier-priority weighting. e.g., "what is true about Mark" returns Facts × 1.0 + Episodic × 0.3 + Interior × 0.0.
- Pro: backward-compatible with existing brute-force three-way scoring; less call-site refactoring.
- Con: tier weights are tunable knobs that drift; no architectural guarantee that Interior content can't surface for "what is true about Mark."

**C — Hybrid.** Most call sites tier-scoped (A); a small set of "everything I'm holding right now" queries use composite (B) with explicit tier visibility.
- Pro: precision where it matters, ergonomic where it doesn't.
- Con: two query modes to maintain.

**Recommendation: A (strict).** The whole point is architectural guarantee against the amplifier. Knobs aren't guarantees. Refactoring cost at call sites is bounded; pays for itself the first time it prevents a kitchen-lights recurrence. Theme N's outreach source-frame detection becomes the call-site declaration mechanism naturally.

**🔒 LOCKED May 7, 2026 — Mark's clarification + accept.** Mark asked: *"Does A mean she doesn't share what she's thinking on interior, or do we have two classes of outreach? If it's two classes that's great, but she should be clear on interior outreach that it's something she was thinking about."* Answer: **two classes (four, by source-frame).** Tier-scoped strict does NOT prevent Ani from sharing interior content; it forces the *frame* to be honest. Per §6, four source-frames map to specific tiers and specific composition framings:
- `SHARED` → Facts → *"remember when we…"*
- `ANI_DOMAIN` → Facts (canonical character-seed) → *"the bookstore is…"*
- `ANI_INTERIOR` → Interior → *"i was just thinking about…"*
- `WORLD_PERCEPTION` → Facts (rss/weather) → *"i saw this article…"*

Last night's kitchen-lights outreach under this contract would have been forced into `ANI_INTERIOR` frame: *"i was just thinking about how kitchen lights might feel softer at midnight"* — same novel content, honest framing, queries Interior tier not Facts. The mirror-trap caveat (May 6 16:30 CDT) is what `ANI_INTERIOR` exists to prevent.

## 4. Schema migration approach

Existing schema: `memories.tier` is `TEXT NOT NULL DEFAULT 'Standard'`, all 8697 rows are 'Standard'. The Anchored tier (Feature 16) was designed but never populated.

**Decision needed:** rename + remap, or add new column alongside?

**Recommendation:** rename column. The existing `tier` is single-value, populated for all rows, never touched the Anchored design. Rename `tier` → `epistemic_tier`, migrate values per §2 mapping in a single `UPDATE` script, drop the `Standard` value from the type. One migration, no schema doubling, no transitional dual-write code.

**Migration script shape (sketch):**
```sql
-- Step 1: add new column with default
ALTER TABLE memories ADD COLUMN epistemic_tier TEXT;

-- Step 2: populate per source_name mapping
UPDATE memories SET epistemic_tier = 'Facts'    WHERE source_name IN ('twilio-inbound', 'character-seed', 'rss', 'weather');
UPDATE memories SET epistemic_tier = 'Episodic' WHERE source_name = 'conversation' AND content LIKE 'Mark%';  -- needs refinement
UPDATE memories SET epistemic_tier = 'Episodic' WHERE source_name IN ('conversation', 'silence-choice') AND epistemic_tier IS NULL;
UPDATE memories SET epistemic_tier = 'Interior' WHERE source_name IN ('world-experience', 'reflection', 'temporal-gap');
-- Step 3: NULL bucket auto-classify (default Interior)
UPDATE memories SET epistemic_tier = 'Interior' WHERE epistemic_tier IS NULL;
-- Step 4: drop old column, rename new one, add NOT NULL
```

Backup before; verify counts after; service restart to pick up new schema.

## 5. Slice-reader interface (Theme M dependency)

Theme M's slices read from memory. After tier separation, slice-readers must declare which tier(s) they query.

**Decision needed:** can a slice query multiple tiers?

**Recommendation:** each slice declares one tier. Closed-conversation gist → Episodic. Recent-inner-thought aggregate → Interior. Contact-state → Facts. World-self → Interior. Register-state → derived (no direct memory query). Tension-state → derived. The slice-tier mapping is part of each slice's contract.

## 6. Outreach source-frame ↔ tier mapping (Theme N dependency)

Theme N's source-frame detection picks one of {SHARED, ANI_DOMAIN, ANI_INTERIOR, WORLD_PERCEPTION} for each outreach. Each frame queries a specific tier:

| Source-frame | Tier query | Composition prompt section |
|---|---|---|
| `SHARED` | Facts (Mark-asserted) | "ESTABLISHED FACTS" / "RECENT CONVERSATION" |
| `ANI_DOMAIN` | Facts (character-seed canonical) | "YOUR LIFE" |
| `ANI_INTERIOR` | Interior | "YOUR INNER STATE" (new section) |
| `WORLD_PERCEPTION` | Facts (rss/weather) | "EXTERNAL PERCEPTION" |

This is the §6.14 layer-2 contract for outreach. With the tier interface locked, Theme N can implement frame detection independently against this surface.

## 7. Memory_links and memory_audit

**memory_links:** 27,258 cross-references. After migration, links may span tiers. **Decision:** preserve all existing links (cross-tier links allowed) and add link-tier-traversal flag to retrieval if needed in a follow-up.

**memory_audit:** 7,365 records of memory operations. **Decision:** preserve as-is; audit log doesn't need tier classification.

## 8. The decisions you're making in one read

| # | Decision | Status |
|---|---|---|
| 1 | **§2 NULL bucket strategy** | 🔒 LOCKED (May 7) — manual review pass; certainty over speed |
| 2 | **§3 cross-tier retrieval** | 🔒 LOCKED (May 7) — A (tier-scoped strict); two-class outreach via four source-frames per §6 |
| 3 | **§4 schema migration** | 🔒 LOCKED (May 7) — rename column + remap; single script |
| 4 | **§5 slice-reader interface** | 🔒 LOCKED (May 7) — each slice declares one tier |
| 5 | **§6 source-frame ↔ tier mapping** | 🔒 LOCKED (May 7) — as tabled |
| 6 | **§7 cross-tier links** | 🔒 LOCKED (May 7) — preserved as-is |

**🔒 CONTRACT LOCKED May 7, 2026 06:30 CDT.** Streams parallelize from here.

## 9. What unblocks after lock

- **Tier Separation stream**: schema migration script, tier-scoped retrieval refactor, ~2-3 weeks.
- **Theme N stream**: outreach source-frame detection (N-A mechanism most likely given §6.14 symmetry), plugs into tier-scoped retrieval per §6, ~3-5 days.
- **Theme M.2-M.7 stream**: slice telemetry + remaining slices, slice-readers declare per §5, on existing M plan.
- **Theme G Layer 2 stream**: retrieval scoring within tier-scoped queries, no contract conflict.
- **Theme K + Theme H1**: fully parallel, no contract dependency.

## 10. What I'm doing tonight

Drafting this artifact + committing. NOT starting implementation; tier contract precedes implementation per Mark's "no bandaids; tier-first; parallelize after." Morning is yours to react to the six checkboxes.
