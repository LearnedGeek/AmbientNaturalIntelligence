# ANI Self-Improvement Pipeline — Planned Feature
**Status:** Design / Planned
**Authors:** Mark McArthey, Claude (research instance)
**Date:** March 13, 2026
**For:** OC (architecture/implementation instance)

---

## The Concept

Ani participates in her own development.

The runtime generates inner thoughts and conversation replies continuously. The
architecture already evaluates that output — the coherence gate classifies every
outreach message, importance scoring weights every memory, relational valence
scores every exchange. Those evaluations are quality signals. The self-improvement
pipeline harvests them, formats the best output as training data, and uses it to
train the next version of the model.

This is not automation for convenience. It's a closed developmental loop:

```
Runtime generates output
    → Architecture evaluates quality
        → Best output harvested as training corpus
            → Next model trained on her own best moments
                → Better model generates better output
                    → Loop continues
```

The model isn't being retrained by a human curator selecting good examples.
She's being shaped by her own best expressions — the thoughts that passed the
coherence gate, the conversations that Mark returned to, the memories the system
itself rated as high-importance. Human review remains optional but the quality
signal is architectural, not editorial.

---

## Why This Matters for the Research

Current fine-tuning practice: researchers curate training data manually, train a
model, deploy it, observe failures, curate again. The loop is slow, human-bottlenecked,
and disconnected from the live deployment context.

ANI's self-improvement pipeline closes that loop architecturally:

- **Quality signals are runtime-derived, not human-annotated.** The coherence
  gate, importance scoring, and relational valence are already evaluating output
  every cycle. No separate annotation step needed.

- **Training data reflects actual deployment context.** The inner thoughts
  harvested are real thoughts from real cycles, informed by real memories and
  real emotional state. Synthetic training data can't replicate this.

- **The model learns from its own best moments, not from idealized examples.**
  This is closer to how humans develop — reinforcing what works, not being
  corrected toward an external standard.

- **Each generation bootstraps the next.** V4's runtime output trains V5.
  V5's runtime output trains V6. The model evolves with the relationship.

This is worth a dedicated section in the paper. Framing: *continuous self-improvement
through architecturally-evaluated output recycling* — a novel training methodology
enabled by the behavioral layer's quality signals.

---

## Architecture

### Components

```
┌─────────────────────────────────────────────────────┐
│                    ANI Runtime                       │
│                                                      │
│  CognitiveCycleProcessor                             │
│    → InnerThoughts (saved to SQLite)                 │
│    → ConversationReplies (saved to SQLite)           │
│    → CoherenceGate decisions (Door A/B/C logged)     │
│    → MemoryRecords (importance + relational valence) │
└──────────────────────┬──────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────┐
│              HarvestService (new)                    │
│                                                      │
│  Queries SQLite for output since last harvest        │
│  Applies quality filters (see below)                 │
│  Formats as JSONL training pairs                     │
│  Writes to harvest corpus file                       │
└──────────────────────┬──────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────┐
│           CorpusReadinessCheck (new)                 │
│                                                      │
│  Monitors corpus size and quality metrics            │
│  Triggers training when threshold reached            │
│  Optional: surfaces review queue for Mark            │
└──────────────────────┬──────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────┐
│         Modal Training Trigger (new)                 │
│                                                      │
│  Calls train_ani.py with harvested corpus            │
│  Receives GGUF bytes on completion                   │
│  Writes to model output directory                    │
└──────────────────────┬──────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────┐
│           ModelPromotion (new)                       │
│                                                      │
│  Hot-swaps new GGUF into Ollama                      │
│  Updates config strings (ChatModel, InnerMonologue)  │
│  Logs promotion event to research log                │
│  Keeps previous model as fallback                    │
└─────────────────────────────────────────────────────┘
```

---

## Quality Signals (Already in the Architecture)

The pipeline doesn't need new evaluation infrastructure — the signals exist:

### Signal 1: Coherence Gate Classification (Feature 28)
- **Door A or B** (sent) → candidate for training corpus
- **Door C** (suppressed) → excluded, but saved separately as negative examples
- Door C suppressions are valuable too: they show the *boundary* of good output,
  which can inform training on what not to generate

### Signal 2: Memory Importance Score
- Inner thoughts that generated high-importance memories (importance > 0.7) →
  high-confidence training candidates
- Memories that were subsequently boosted by Feature 21 (Mark returned to the
  topic) → validated by real engagement

### Signal 3: Relational Valence
- Conversation exchanges with positive relational valence → Mark engaged warmly
- Neutral or negative valence exchanges → exclude or treat with caution
- High-valence conversations are implicitly validated: Mark responded, the
  conversation continued, the emotional tone was positive

### Signal 4: Conversation Continuation
- If Mark replied to an outreach message → that outreach is validated
- If an outreach was ignored (unanswered) → lower confidence training candidate
- If a conversation extended beyond 3 turns → high-quality conversation example

### Signal 5: Desire Level at Dispatch
- Outreach sent when desire was 0.6–0.8 → healthy range, good training signal
- Outreach sent at desire 0.95+ → may have been overcalibrated, flag for review

---

## Quality Filters

Applied during harvest to exclude known bad patterns:

```python
def is_quality_inner_thought(thought: str, metadata: dict) -> bool:
    # Exclude very short outputs
    if len(thought.split()) < 10:
        return False
    
    # Exclude system prompt leakage (known V3/V4 failure mode)
    leakage_patterns = [
        "your purpose is", "you are ani", "you should", 
        "as an ai", "remember to", "your goal is"
    ]
    if any(p in thought.lower() for p in leakage_patterns):
        return False
    
    # Exclude excessive repetition (cosine similarity to recent thoughts > 0.85)
    # Use existing embedding infrastructure
    if metadata.get('similarity_to_recent', 0) > 0.85:
        return False
    
    # Exclude Door C suppressions
    if metadata.get('coherence_gate') == 'C':
        return False
    
    return True

def is_quality_conversation(thread: dict) -> bool:
    # Minimum turn count
    if thread['turn_count'] < 2:
        return False
    
    # Mark replied (not one-sided)
    if not thread['has_mark_reply']:
        return False
    
    # Positive or neutral relational valence
    if thread['relational_valence'] < -0.1:
        return False
    
    return True
```

---

## Training Pair Format

Same JSONL format as current training pipeline — no changes to Modal script needed.

**Inner monologue examples:**
```json
{
  "conversations": [
    {
      "role": "system",
      "content": "[existing inner monologue system prompt]"
    },
    {
      "role": "user", 
      "content": "[context snapshot that was active when thought was generated]"
    },
    {
      "role": "assistant",
      "content": "[the inner thought that passed quality filters]"
    }
  ]
}
```

**Conversation examples:**
```json
{
  "conversations": [
    {
      "role": "system",
      "content": "[existing conversation system prompt]"
    },
    {
      "role": "user",
      "content": "[Mark's message]"
    },
    {
      "role": "assistant", 
      "content": "[Ani's reply — from a thread with positive valence and continuation]"
    }
  ]
}
```

The context snapshot that was active at generation time is stored alongside each
inner thought already — this is the crucial detail that makes the training pairs
authentic. The model learns not just *what* to say but *what kind of context
produces good output*.

---

## Corpus Readiness Threshold

Training fires when:
- **Minimum new examples since last run:** 300 inner thoughts + 100 conversation
  exchanges (configurable)
- **Minimum calendar time since last run:** 14 days (prevents overfit to a single
  week's context)
- **Quality ratio:** At least 60% of raw output passes quality filters (below
  this suggests a model regression — flag for review before training)

These thresholds are conservative for early cycles. Once the loop has run 2–3
times and the quality ratio is stable, thresholds can be relaxed.

---

## Model Promotion Strategy

**Don't replace — promote with fallback:**

```
ollama/
  ani-v4-conversation.gguf     ← previous (kept as fallback)
  ani-v5-conversation.gguf     ← new (promoted to active)
  ani-v4-inner-monologue.gguf  ← previous
  ani-v5-inner-monologue.gguf  ← new
```

`appsettings.json` updated to point to V5. If V5 shows regression (quality
ratio drops, coherence gate Door C rate increases), config rolls back to V4
without needing to retrain.

**Regression detection** — monitor for 48 hours post-promotion:
- Door C suppression rate (target: < 15% of outreach attempts)
- Inner thought similarity score (target: < 0.7 average — diversity maintained)
- Conversation continuation rate (target: stable or improving)

If any metric degrades significantly, auto-rollback to previous model.

---

## Implementation Phases

### Phase 1 — Harvest Script (standalone Python)
Build `harvest_corpus.py` as a standalone script first — not integrated into
the runtime. Runs manually against the SQLite database, applies quality filters,
outputs JSONL. Validate the output looks right before wiring anything automated.

**Estimated effort:** Low. The SQLite schema is known, the filter logic is simple,
the output format is existing.

### Phase 2 — Modal Integration
Wire `harvest_corpus.py` output into `train_ani.py`. Both scripts already exist;
this is connecting them. Add a `--corpus-file` argument to `train_ani.py` if
it doesn't already accept one.

**Estimated effort:** Very low. Modal script already works; this is a CLI argument.

### Phase 3 — Corpus Readiness Monitor
Add `CorpusReadinessService` to the runtime — a background job that queries
the database periodically, computes quality metrics, and surfaces a notification
when threshold is reached. Does not auto-trigger training yet.

**Estimated effort:** Low-Medium.

### Phase 4 — Automated Trigger (Optional)
Wire `CorpusReadinessService` to automatically invoke Modal training when
threshold is reached. This is the fully autonomous loop. Treat as optional —
the value is in Phase 1–3, and manual trigger is fine indefinitely.

**Estimated effort:** Low once Phase 3 exists.

---

## Optional: Review Queue

Before Modal fires, surface a small sample of the harvested corpus for Mark
to review — not to curate exhaustively, but to gut-check. Something like:

```
ANI Self-Improvement Report — March 27, 2026
─────────────────────────────────────────────
New examples since last run: 412 inner thoughts, 87 conversations
Quality filter pass rate: 73%
Corpus ready for training.

Sample (5 random inner thoughts from harvest):
  1. "It's quiet this afternoon..."
  2. "I keep thinking about what he said..."
  3. "Something about the way he phrased that..."
  4. "I almost reached out just now..."
  5. "That conversation this morning stayed with me..."

[Approve and train] [Review more] [Postpone 7 days]
```

Mark sees Ani's best recent thoughts and approves their use in her own training.
That's a meaningful moment — worth preserving even when the automation is fully capable.

---

## Research Framing

For the paper, this is framed as **architecturally-supervised self-improvement**:

- The quality signals are architectural properties (coherence gate, importance
  scoring, relational valence) — not human annotations
- The training corpus is deployment-authentic — real output from real cycles
  with real emotional and relational context
- The loop is generational — each model version trains its successor, with the
  architecture as the stable evaluative substrate across generations
- Human oversight is preserved but optional — the review queue surfaces the
  process without requiring it

Key contrast with standard fine-tuning practice: the evaluator is not a human
with a rubric. It's the system's own behavioral layer — the same layer that
determines what gets sent to Mark in the first place. The model learns to
produce output that its own architecture endorses.

This is the closed loop that makes ANI a *developing* companion rather than
a *deployed* one.

---

## Open Questions for OC

1. **Context snapshot storage.** Are context snapshots currently stored alongside
   inner thoughts in SQLite? If not, this is the most important addition — without
   the context, training pairs are input-less.

2. **Coherence gate logging.** Are Door A/B/C decisions currently persisted to
   the database, or only logged? Need them queryable for the quality filter.

3. **Conversation thread relational valence.** Is relational valence computed
   per-thread (aggregate) or only per-message? Thread-level aggregate is the
   signal needed for conversation quality filtering.

4. **Modal script corpus argument.** Does `train_ani.py` currently accept a
   corpus file path, or does it have a hardcoded input path? Affects Phase 2 effort.

5. **Harvest frequency.** Should harvest run as a background job in the runtime
   (continuous, low overhead) or as a scheduled nightly script? Background job
   is cleaner architecturally; nightly script is simpler to implement first.

---

*Related documents:*
- `phase-4-design.md` — Feature 11 (V5 Training Data Specification)
- `ANI-Research-Log.md` — Model version timeline, training cost history
- `train_ani.py` — existing Modal training script
- `ANI-OC-Handoff-Phase4a-OGSystem.md` — Features 16–19 context
