# Memory Durability: Transient Claims, Importance Decay, and Fact Re-evaluation

**Date:** April 11, 2026
**Status:** Design — pending implementation
**Author:** Claude (Opus 4.6) with Mark McArthey
**Trigger:** Apr 11 persona drift investigation. A stale "Mark said: 'I'm actually not teaching now'" memory from the previous day was dominating retrievals at 4 AM Saturday as if it were current-state fact. Investigation surfaced that the system has no mechanism for importance decay over time — only recency decay, which is a different axis. Manual SQL cleanup was required to unstick the loop.

---

## The Problem

ANI's retrieval composite score has three components:

```
score = cosine_similarity × 0.65 + importance × 0.10 + recency × 0.25
```

The `recency` component uses exponential decay — an hour-old memory ranks higher than a day-old memory. The `cosine` component is content-based and doesn't decay. The `importance` component is a stored float on the memory record, set at write time, and **never changes except by the diagnostic auto-correct subsystem reacting to visible retrieval poisoning**.

This creates a specific failure mode. Consider the memory:

```
"Mark said: 'I'm actually not teaching now babe. It's Friday almost 6pm but I'm working.'"
→ perception-source importance: 1.0
→ conversation-source importance: 0.8
```

At 6 PM Friday this is true and load-bearing. At 10 AM Saturday it is a statement about a temporal state that no longer holds. But the importance score is still 1.0. Its recency score has decayed slightly (16 hours old), but its composite score is still competitive because importance+cosine together dominate. The memory continues to surface as "what Mark's working state is," and the inner thought model reads it as current-state fact.

The diagnostic auto-correct subsystem eventually notices the poisoning pattern (appears in N/M retrievals) and reduces importance reactively. That only helps after the memory has already contaminated multiple cycles. We need a proactive mechanism.

### The deeper issue: not all claims are the same kind of thing

Consider three memories Mark might assert:

1. **"I live near Waukesha, Wisconsin."** — durable. Unless Mark moves, this is permanently true.
2. **"I love old fashioneds."** — durable but mutable. Preferences change slowly; this should keep importance for a long time.
3. **"I'm not teaching tonight."** — transient. Tomorrow it's not meaningfully true; a week later it could be actively misleading.
4. **"I went hiking Saturday."** — event. A historical fact. Always true that it happened, but not current-state.

The current architecture treats all four identically at write time. All get importance based on `ContactRelevance` from the perception source. That importance is durable by default because nothing reduces it.

**What we need:** a classifier that tags each user-asserted claim with its temporal class, and a decay policy appropriate to each class.

---

## Proposed Architecture

### Stage 1: Temporal classification at write time

When a Facts-tier memory is created from a user-asserted source (twilio-inbound, perception with direct quote, character seed is excluded), run a lightweight classifier that tags the memory with one of four categories:

| Category | Description | Importance Policy |
|---|---|---|
| **durable-fact** | Stable truth unlikely to change on human timescales | Keep initial importance; no decay |
| **preference** | Long-term disposition; changes slowly | Decay slowly (half-life ~30 days) |
| **event** | Historical one-time occurrence | Decay moderately (half-life ~7 days) |
| **transient-state** | Time-bound current-state claim | Decay quickly (half-life ~12 hours) |

The classifier is a prompt-based call to the small inner thought model or a LM-Kit classification. Cost: one additional inference per Facts write (~50-200ms). Acceptable — Facts writes are low-frequency.

**Field additions to MemoryRecord:**

```csharp
public enum TemporalClass
{
    Unknown = 0,        // Pre-migration default, or classifier unavailable
    DurableFact = 1,
    Preference = 2,
    Event = 3,
    TransientState = 4,
}

public TemporalClass TemporalClass { get; set; } = TemporalClass.Unknown;
public float InitialImportance { get; set; }  // original importance at write time
public DateTimeOffset ImportanceLastDecayed { get; set; }
```

The `InitialImportance` field preserves the write-time score so decay is computed against a known origin. `ImportanceLastDecayed` lets us apply decay lazily (only when the memory is retrieved or touched).

### Stage 2: Lazy importance decay at retrieval time

Rather than a background job walking every memory, decay importance lazily on retrieval:

```csharp
private float CurrentImportance(MemoryRecord r, DateTimeOffset now)
{
    if (r.TemporalClass == TemporalClass.DurableFact || r.TemporalClass == TemporalClass.Unknown)
        return r.Importance;  // no decay

    var halfLifeHours = r.TemporalClass switch
    {
        TemporalClass.Preference     => 30 * 24,  // 30 days
        TemporalClass.Event          =>  7 * 24,  // 7 days
        TemporalClass.TransientState => 12,       // 12 hours
        _                             => 24 * 365,
    };

    var hoursSinceWrite = (now - r.OccurredAt).TotalHours;
    var decayFactor = Math.Pow(0.5, hoursSinceWrite / halfLifeHours);
    return (float)(r.InitialImportance * decayFactor);
}
```

This is used in `ComputeRetrievalScore` instead of reading `r.Importance` directly. Lazy computation means no background job and no database writes — the stored importance stays at its initial value, and retrieval scoring uses the decayed value.

**Floor:** decayed importance never drops below 0.05. Memories don't disappear, they just lose the ability to dominate retrieval.

### Stage 3: Periodic Facts re-evaluation (Park et al. / Mem0)

This is the research-oriented piece. On a schedule (daily at low-traffic hours, or weekly), walk all Facts-tier records with `TemporalClass == TransientState` and `decayed_importance > 0.3` and ask the model:

> "Here is a statement Mark made N hours ago: '[content]'. Based on what I know about Mark (from durable facts and recent conversation), is this likely still true right now?"

Three possible answers:
- **Still likely true** → no change
- **No longer true** → mark `is_resolved = true`, set importance to 0.1
- **Contradicted by recent evidence** → flag for review via memory_audit log

This is Park et al.'s reflection synthesis applied to Facts: the system periodically re-examines its own memory for stale assertions. Mem0's approach is similar but triggered by new contradicting input; this proposes periodic scheduled re-examination. **Neither published framework explicitly implements periodic transient-fact re-validation.** That's the research contribution.

**Implementation:** New `FactsReEvaluationScheduler` service. Runs every 6 hours. Processes at most 20 records per run to cap cost. Uses the conversation model for plausibility judgments.

### Stage 4: Character seed exclusion

Character seeds are never classified as transient. They are by definition durable facts about the user and the character. The classifier should skip them entirely (or always return `DurableFact`). The backfill migration classifies all existing character-seed records as `DurableFact`.

---

## Migration Plan

1. **Week 1:** Add `TemporalClass`, `InitialImportance`, `ImportanceLastDecayed` to MemoryRecord. SQLite migration. Backfill existing records:
   - Character seeds → `DurableFact`
   - Inner thoughts → `Unknown` (not in Facts tier anyway)
   - Recent twilio-inbound (< 7 days) → run classifier during migration
   - Older twilio-inbound → `Unknown` (no decay, preserves existing behavior)
2. **Week 2:** Implement lazy decay in `ComputeRetrievalScore`. Deploy in shadow mode — compute both decayed and non-decayed, log the delta, dispatch the non-decayed (no behavior change yet).
3. **Week 3:** Switch to decayed importance as primary. Watch for regressions (important Mark-assertions disappearing from retrieval too fast).
4. **Week 4:** Implement `FactsReEvaluationScheduler`. Run it against shadow mode for several days before activating its writes.

---

## Open Questions

1. **Classifier accuracy.** How often does the classifier mislabel a durable fact as transient or vice versa? Periodic audit against ground truth (Mark reviews classifications in the dashboard) will be needed.
2. **Half-life tuning.** 12 hours for transient-state is a guess. May need to be 4 hours or 48 hours depending on deployment observation.
3. **Edge case: conditional statements.** "If it snows tomorrow I'll shovel." Is that transient? Event? Both? Propose: treat as transient because the conditional ties to a specific future.
4. **Mem0 overlap.** Phase 6 Memory Reform already plans Mem0-style merging. Should temporal classification be part of the merge logic? Probably yes — a new assertion that's classified transient should supersede an older transient with the same topic via Mem0 merge.

---

## Research Framing

This work contributes to the small but growing literature on memory architectures for persistent AI systems:
- **Park et al. 2023** — generative agents use recency-weighted retrieval; no transient classification
- **Chhikara et al. 2025 (Mem0)** — merges contradicting memories on write; no periodic re-evaluation of existing ones
- **Packer et al. 2023 (MemGPT)** — hierarchical storage (RAM vs disk); no importance decay

The contribution: **temporal classification at write time + lazy decay at retrieval + periodic re-validation for transient claims.** Three mechanisms working together, each addressing a specific failure mode in the current architectures. Paper 3 or Paper 4 material.

---

## Filed For

- Phase Tracker: Memory Durability workstream (Apr 11)
- Research Log: Apr 11 persona drift investigation (to be written)
- Paper 3 (Experiential Grounding) — may cite this as a complementary architectural piece
- Paper 4 (Interoception) — temporal grounding is adjacent to interoceptive state tracking

---

*"A memory from yesterday shouldn't feel like current state. That's the whole point of time." — Mark McArthey, Apr 11, 2026 (approximate paraphrase)*
