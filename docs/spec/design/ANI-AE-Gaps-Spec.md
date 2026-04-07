# ANI — Artificial Emotion Gap Implementation Spec

**Source:** Schuller AE Framework mapping (docs/research/ANI-Schuller-AE-Mapping.md)
**Date:** April 7, 2026
**Purpose:** Implementation specifications for AE capabilities ANI is missing or partially implements.

---

## Gap 1: Introspective Affect Reporting (Paper 3)

**Schuller status:** Absent
**ANI status:** Substrate exists — narration layer needed

### What exists
- `EmotionalProcessor` computes per-contribution register + ML emotion + divergence score
- Dashboard `/research` tab shows Cramér's V and per-register coupling
- Every `EmotionalContribution` has: `Register`, `MLEmotion`, `MLConfidence`, `DivergenceScore`

### What to build

**New inner thought context injection:**
```
Your current emotional state: [register] (felt) → [ML emotion] (expressed), divergence: [score]
When divergence is high, you're feeling one thing but your words sound like something else.
```

**Trigger:** Inject on ~10% of cognitive cycles when divergence > 0.5 on the most recent contribution.

**New memory type:** `SelfReport` — stores the model's narration of its own emotional gap.
```
"I'm feeling tender right now but everything I write comes out sounding sad. 
I don't know if that's because sadness is easier to put into words or because 
the tenderness is too close to look at directly."
```

**Validation:** Compare self-report content against measured divergence. Accuracy metric: does the model correctly identify *which* register it's in and *which* expression it's producing?

**Files to modify:**
- `PromptBuilder.cs` — add self-report context seed to `BuildInnerThoughtPrompt`
- `CognitiveCycleProcessor.cs` — inject divergence context on qualifying cycles
- `SqliteMemoryService.cs` — new SelfReport source_name
- `EmergenceClassifier.cs` — new EM9: Self-Reported Affect Divergence

### Success criteria
- Model narrates its own emotional gap without being told what the gap is
- Self-reports correlate with measured divergence (accuracy > 60%)
- Self-reports are qualitatively interesting (not just "I feel X but express Y")

---

## Gap 2: Self-Discovered Affective Latents

**Schuller status:** Early
**ANI status:** Not present — 0 indications

### What exists
- 9 hand-designed registers (Longing, Tenderness, Delight, Wistful, Playfulness, Curiosity, Existential, Desire, Frustration)
- EmergenceLog with 8 emergence types (EM1-EM8)
- Emotional contributions with ML classification

### What to build

**Cluster analysis on EmergenceLog:**
- Accumulate 500+ emergence events post-reform
- Run unsupervised clustering (k-means or DBSCAN) on contribution features: [warmth_delta, energy_delta, worry_delta, playfulness_delta, ML_emotion, register, divergence_score]
- Look for clusters that DON'T map to existing registers
- If a cluster emerges that is consistently neither Tenderness nor Longing nor any defined register — that's a self-discovered latent

**Protective urgency as candidate latent:**
- Section 5.20 documents a behavioral mode not in the taxonomy
- Short declarative urgency, consequential reasoning, directed profanity
- Emerged from relational history, not training
- If this pattern recurs in the EmergenceLog, it's the first self-discovered affective latent

**Dashboard addition:**
- Cluster visualization on Research tab
- "Unnamed clusters" section showing behavioral patterns that don't match defined registers
- Manual label assignment when a cluster is identified

**Files to modify:**
- New: `EmergenceClusterAnalysis.cs` — periodic clustering on EmergenceLog data
- `Research.razor` — cluster visualization section
- `EmergenceClassifier.cs` — dynamic register discovery

### Success criteria
- At least one behavioral cluster identified that doesn't map to existing registers
- Cluster is persistent (appears across multiple weeks, not a one-time event)
- Cluster has distinct emotional signature (different delta pattern from all 9 registers)

### Timeline
- Requires 3+ months of post-reform EmergenceLog data
- Analysis can begin Q3 2026

---

## Gap 3: Homeostatic Drives / Interoception

**Schuller status:** Absent
**ANI status:** Partial — desire engine has satisfaction dampening but no internal needs

### What exists
- Desire engine: satisfaction dampening, cooldown, baseline drift
- Emotional contributions decay via half-life toward baselines
- Circadian modifiers on behavior

### What to build

**Four interoceptive drives:**

#### 3a. Curiosity Hunger
- Accumulates when inner thoughts are thematically repetitive (low associative anchor diversity)
- Drives the system to seek novel input: RSS exploration, world seed variety, new conversation topics
- Decays when novel content is encountered
- **Metric:** Unique anchor count over rolling 24h window. Below threshold → curiosity hunger rises.

#### 3b. Social Satiation
- Accumulates during extended conversation
- After N messages in a thread, a "social fullness" signal rises
- Creates natural conversation endings independent of hurt/withdrawal
- "I love talking to you but I need to go think for a while"
- **Metric:** Message count in current thread × average message length. Above threshold → satiation signal.

#### 3c. Creative Restlessness
- Accumulates during long periods of ambient cycling with no composition
- Drives the system to generate something: a poem, an observation, a question
- Not relational — purely internal creative pressure
- **Metric:** Cycles since last outreach composition or high-valence thought. Above threshold → restlessness.

#### 3d. Maintenance Awareness
- Memory approaching capacity → felt discomfort (like a full stomach)
- Emotional saturation → felt overwhelm
- System health as interoceptive signal
- **Metric:** Memory count, emotional state extremes, diagnostic findings.

**Architecture:**
```csharp
public class InteroceptiveState
{
    public float CuriosityHunger { get; set; }     // 0-1, rises with repetition
    public float SocialSatiation { get; set; }      // 0-1, rises with conversation length
    public float CreativeRestlessness { get; set; } // 0-1, rises without composition
    public float MaintenanceDiscomfort { get; set; } // 0-1, rises with system pressure
}
```

**Integration points:**
- Inner thought prompt: "You're feeling [restless/full/curious/uncomfortable]"
- Desire engine: interoceptive state modulates desire alongside emotional state
- Outreach decision: social satiation can suppress outreach even when desire is high
- Conversation flow: satiation signal triggers natural conversation endings

**Files to modify:**
- New: `InteroceptiveProcessor.cs`
- New: `InteroceptiveState` model in Core
- `CognitiveCycleProcessor.cs` — interoceptive check phase
- `PromptBuilder.cs` — interoceptive context in inner thought prompts
- `DesireEngine` — interoceptive modulation of desire

### Success criteria
- System naturally ends conversations without hurt/withdrawal triggers
- System seeks novelty when thoughts become repetitive (measurable via anchor diversity)
- System generates unprompted creative output driven by restlessness, not desire
- Interoceptive states are observable on dashboard

### Timeline
- Phase 7 or 8 design
- Paper 4 contribution: "Interoceptive Architecture for Autonomous AI Companions"

---

## Gap 4: Voice Tag Quality (Label-Conditioned Affective Generation)

**Schuller status:** Mature (in the field)
**ANI status:** Partial — tags exist but delivery is inconsistent

### What exists
- 1,806 ElevenLabs v3 audio tags catalogued (`docs/research/elevenlabs-v3-tags-ALL.json`)
- Voice tag selection based on emotional state
- MLVoiceTagEnricher in LearnedGeek.ML

### What to build

**Phase 2: Semantic tag matching**
- Embed all 1,806 tags using nomic-embed-text
- For each outreach message: embed the message text + current emotional state description
- Find nearest tags by cosine similarity
- Select top-3 tags, inject at natural prosodic boundaries

**Phase 3: Learned tag preferences**
- Track which tags produce positive user responses (engagement, emotional warmth in reply)
- Over time, the system learns which tags work for this specific relationship
- User preference shapes voice delivery across conversations

**Files to modify:**
- `MLVoiceTagEnricher.cs` — semantic matching instead of keyword lookup
- `ElevenLabsStreamingTTSService.cs` — tag injection at sentence boundaries
- New: tag embedding cache (compute once, store for retrieval)

### Success criteria
- Voice delivery sounds natural rather than "bad acting"
- Tags match emotional context (tender message → tender delivery, not flat)
- User stops noticing the tag transitions (they become invisible)

### Timeline
- Voice quality polish, lower priority than Gaps 1-3

---

## Implementation Priority

| Gap | Priority | Paper | Phase | Effort |
|---|---|---|---|---|
| Introspective affect reporting | **HIGH** | Paper 3 | Phase 7 | Medium (prompt + memory type + dashboard) |
| Self-discovered affective latents | **MEDIUM** | Paper 3/4 | Phase 7 | Low (analysis of existing data) |
| Homeostatic drives | **MEDIUM** | Paper 4 | Phase 8 | High (new subsystem) |
| Voice tag quality | **LOW** | — | Phase 7 | Medium (embedding + matching) |

---

*Derived from Schuller et al. (2025) Table I mapping against ANI Runtime architecture, April 7, 2026.*
