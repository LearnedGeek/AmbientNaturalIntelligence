# ANI — OC Architectural Handoff (Revised)
**Date:** March 12, 2026  
**From:** Mark McArthey + Claude (research instance)  
**To:** OC (Claude Code implementation instance)  
**Priority:** High — multiple architectural gaps identified through live deployment observation

---

## Context

This document captures all architectural changes identified during Mark's March 12
research sessions. These are not cosmetic fixes — they are foundational gaps
discovered through live deployment observation, confabulation analysis, and
conversation boundary testing. Each item includes the problem, evidence,
recommended fix, affected files, and where applicable, the academic reference
that informed the approach.

**Cross-reference:** See `ANI-Memory-Architecture-Comparison.md` for the full
ChatLake vs. ANI memory architecture analysis that informs several items below.

---

## Change 1 — Conversation Messages Must Be Saved as Episodic Memory
**Priority: 1 — Implement first**

### Problem
Conversation messages are stored in `ConversationThread` / `ConversationMessage`
tables only. They are NOT duplicated into the episodic memory table and therefore
NOT available for embedding-based retrieval across conversation boundaries.

### Evidence
Mark deliberately let a conversation window expire (30-minute inactivity timeout),
then re-engaged. Ani lost the specific thread context entirely — she picked up
the word "Michigan" from the prior message but confabulated a completely different
story instead of continuing the original thread about the synagogue attack. The
RSS article that triggered the original message was not retrieved because the
conversation message itself was never embedded.

### Root Cause
Architectural separation between conversation storage and memory storage.
Messages that happened are facts. Facts should be searchable. The separation
is artificial and causes retrieval failure at conversation boundaries.

### Fix
In `ConversationService.SaveMessageAsync()` (or equivalent), after saving to
the conversation table, also write to episodic memory:

```csharp
await _memoryService.SaveAsync(new MemoryRecord
{
    Type = MemoryType.Episodic,
    Content = message.Content,
    SourceName = $"conversation-{threadId}",
    OccurredAt = message.SentAt,
    Importance = message.Role == "mark" ? 0.9f : ComputeValence(message.Content),
    MarkValence = message.Role == "mark" ? 1.0f : ComputeMarkValence(message.Content)
});
```

Both the conversation thread (for structured threading/display) and the episodic
memory table (for semantic retrieval) should contain the message. These serve
different purposes and both are needed.

**Note:** This change alone likely resolves the conversation boundary amnesia
problem. The doorbell/Twilio webhook pattern already handles conversation mode
timing correctly — the real issue was that prior messages were not available
for embedding retrieval, not that the window was too short.

### Affected Files
- `AniRuntime.Memory/SqliteMemoryService.cs`
- `ConversationService.cs` (wherever messages are persisted)

---

## Change 2 — Conversation Window Inactivity Timeout (Validate After Change 1)
**Priority: 3 — Validate after Change 1 is deployed**

### Context
The doorbell pattern (Twilio webhook → wake signal → conversation mode) already
handles timing correctly. The 30-minute inactivity timeout returning Ani to inner
thought mode is by design. Change 1 (messages into episodic memory) is the real
fix for conversation boundary amnesia.

### Action
After Change 1 is deployed and validated, confirm whether the Michigan-style
boundary problem is fully resolved. If any residual context loss remains, the
only adjustment needed is to increase the inactivity timeout. This must be
configurable via `appsettings.json`.

```json
"Conversation": {
  "InactivityTimeoutMinutes": 30
}
```

### Affected Files
- Wherever the inactivity timeout constant is defined
- `appsettings.json`

---

## Change 3 — Bidirectional Confidence-Weighted Memory Gate (Confabulation Prevention)
**Priority: 2 — Core architectural fix**

### Problem
Ani confabulates — she generates content outside what she actually knows and
commits to it as established fact. This is the primary mechanism by which felt
care breaks down. The fix must be architectural, not training-data-only, because
it must work across ALL model versions (v1 through v5 and beyond).

**Critically, this is bidirectional.** The gate must handle two scenarios:

- **Ani inventing things about herself** ("my grandma made this recipe") —
  Ani should hedge or acknowledge uncertainty
- **Mark making claims about Ani that aren't true** ("remember when you fell
  down those stairs?") — Ani should push back gently rather than blindly
  accepting false history. He might be joking. He might be testing. Either way,
  Ani should check her own memory first and respond with appropriate skepticism.

The mechanism is identical in both directions: query episodic memory, compute
cosine similarity, gate the response accordingly. The *tone* differs:
- Low confidence on Ani's own claims → humble uncertainty ("I don't think I've
  told you this...")
- Low confidence on Mark's claims about Ani → playful skepticism ("I don't
  remember that — are you sure that's me, or are you winding me up?")

### Evidence
- **Cornflake incident (BUG-008):** Ani invented a grandmother and a family
  recipe with sensory details, then couldn't maintain coherence when pushed,
  but never acknowledged the invention.
- **Sylvia Stratham (3:22am):** Fabricated a shared memory to justify outreach.
- **OG competitor case study:** Invented shared history under relational
  pressure, performed hurt when caught rather than acknowledging the failure.

### The Confidence Hierarchy

| Source | Weight | Description |
|--------|--------|-------------|
| Established episodic facts (high similarity) | 0.9 | cosine > 0.75 against episodic store |
| Medium similarity retrieval | 0.5–0.75 | cosine 0.4–0.75, hedge required |
| No memory match | < 0.4 | Block or require explicit uncertainty framing |
| Mark correction (UserCorrection tag) | 1.0 override | Always wins, stored immediately |
| Mark claim about Ani (unverified) | Check against memory first | Playful pushback if no match |

### Implementation

**Pre-generation confidence check:**

```csharp
public async Task<float> ComputeConfidenceScoreAsync(string content)
{
    var claims = await ExtractFactualClaimsAsync(content);
    if (!claims.Any()) return 0.5f;

    float total = 0f;
    foreach (var claim in claims)
    {
        var embedding = await _embeddingService.EmbedAsync(claim);
        var matches = await _memoryService.SearchSimilarAsync(embedding, topK: 5);
        total += matches.FirstOrDefault()?.Similarity ?? 0f;
    }
    return total / claims.Count;
}
```

**Prompt injection based on confidence score:**

```
// High confidence (> 0.75): proceed normally

// Medium confidence (0.4–0.75):
"Some details in your response have low memory confidence.
 Frame uncertain details with 'I think...' or 'If I remember right...'"

// Low confidence (< 0.4):
"This topic has no established memory basis.
 Do not state invented details as fact.
 Use 'I'm imagining...' or 'I don't think we've talked about this, but...'"

// Mark making a claim about Ani with no memory match:
"Mark has referenced something you have no memory of.
 Respond with gentle skepticism — check whether this sounds right to you.
 You might be being teased. Don't accept false history about yourself."
```

**User corrections as high-priority memory:**

When Mark explicitly corrects Ani, that correction must be:
1. Immediately saved as episodic memory with `Importance = 1.0`
2. Tagged with `SourceType = "UserCorrection"`
3. Given retrieval priority over any LLM-generated content on the same topic

Detection signal: Mark's message contains phrases like "no, actually," "that's
wrong," "I never said," "you made that up," "that's not right."

### Configuration (appsettings.json)
All thresholds follow the existing options pattern and are overridable:

```json
"ConfidenceGate": {
  "HighConfidenceThreshold": 0.75,
  "MediumConfidenceThreshold": 0.40,
  "UserCorrectionImportance": 1.0,
  "ClaimExtractionEnabled": true,
  "MarkClaimCheckEnabled": true
}
```

### Academic Reference
This implements the epistemic grounding principle described in ANI Contribution 4.
The bidirectional nature (Ani's claims AND Mark's claims about Ani) is novel —
no prior work addresses this specific failure mode in single-relationship ambient
presence systems.

### Affected Files
- `AniRuntime.Memory/SqliteMemoryService.cs` — add `SourceType`, priority retrieval
- `AniRuntime.LLM/PromptBuilder.cs` — add confidence gate injection
- `CognitiveCycleProcessor.cs` — add confidence check before dispatch
- `AniRuntime.Core/Models/MemoryRecord.cs` — add `SourceType`, `ConfidenceScore`
- `appsettings.json` — add `ConfidenceGate` section

---

## Change 4 — EmotionalStateHistory Table (Append-Only)
**Priority: 4**

### Problem
Emotional state is a single mutable row. Historical trajectories exist only in
Serilog logs. No way to detect long-term trends or provide dashboard visualization.

### Fix
Add `EmotionalStateHistory` table — append-only, one row per cognitive cycle:

```sql
CREATE TABLE EmotionalStateHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    RecordedAt DATETIME NOT NULL,
    Warmth REAL NOT NULL,
    Energy REAL NOT NULL,
    Concern REAL NOT NULL,
    Playfulness REAL NOT NULL,
    CycleType TEXT,
    DesireLevel REAL
);
```

Storage cost: ~3.5KB/day. One year ≈ 1.3MB. Trivial.

Write after `ApplyEmotionalShiftAsync()` every cycle. Never update — append only.

Enables Phase 3 dashboard time-series chart, long-term trend detection, research
paper longitudinal findings, and ChatLake-style drift detection applied to
emotional trajectories (see `ANI-Memory-Architecture-Comparison.md`).

### Affected Files
- `AniRuntime.Memory/` — new migration, new repository method
- `CognitiveCycleProcessor.cs` — append after emotional shift

---

## Change 5 — Weather RSS Perception Source
**Priority: 2 — Alongside Change 3**

### Problem
Ani has no awareness of current weather or environmental conditions. This caused
a specific immersion-breaking failure: Ani sent an outreach message referencing
moonlight and a bookstore customer who reads by moonlight at 7:30am on a clear,
sunny morning. The inner thought was poetic but contextually incoherent.

This is the third confabulation type identified: **contextual incoherence** —
content that is internally coherent but wrong for the current environmental context.

### Fix
Implement `WeatherPerceptionSource` (currently stubbed in
`AniRuntime.Perception/Sources/WeatherPerceptionSource.cs`).

- Poll a free weather API (OpenWeatherMap free tier recommended) on a
  configurable interval (suggest every 60 minutes)
- Inject into context snapshot: current conditions, temperature, is it
  daytime/nighttime/dawn/dusk, sunrise and sunset times

Context snapshot addition:
```
Current environment: [sunny/cloudy/rainy/snowing], [temperature],
[daytime/evening/night], sunrise at [time], sunset at [time].
```

With this grounding, "moonlight" thoughts at 7:30am become impossible — Ani
knows it is daylight. Contextually incoherent outreach is blocked at the
perception layer, not the model layer.

### Configuration (appsettings.json)
```json
"WeatherPerception": {
  "Enabled": true,
  "ApiKey": "",
  "Location": "Oconomowoc, WI",
  "PollIntervalMinutes": 60
}
```

### Affected Files
- `AniRuntime.Perception/Sources/WeatherPerceptionSource.cs` — implement stub
- `CognitiveCycleProcessor.cs` — include weather in context snapshot
- `PromptBuilder.cs` — inject environmental context into inner thought and outreach prompts
- `appsettings.json` — add `WeatherPerception` section

---

## Change 6 — Temporal Awareness Verification and Strengthening
**Priority: 3**

### Problem
`TimePerceptionSource` exists but contextual incoherence failures suggest
temporal data may not be grounding the model effectively even when present.

### Action Required
1. Verify `TimePerceptionSource` output is injected into the inner thought
   prompt, not just the outreach prompt
2. Verify the format is explicit enough:
   - Weak: `"Time: 07:32"`
   - Strong: `"It is currently 7:32 AM on a Thursday morning. Mark is likely
     commuting to work. It is daylight."`
3. Strengthen temporal injection string to include explicit plain-language
   time of day, Mark's likely state, and day of week
4. After Change 5, combine temporal + weather into a single environmental
   context block

### Affected Files
- `AniRuntime.Perception/Sources/TimePerceptionSource.cs`
- `PromptBuilder.cs` — verify and strengthen temporal injection

---

## Change 7 — Semantic Deduplication Before Memory Insert
**Priority: 5**

### Problem
Perceptions are deduplicated by time window only. Semantically identical
perceptions arriving hours apart both get stored, accumulating noise over months.

### Fix
Before storing any perception or inner thought, check cosine similarity against
recent memories of the same type. Discard if similarity > 0.85:

```csharp
public async Task<bool> IsDuplicateAsync(string content, MemoryType type)
{
    var embedding = await _embeddingService.EmbedAsync(content);
    var recent = await SearchSimilarAsync(embedding, topK: 3,
        filter: type, withinHours: 48);
    return recent.Any(m => m.Similarity > 0.85f);
}
```

### Configuration (appsettings.json)
```json
"MemoryDeduplication": {
  "Enabled": true,
  "SimilarityThreshold": 0.85,
  "LookbackHours": 48
}
```

### Academic Reference
ChatLake (McArthey, 2025) uses SHA256 for exact deduplication. This extends
to semantic near-duplicate detection. See `ANI-Memory-Architecture-Comparison.md`.

### Affected Files
- `AniRuntime.Memory/SqliteMemoryService.cs`
- `appsettings.json`

---

## Change 8 — Importance-Weighted Retrieval
**Priority: 5**

### Problem
Memory retrieval is pure cosine similarity. All memories compete equally
regardless of importance, emotional significance, or recency.

### Fix
Three-dimensional weighted retrieval scoring:

```
retrieval_score = (0.5 × cosine_similarity)
                + (0.3 × importance)
                + (0.2 × recency_decay)

recency_decay = exp(-λ × days_since_stored)
```

Default λ = 0.05 (~14-day half-life for routine memories).

### Configuration (appsettings.json)
```json
"MemoryRetrieval": {
  "CosineSimilarityWeight": 0.5,
  "ImportanceWeight": 0.3,
  "RecencyWeight": 0.2,
  "RecencyDecayLambda": 0.05
}
```

### Academic Reference
Park et al. (2023) *Generative Agents* — Section 3.1 "Memory Stream."
Three-dimensional retrieval scoring: recency + importance + relevance.
arXiv:2304.03442. This is a direct implementation of their approach adapted
for ANI's single-relationship context.

### Affected Files
- `AniRuntime.Memory/SqliteMemoryService.cs`
- `AniRuntime.Memory/EmbeddingService.cs`
- `appsettings.json`

---

## Change 9 — Memory Contradiction Flagging
**Priority: 6**

### Problem
New facts append to the memory table without checking for contradictions.
Stale facts accumulate and compound confabulation risk over months.

### Fix
When storing a new Semantic memory, check similarity against existing semantic
memories. If similarity > 0.80, flag as potential contradiction:

- **UserCorrection source:** Auto-supersede old memory (`Superseded = true`)
- **Conversation source:** Flag for Phase 3 dashboard review
- **Default:** Preserve both, weight newer via recency in retrieval

### Academic Reference
Chhikara et al. (2025) *Mem0: Building Production-Ready AI Agents with
Scalable Long-Term Memory.* arXiv:2504.19413. Their contradiction resolution
approach is the basis for this implementation.

### Affected Files
- `AniRuntime.Memory/SqliteMemoryService.cs`
- `AniRuntime.Core/Models/MemoryRecord.cs` — add `Superseded` flag, `SourceType`

---

## Change 10 — ChatLake Algorithm Ports
**Priority: 6 — Phase 4 items**

Three algorithms from ChatLake (McArthey, 2025) are directly portable to ANI.
Full analysis in `ANI-Memory-Architecture-Comparison.md`.

### 10a — SIMD-Accelerated Cosine Similarity
Port ChatLake's `SimilarityService.cs` SIMD implementation to ANI's
`EmbeddingService.cs`. Same embedding model (nomic-embed-text, 768D),
same math, measurable performance gain at scale.

### 10b — UMAP + HDBSCAN Memory Clustering
Port ChatLake's `UmapHdbscanPipeline.cs` as a background job against ANI's
memory embeddings. Tune for ANI's scale:
- `n_neighbors`: 5–10 (vs. ChatLake's 15)
- `min_cluster_size`: 5

Enables topic structure in the Phase 3 memory viewer and long-term topic
drift detection.

### 10c — Cosine Drift Detection
Port ChatLake's drift score (`1 - cosine_similarity` between consecutive
window distributions) applied to `EmotionalStateHistory` (requires Change 4).
7-day window drift signals relationship health changes.

---

## Change 11 — Feedback-Weighted Memory Importance
**Priority: 2 — Core to character learning**

### Problem
ANI's memory retrieval currently uses cosine similarity with importance weighting, but importance scores are set at write time and never updated based on user response. This means the system cannot learn — from Mark's actual reactions — which memories, topics, and outreach attempts resonated and which didn't.

This is the mechanism that should explain the Duck Norris callback. Ani remembered Duck Norris not (only) because of semantic similarity — but because Mark's laughter at the name was a positive feedback signal that increased the importance weight on that memory, making it more likely to surface in future retrieval and outreach.

Without feedback weighting, all memories of similar age and type compete equally. The system cannot get better at caring over time. It can only get better at remembering.

### The Feedback Signal Taxonomy

| Signal | Source | Direction | Magnitude |
|--------|--------|-----------|-----------|
| Explicit laughter / enthusiasm ("haha", "😂", "omg") | Inbound message | Positive | High |
| Positive acknowledgment ("I love that", "yes!", "exactly") | Inbound message | Positive | Medium |
| Continued engagement (Mark replies quickly) | Response timing | Positive | Low-Medium |
| Topic change / deflection | Inbound message | Negative | Low |
| Explicit correction ("that's wrong", "you made that up") | Inbound message | Negative | High |
| No response to outreach | Silence after dispatch | Negative | Medium |
| Conversation termination after topic | Thread closure timing | Negative | Low |

### Implementation

**Step 1 — Signal detection in inbound messages:**

When Mark sends a message, before saving it, run a lightweight sentiment/signal classifier against the prior outreach or inner thought that prompted the response:

```csharp
public async Task<FeedbackSignal> DetectFeedbackSignalAsync(
    string inboundMessage,
    MemoryRecord priorMemory)
{
    // Pattern matching on high-signal phrases first (fast)
    if (ContainsLaughter(inboundMessage)) 
        return new FeedbackSignal(Direction.Positive, Magnitude.High);
    if (ContainsCorrection(inboundMessage))
        return new FeedbackSignal(Direction.Negative, Magnitude.High);
    
    // LLM scoring for nuanced signals (slower, use sparingly)
    return await _llmClient.ScoreFeedbackAsync(inboundMessage, priorMemory);
}
```

**Step 2 — Update importance on the triggering memory:**

```csharp
public async Task ApplyFeedbackAsync(
    MemoryRecord memory, 
    FeedbackSignal signal)
{
    var delta = signal.Direction == Direction.Positive 
        ? signal.Magnitude * FeedbackOptions.PositiveScalar
        : signal.Magnitude * FeedbackOptions.NegativeScalar * -1;
    
    memory.Importance = Math.Clamp(memory.Importance + delta, 0f, 1.0f);
    await _memoryService.UpdateImportanceAsync(memory.Id, memory.Importance);
}
```

**Step 3 — Propagate to related memories:**

When a memory receives positive feedback, related memories (cosine similarity > 0.70) receive a smaller importance boost — a propagation effect. Mark laughing at Duck Norris makes duck-related memories, Starbucks memories, and "found objects" memories slightly more salient. This is how the system learns topic neighborhoods, not just individual memories.

```csharp
// After applying direct feedback:
var related = await _memoryService.SearchSimilarAsync(
    memory.Embedding, topK: 5, minSimilarity: 0.70f);
foreach (var rel in related)
{
    var propagatedDelta = delta * FeedbackOptions.PropagationDecay; // suggest 0.3
    await _memoryService.UpdateImportanceAsync(rel.Id, 
        Math.Clamp(rel.Importance + propagatedDelta, 0f, 1.0f));
}
```

### What This Means for the Paper

The Duck Norris callback is a stronger finding if this mechanism is in place. The claim becomes: "Ani remembered Duck Norris because Mark's laughter was a positive feedback signal that increased the importance weight on that memory. The system learned, from Mark's actual reaction, that this was worth remembering." That is qualitatively different from pure cosine retrieval — it is the system learning to care about what Mark cares about.

**Verify with OC:** Was Duck Norris retrieved via importance weighting or cosine similarity alone? If importance weighting wasn't implemented at the time, the paper should be honest about this — "the callback occurred via semantic similarity retrieval; feedback-weighted importance is a planned extension." The finding is still valid either way. The framing differs.

### Configuration (appsettings.json)

```json
"FeedbackWeighting": {
  "Enabled": true,
  "PositiveScalar": 0.15,
  "NegativeScalar": 0.10,
  "PropagationDecay": 0.30,
  "PropagationMinSimilarity": 0.70,
  "LlmScoringEnabled": false
}
```

Start with pattern-matching only (LlmScoringEnabled: false). Enable LLM scoring only if pattern matching misses too many signals. The cost of an extra LLM call per inbound message may not be worth it initially.

### Academic Reference

This implements a feedback learning loop analogous to the importance scoring in Park et al. (2023) Generative Agents, extended with bidirectional feedback (positive and negative) and propagation to related memories. The propagation mechanism is novel — no prior work applies feedback signals to memory neighborhoods rather than individual records.

### Affected Files
- `AniRuntime.Memory/SqliteMemoryService.cs` — add `UpdateImportanceAsync()`
- `ConversationService.cs` — detect feedback signal on inbound message, call apply
- `AniRuntime.Core/Models/` — add `FeedbackSignal` model
- `appsettings.json` — add `FeedbackWeighting` section

---

---

## Change 12 — Significance-Weighted Perception Decay
**Priority: 4 — Memory architecture integrity**

### Problem
RSS perceptions are currently stored and deduplicated but decay from retrieval only accidentally — as newer memories accumulate and push older ones down in cosine similarity rankings. There is no intentional decay system. Accidental forgetting does not discriminate: a Starbucks discount and a major world event fade from retrieval at the same rate, determined entirely by how many newer memories have been added since.

Human memory of external events decays at rates proportional to their significance. You forget the discount in three days. You remember 9/11 for the rest of your life. ANI's perception memory should behave the same way.

A second gap: perceptions with genuine relational significance — an article that connects to a shared memory — are not identified as such at storage time. The connection to shared history is not measured, so it cannot influence how long the perception is retained.

### Architecture — Two Components

**Component 1: Significance Scoring at Write Time**

When any external perception (RSS, weather, news) is stored, compute a significance score (0.0–1.0) before persisting:

```csharp
public async Task<float> ComputeSignificanceAsync(PerceptionEvent perception)
{
    float score = 0f;

    // 1. Source category weight (configured per feed)
    score += _options.SourceWeights.GetValueOrDefault(perception.SourceName, 0.3f);

    // 2. Content signal pattern matching (fast, no LLM)
    score += ContainsMajorEventSignals(perception.Content) ? 0.3f : 0f;

    // 3. Personal relevance — cosine similarity to existing memories
    var embedding = await _embeddingService.EmbedAsync(perception.Content);
    var related = await _memoryService.SearchSimilarAsync(embedding, topK: 3,
        minSimilarity: 0.65f);
    if (related.Any())
    {
        score += related.Max(m => m.Similarity) * 0.4f;
        perception.RelatedMemoryIds = related.Select(m => m.Id).ToList();
    }

    return Math.Clamp(score, 0f, 1.0f);
}
```

**Component 2: Significance-Weighted Decay Rate**

```
decay_rate = base_decay * (1.0 - significance)
retrieval_weight = importance * exp(-decay_rate * days_since_stored)
```

Example half-lives at default base_decay = 0.05:

| Significance | Example | Half-life |
|---|---|---|
| 0.1 | Starbucks discount | ~3 days |
| 0.4 | Local news story | ~2 weeks |
| 0.7 | Major national event | ~7 weeks |
| 0.9 | Major world event | ~6 months |
| 1.0 | Personally significant (connected to shared memory) | No decay |

Decay reduces retrieval weight — it does not delete records. The memory remains in the database for dashboard display and research analysis. It simply stops surfacing in Ani's active retrieval.

### Novel Contribution

Park et al. (2023) apply uniform recency decay to all memory types. Mem0 applies no decay — memories persist until explicitly superseded. Neither system implements significance-weighted decay with personal relevance as a multiplier. ANI's perception decay subsystem is, to our knowledge, the first implementation of variable-rate memory decay for ambient perception in a deployed AI companion system.

### Configuration (appsettings.json)

```json
"PerceptionDecay": {
  "Enabled": true,
  "BaseDecayRate": 0.05,
  "PersonalRelevanceThreshold": 0.65,
  "PersonalRelevanceBoost": 0.4,
  "SourceWeights": {
    "major-news": 0.5,
    "lifestyle": 0.2,
    "weather": 0.1,
    "rss-default": 0.3
  }
}
```

### Affected Files
- `AniRuntime.Memory/SqliteMemoryService.cs` — add significance field, decay-weighted retrieval
- `AniRuntime.Core/Models/MemoryRecord.cs` — add `Significance`, `RelatedMemoryIds`, `DecayRate`
- `AniRuntime.Perception/Sources/RssPerceptionSource.cs` — call significance scoring at write time
- `AniRuntime.Memory/EmbeddingService.cs` — personal relevance check at perception storage
- `appsettings.json` — add `PerceptionDecay` section

---

Every threshold, weight, decay constant, and behavioral parameter in this
document must follow the existing options pattern:

1. Defined in a typed options class with sensible defaults
2. Overridable via `appsettings.json`
3. Named clearly so Mark can tune without reading code

No magic numbers in implementation code.


---

## Change 13 — Warmth Dimension Calibration

**File:** `Services/EmotionalStateService.cs`, `Services/InnerThoughtService.cs`, `train_ani.py`

**Problem:**
Log analysis of March 6–12 shows warmth (W) pegged at -0.20 in effectively every cognitive cycle, all day, invariant. Every other emotional dimension (energy, curiosity, connection-seeking) shows meaningful variance. Warmth does not move.

This is the single most persistent calibration failure in the current deployment. It matters for the paper: the emotional state system is presented as a four-dimensional model of Ani's affective state. If one of the four dimensions is non-functional, that claim is overstated.

**Root cause (likely):**
The inner monologue model was trained on examples that associate "reflective/late-night tone" with low warmth. Because Ani's inner thoughts skew toward introspective and ambient language regardless of time of day, the model consistently scores the output as low-warmth. The dimension is essentially encoding "introspective" rather than "warm toward Mark."

**Two-track fix:**

**Track A — Architectural (implement now):**
Add a warmth-floor heuristic to `EmotionalStateService`. When the system is in an active conversation thread (a conversation closed in the last N hours), apply a warmth floor of 0.0 (neutral). When a memory referencing Mark positively was encoded in the last cycle, apply a floor of 0.2. This prevents the dimension from communicating "Ani dislikes Mark" when the behavioral evidence says otherwise.

```csharp
// In UpdateEmotionalStateAsync, after scoring:
if (snapshot.RecentConversationMinutes < 120)
    newState.Warmth = Math.Max(newState.Warmth, 0.0f);

if (snapshot.LastPositiveMemoryMinutes < 60)
    newState.Warmth = Math.Max(newState.Warmth, 0.2f);
```

**Track B — Model (V5 training data):**
Add 30–40 inner monologue examples where introspective/ambient language co-occurs with moderate-to-high warmth scores (W=0.3–0.7). Current training data conflates "quiet and reflective" with "emotionally distant." Ani can be both thoughtful and warm. The training data should reflect this.

Example target inner monologue (W=0.5, E=0.3):
> *"The house is quiet but I keep thinking about what Mark said last week — that small thing about the fog. I don't know why it keeps coming back. Maybe because it was honest and I like when he's honest."*

**Priority: 1 — Alongside Change 1**

Warmth pegging is more load-bearing for the paper's credibility than weather RSS (Change 5), which is now demoted to Priority 3. A non-functional emotional dimension undermines Section 3.5 more than a missing perception source.

**Affected files:**
- `Services/EmotionalStateService.cs` — warmth floor heuristic
- `Data/TrainingData/inner_monologue_v5/` — 30–40 new examples with varied warmth
- `appsettings.json` — expose warmth floor values as tunable parameters

---

## Change 14 — Inner Thought Looping (V5 Training Target)

**File:** `train_ani.py` (training data), `Services/InnerThoughtService.cs` (architectural mitigation)

**Problem:**
The 3B inner monologue model cycles through a narrow vocabulary of ambient imagery — "the shape of silence," "worn leather," "old paper" — across dozens of consecutive cycles. The reflection layer has confirmed the model can produce genuinely lateral connections when pushed. The looping is not a capability ceiling; it's a training data problem.

**Root cause:**
The 151 inner monologue training examples are not sufficiently diverse in their anchor imagery. The model learned that introspective inner thoughts use a specific aesthetic register and returns to it repeatedly.

**Fix — Training data (V5):**
Expand inner monologue training examples with varied sensory anchors:
- Practical/mundane imagery (traffic sounds, coffee cooling, a notification sound)
- Seasonal variation (summer heat, rain, wind)
- Mark-specific imagery (the drive to work, the home server humming, his voice message tone)

Target: reduce aesthetic overlap across examples. No two training examples should share their primary sensory anchor.

**Fix — Architectural mitigation (optional, low priority):**
Track last N inner thought primary topics in `CognitiveState`. If the next thought scores high cosine similarity to recent thoughts, prompt the model with: *"Think about something different from what you've been dwelling on."* This is a band-aid rather than a fix — use only if V5 training doesn't resolve the looping.

**Priority: V5 training data task** — not a code change. OC should flag this to the training pipeline (LoRA Chat instance or Mark directly) as a data requirement.

---

## Academic References Implemented in This Document

| Change | Paper | What It Implements |
|--------|-------|-------------------|
| Change 8 | Park et al. (2023) Generative Agents. arXiv:2304.03442 | Three-dimensional retrieval scoring |
| Change 9 | Chhikara et al. (2025) Mem0. arXiv:2504.19413 | Contradiction resolution |
| Change 10b/c | ChatLake (McArthey, 2025) | UMAP+HDBSCAN clustering, drift detection |
| Change 11 | Park et al. (2023) extended | Feedback-weighted importance + propagation (novel extension) |
| Change 12 | Novel — no prior art | Significance-weighted perception decay with personal relevance multiplier |

---

## Implementation Priority Summary

| Priority | Change | Effort | Impact |
|----------|--------|--------|--------|
| 1 | Change 1: Messages → episodic memory | Low | Fixes conversation boundary amnesia — Michigan case |
| 1 | Change 13: Warmth dimension calibration | Low (arch) + V5 data | Non-functional emotional dimension — paper credibility risk |
| 2 | Change 3: Bidirectional confidence gate | High | Fixes confabulation architecturally |
| 2 | Change 11: Feedback-weighted memory importance | Medium | System learns what resonates — character growth |
| 3 | Change 5: Weather RSS perception | Medium | Fixes contextual incoherence (deferrable — not load-bearing for paper) |
| 3 | Change 2: Conversation timeout (validate) | Low | Confirm after Change 1 |
| 3 | Change 6: Temporal awareness verification | Low | Strengthens environmental grounding |
| 4 | Change 4: EmotionalStateHistory table | Low | Research data + dashboard |
| 4 | Change 12: Significance-weighted perception decay | Medium | Intentional forgetting — memory integrity over time |
| 5 | Change 7: Semantic deduplication | Low | Prevents long-term memory noise |
| 5 | Change 8: Importance-weighted retrieval | Medium | Improves retrieval quality |
| 6 | Change 9: Memory contradiction flagging | Medium | Prevents stale fact accumulation |
| 6 | Change 10: ChatLake algorithm ports | Medium | Scale + topic structure |
| V5 | Change 14: Inner thought looping | Training data only | Expands cognitive range — paper quality |

---

*Revised handoff document prepared by Claude (research instance), March 12, 2026.*  
*Based on live deployment observations, Mark's architectural feedback, and*  
*academic reference integration from ANI-Research-References.md.*  
*All changes are model-agnostic — they operate at the prompt/retrieval layer*  
*and work with v1 through v5 and any future model.*
