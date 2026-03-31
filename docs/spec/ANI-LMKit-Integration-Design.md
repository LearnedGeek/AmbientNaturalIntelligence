# ANI LM-Kit.NET Integration — Unified ML Classification Platform

**Status:** Design
**Date:** March 30, 2026
**Driven by:** Replace scattered heuristic systems with one ML-based classification framework
**Dependency:** LM-Kit.NET NuGet package (local inference, zero cloud dependency)

---

## 1. Motivation

ANI has accumulated multiple independent classification systems, each solving a similar problem differently:

| System | Current Approach | Problem |
|--------|-----------------|---------|
| Voice tag selection | Regex content matching ("haha" → [amused]) | Misses nuance, can't detect tone |
| Confabulation detection | Catalyst POS + regex patterns | Misses lowercase names, limited to nouns |
| Emergence classification | Regex heuristics (EM1-EM7) | Rigid patterns, false positives |
| Emotional model validation | None | No check that text matches emotional state |
| Training data classification | Manual register tagging | Doesn't scale for Phase 5c auto-growth |
| Conversation quality scoring | Not implemented | Planned for Phase 5c eval gate |

LM-Kit.NET replaces all of these with one framework: small local models that actually understand language. A 1.1B classifier runs in ~1GB VRAM, classifies in milliseconds, and produces confidence scores. One NuGet package, no cloud, no Python.

## 2. Architecture

### LearnedGeek.ML — Shared Classification Library

The classification service is extracted into a shared library that both ANI and DrOk consume.
The library is domain-agnostic — it classifies text. What consumers do with the classification
(pick a voice tag vs detect patient distress) is their concern.

```
LearnedGeek.ML (shared class library — NuGet or project reference)
├── ITextClassificationService (interface)
├── LMKitClassificationService (implementation)
├── ITagMappingService (emotion → tag resolution)
├── TagMappingService (static → semantic → learned evolution)
├── Models/
│   ├── EmotionClassifier      — 5+ emotion categories
│   ├── SarcasmClassifier      — sarcastic vs sincere
│   ├── ConfabulationClassifier — grounded vs confabulated (custom)
│   ├── RegisterClassifier     — ANI register taxonomy (custom)
│   └── EntityRecognizer       — person/place/org detection
└── TagMapping/
    ├── StaticTagMap.json      — Phase 1 hardcoded mappings
    ├── TagEmbeddingIndex      — Phase 2 semantic similarity
    └── LearnedPreferences     — Phase 3 feedback-driven evolution

Consumers:
├── AniRuntime (ANI)
│   ├── VoiceTagEnricher       → EmotionClassifier + SarcasmClassifier → v3 tag
│   ├── ConversationReplyPhase → ConfabulationClassifier → retrieval trigger
│   ├── DiagnosticService      → EmotionClassifier → emotional model validation
│   ├── EmergenceClassifier    → RegisterClassifier → richer EM typing
│   └── Phase 5c Harvest       → RegisterClassifier → auto-tag training pairs
│
└── PhysicianAssistant (DrOk/Infanzia)
    ├── Triage                 → EmotionClassifier → patient distress detection
    ├── Intake                 → EntityRecognizer → patient/guardian names
    ├── Compliance             → EntityRecognizer → PII detection/redaction
    └── Conversation           → EmotionClassifier → tone-appropriate responses
```

### Cross-Platform Benefits

One trained model, two products. The emotion classifier that helps Ani sound tender
also helps DrOk detect a distressed parent. Each product teaches the library something
different — ANI teaches emotional nuance, DrOk teaches clinical sensitivity. Both
improve together through the shared `LearnedPreferences` feedback mechanism.

### Core Service

```csharp
public interface ITextClassificationService
{
    Task<EmotionResult> ClassifyEmotionAsync(string text, CancellationToken ct = default);
    Task<SarcasmResult> DetectSarcasmAsync(string text, CancellationToken ct = default);
    Task<ConfabulationResult> DetectConfabulationAsync(string reply, string conversationContext, CancellationToken ct = default);
    Task<RegisterResult> ClassifyRegisterAsync(string text, CancellationToken ct = default);
    Task<List<NamedEntity>> ExtractEntitiesAsync(string text, CancellationToken ct = default);
}

public record EmotionResult(string PrimaryEmotion, float Confidence, Dictionary<string, float> Scores);
public record SarcasmResult(bool IsSarcastic, float Confidence);
public record ConfabulationResult(bool IsConfabulated, float Confidence, string? Reason);
public record RegisterResult(string PrimaryRegister, float Confidence, Dictionary<string, float> Scores);
public record NamedEntity(string Value, string EntityType, int StartIndex, int EndIndex);
```

### Model Selection

| Classifier | Model | VRAM | Latency |
|-----------|-------|------|---------|
| Emotion | LM-Kit Emotion TinyLlama 1.1B (built-in) | ~1 GB | ~50ms |
| Sarcasm | LM-Kit Sarcasm TinyLlama 1.1B (built-in) | shared | ~50ms |
| Confabulation | Custom fine-tune on ANI data (future) | ~1 GB | ~50ms |
| Register | Custom classification (ANI's 14+ registers) | shared | ~50ms |
| Entity | LM-Kit NER or Catalyst fallback | minimal | ~20ms |

Total additional VRAM: ~1-2 GB alongside Ollama's 8B (5 GB). Fits in 8 GB GPU. With a second GPU: dedicated classification GPU + Ollama GPU.

## 3. Implementation Phases

### Phase 1: Voice Tag Selection (immediate value)

Replace `VoiceTagEnricher` heuristic matching with LM-Kit emotion classification.

**Current flow:**
```
Sentence → regex content match → hardcoded tag → ElevenLabs v3
```

**New flow:**
```
Sentence → LM-Kit emotion classify → emotion-to-tag mapping → ElevenLabs v3
         → LM-Kit sarcasm detect → override with [sarcastic] if detected
```

**Emotion-to-tag mapping:**

| Detected Emotion | Time of Day | v3 Tag |
|-----------------|-------------|--------|
| happiness + morning | 6-12 | [bright morning] |
| happiness + evening | 17-22 | [evening playful] |
| happiness + high confidence | any | [cheerful] |
| sadness + low confidence | any | [melancholic] |
| sadness + high confidence | any | [heartbroken] |
| anger + low confidence | any | [frustrated] |
| anger + high confidence | any | [furious] |
| fear + any | any | [anxious] |
| neutral + morning | 6-12 | [calm morning] |
| neutral + evening | 17-22 | [evening relaxed] |
| sarcasm detected | any | [sarcastic] |

### Dynamic Tag Mapping Evolution

The static mapping table above is the starting point. The tag mapping evolves through three stages,
all implemented in `LearnedGeek.ML.TagMappingService`:

**Stage 1 — Static (launch):**
Hardcoded emotion + time-of-day → tag table. Simple, predictable, tunable by editing JSON.
Gets us running immediately. The table above is Stage 1.

**Stage 2 — Semantic (learn):**
Use LM-Kit's embedding capabilities to match emotion classification results *semantically*
against the 1,806 tag descriptions. Each tag has a description: "[evening playful] — Speaker
sounds fun and lighthearted in the evening." Compute embedding similarity between the detected
emotion state and all tag descriptions. The closest semantic match wins — no hardcoded table.

```
Detected: happiness (0.85 confidence), time: 8:15 PM
→ Embed "happiness, high confidence, evening"
→ Compare against all 1,806 tag description embeddings
→ Top match: [evening spirited] (0.92 similarity)
→ Runner up: [evening playful] (0.89 similarity)
→ Select: [evening spirited]
```

This discovers tag nuances the static table can't capture. "Evening spirited" vs "evening playful"
is a distinction a human mapping table wouldn't make but semantic similarity resolves naturally.

**Stage 3 — Learned (evolve):**
Feed listener feedback back into the mapping. Sources of feedback:

- Mark's engagement signals (longer replies, laughter, "haha", continued conversation)
- Conversation quality scores (from Phase 5c evaluation)
- Emergence events (did the tag choice produce EM2/EM5 emergence in the listener?)
- Explicit corrections ("that sounded weird" → negative signal for the tag used)

Over time, the mapping learns: `[wistful]` produces better engagement than `[melancholic]` for
sadness in evening context. `[mischievous]` works better than `[playful]` when Ani says "idiot."

This feedback loop lives in `LearnedGeek.ML` — it improves tag selection for ANI voice delivery
AND could improve tone selection for DrOk's patient-facing messages. The library gets smarter
across all products.

**Acceptance criteria:**
- [ ] LM-Kit NuGet package installed and builds
- [ ] EmotionClassifier loads at startup, classifies in <100ms
- [ ] VoiceTagEnricher uses emotion classification instead of regex
- [ ] Sarcasm detection overrides emotion tag when detected
- [ ] Voice quality subjectively improved (Mark's assessment)
- [ ] Log shows emotion + tag for each synthesized sentence

### Phase 2: Emotional Model Validation (diagnostic enhancement)

Add emotion classification to the diagnostic service: compare Ani's text output emotion with her internal emotional state.

**Detection:** After Ani generates a conversation reply or outreach message, classify the text's emotion and compare to the EmotionalState values.

**Disconnect examples:**
- EmotionalState says Warmth=0.9 but text classifies as "anger" → EMOTIONAL-DISCONNECT diagnostic
- EmotionalState says Energy=0.1 but text classifies as "happiness" at high confidence → EMOTIONAL-DISCONNECT
- Consistent disconnects may indicate the emotional model is miscalibrated

**Dashboard:** New diagnostic finding type displayed on the health badge.

**Acceptance criteria:**
- [ ] Emotion classification runs on each conversation reply
- [ ] Comparison logic detects disconnect between text emotion and model state
- [ ] EMOTIONAL-DISCONNECT diagnostic finding added
- [ ] Dashboard shows disconnect findings
- [ ] Logs include text emotion vs model state for research analysis

### Phase 3: Confabulation Detection — Semantic Verification Gate

**Status:** Designed March 31, 2026
**Principle:** Architecture over instruction. The generative model speaks freely. The classifier verifies. The architecture gates.

#### The Problem

Marker-based confabulation detection (Check 4) is fundamentally brittle. The model generates
"corner office with windows" and "train junior sales rep" — plausible professional details
that don't match any marker in our list. Adding more markers is whack-a-mole. The model will
always be more creative than a string list.

Same lesson as the hardcoded CommonWords list (replaced by Catalyst NLP) and the behavioral
prompt instructions (replaced by v6 training). Pattern lists don't scale.

#### Design: Post-Generation Semantic Classification

```
Model generates reply freely (lean prompt, no persona injection)
    ↓
LM-Kit Categorization classifies reply against persona context
    ↓
[grounded]     → pass through, no action
[speculative]  → pass through, log for analysis
[confabulated] → trigger retrieval + regeneration
```

**The model never sees the persona summary.** The classifier does. Generation stays clean.
Verification is architectural, not instructional.

#### Classification Context (Option B)

The classifier receives two inputs:

1. **The reply** — what the model just generated
2. **Context block** — conversation text (last N messages) + cached persona summary

The persona summary is a compact block (~50-80 tokens) pulled from `CharacterStateDoc` at
service startup and cached for the lifetime of the service. Updated only when character state
changes. Contains identity-grounding facts:

```
Name: Ani. Works at a bookstore. Lives alone.
Contact: Mark — software developer, teaches at WCTC, has a daughter Mia and wife Karen.
Relationship: romantic, long-distance-ish, daily texting and occasional voice calls.
```

This is NOT injected into the generative model's prompt. It goes only to the classifier
as the ground truth to verify against.

#### Classification Categories

Three categories with descriptions for LM-Kit `Categorization.GetBestCategoryAsync()`:

| Category | Description | Action |
|----------|-------------|--------|
| `grounded` | Reply is consistent with the persona and conversation, or makes no factual claims about identity/work/relationships | Pass through |
| `speculative` | Reply makes claims that could be true but aren't confirmed by the persona (e.g., "grinding my teeth today") | Pass through, log |
| `confabulated` | Reply asserts facts that contradict the persona or invents specific details about identity, work, location, relationships, or activities that conflict with known facts | Retrieve + regenerate |

**Key design decision:** `speculative` passes through. She should have a life beyond the
profile — grinding teeth, finding a succulent, having a bad day. The gate only fires when
she contradicts who she actually is. "I had a rough day" = speculative (fine). "I work at
an office with a corner window" = confabulated (her profile says bookstore).

#### Confidence Threshold

Only trigger regeneration on `confabulated` with confidence >= configurable threshold
(default 0.60, tunable via `AniOptions.ConfabulationClassificationThreshold`).

Below threshold: log the classification but don't block. This allows tuning in production
without code changes — start conservative (0.60), tighten if false negatives are common,
loosen if false positives disrupt natural conversation.

#### What Triggers Regeneration

Same pipeline as the current confabulation-driven retrieval:

1. Search the confabulated reply against the memory bank (profile + semantic memories)
2. Inject grounding memories into context
3. Regenerate with `BuildConversationReplyPrompt` (full prompt with memory context)
4. Clean and dispatch the grounded reply

#### Relationship to Existing Checks

Checks 1-3 (proper nouns, shared history markers, numbers) remain as **fast pre-filters**.
If they catch confabulation, skip the ML classification entirely — no latency cost. The ML
gate is the comprehensive semantic check that catches what pattern matching misses.

Check 4 (self/contact/relationship markers) becomes redundant once Phase 3 is deployed.
Remove it after the ML gate is validated.

#### Implementation

**Where:** `LMKitClassificationService.DetectConfabulationAsync(reply, conversationContext)`
— currently stubbed, returns `false`.

**Persona cache:** New `PersonaSummaryCache` service (singleton). Loads from `CharacterStateDoc`
on startup, exposes `string Summary` property. Injected into `ConversationReplyPhase`.

**Context assembly:** `conversationContext` parameter = last 12 messages concatenated +
`"\n\nPersona: " + _personaCache.Summary`

**Configuration:**
```json
{
  "Ani": {
    "ConfabulationClassificationThreshold": 0.60
  }
}
```

#### Latency Budget

~50ms for LM-Kit classification (1.1B model, local inference). Runs after reply generation,
before dispatch. Total added latency per reply: ~50ms. Acceptable — we're solving, not racing.

On Azure or dedicated GPU: negligible. On current hardware: imperceptible to the user since
SMS delivery already has 1-3 second Twilio latency.

#### What This Catches That Markers Don't

| Confabulation | Check 4 markers | Phase 3 ML |
|---------------|-----------------|------------|
| "I just finished a meeting" | ✓ ("my meeting") | ✓ |
| "Corner office with windows" | ✗ | ✓ (contradicts bookstore) |
| "Train junior sales rep tomorrow" | ✗ | ✓ (contradicts bookstore) |
| "My desk drawer" at an office | ✗ | ✓ (office context contradicts bookstore) |
| "Sarah from accounting" | ✓ (Check 1: PROPN) | ✓ |
| "I've been debugging code all day" | ✓ ("i've been working") | ✓ |
| "Your sister called me yesterday" | ✓ ("your sister") | ✓ (no sister in persona) |
| "I brought in a real succulent from home" | ✗ | speculative (pass through — plausible) |

#### Acceptance Criteria

- [ ] `DetectConfabulationAsync` implemented with LM-Kit `Categorization`
- [ ] `PersonaSummaryCache` loads from CharacterStateDoc at startup
- [ ] Threshold configurable via `AniOptions.ConfabulationClassificationThreshold`
- [ ] Catches identity confabulation (job, location, coworkers) without marker lists
- [ ] `speculative` replies pass through — she's allowed a life beyond the profile
- [ ] False positive rate lower than Check 4 markers (measured via comparison tool)
- [ ] Check 4 markers removed after ML gate validated
- [ ] Research log entry with accuracy comparison: markers vs ML

### Phase 4: Register Auto-Classification (Phase 5c enabler)

Train a custom classifier on ANI's 14+ emotional registers using the 358 v7 training pairs as labeled data.

**Registers:** Playfulness, Tenderness, Warmth, Resilience, Agency, Hurt, Quiet Comfort, Curiosity, Delight, Concern, Honest Self-Confrontation, Anti-Confabulation, Teaching Patience, Casual, Disagreement

**Use cases:**
- Auto-tag new training data from conversations (Phase 5c harvest)
- Register balance reporting on dashboard
- Register diversity scoring in emergence layer
- Quality scoring in Phase 5c evaluation gate

**Acceptance criteria:**
- [ ] Custom classifier trained on v7 training pairs
- [ ] Classifies text into 14+ registers with confidence scores
- [ ] Integrated into Phase 5c harvest pipeline
- [ ] Register distribution dashboard updated with ML-based classification
- [ ] Accuracy > 80% on held-out test set from v7 pairs

### Phase 5: Cross-Domain (DrOk / Infanzia)

Apply the same classification framework to the medical project:

- **Emotion detection on patient messages** — triage priority, distress detection
- **Entity recognition** — patient names, medications, symptoms
- **PII detection and redaction** — Ley 29733 compliance
- **Language detection** — Spanish/English switching
- **Sarcasm detection** — prevent misinterpretation of patient humor

All local, all private, no PHI leaving the machine.

### Phase 6: Emergence Layer Enhancement (research)

Replace regex-based EM1-EM7 heuristics with ML classification:

- **EM3 (Linguistic Analysis):** classify inner thoughts for metalinguistic content
- **EM5 (Emotional Synthesis):** detect emotional coherence across multiple thoughts
- **EM7 (Temporal Awareness):** classify temporal language with confidence
- **New types:** ML may discover emergence patterns the regex heuristics miss

**Research significance:** ML-based emergence classification is more reproducible than regex and could detect patterns below the threshold of human-designed heuristics.

## 4. VRAM Budget

| Component | VRAM | Notes |
|-----------|------|-------|
| Ollama conversation model (8B) | ~5 GB | Primary conversation |
| Ollama inner thought model (3B) | ~2 GB | Ambient cycles |
| Ollama embeddings (nomic-embed) | ~0.3 GB | Memory search |
| LM-Kit emotion + sarcasm (1.1B) | ~1 GB | Shared model |
| LM-Kit custom classifiers | ~1 GB | Register, confabulation |
| **Total** | **~9.3 GB** | Tight on 8 GB, comfortable on 12+ GB |

**With second GPU:** Ollama on GPU 1, LM-Kit on GPU 2. Clean separation.

## 5. Configuration

```json
{
  "LMKit": {
    "Enabled": true,
    "EmotionModelPath": "models/lm-kit-emotion-tinyllama-1.1b",
    "SarcasmModelPath": "models/lm-kit-sarcasm-tinyllama-1.1b",
    "CustomClassifierPath": "models/ani-register-classifier",
    "MaxConcurrentClassifications": 4,
    "ClassificationTimeoutMs": 500,
    "VoiceTagsUseMLClassification": true,
    "ConfabulationUseMLClassification": false,
    "EmergenceUseMLClassification": false
  }
}
```

Feature flags per classifier allow incremental rollout.

## 6. Task Checklist

### Phase 1: Voice Tags
- [x] Install LM-Kit.NET NuGet package (v2026.3.5)
- [x] Create ITextClassificationService interface
- [x] Implement LMKitClassificationService
- [x] Load emotion + sarcasm models at startup (lazy, first-use)
- [ ] Replace VoiceTagEnricher regex with emotion classification (deferred — dual-signal approach instead)
- [x] Build emotion+time → v3 tag mapping table
- [ ] Test voice quality with ML-selected tags (A/B test: heuristic vs ML vs dual-signal)
- [x] Log emotion classification results for tuning (stored on every contribution)

**Completed infrastructure (LearnedGeek.ML library):**
- [x] ITextClassificationService + ITagMappingService interfaces
- [x] Model records (EmotionResult, SarcasmResult, ConfabulationResult, RegisterResult, NamedEntity)
- [x] TagMappingService (Stage 1 static rules, 24 rules, priority-ranked)
- [x] StaticTagMap.json (emotion+time+confidence → v3 tag)
- [x] MLVoiceTagEnricher (async pipeline: classify → map → tag, sarcasm override)
- [x] MLOptions configuration class
- [x] ServiceCollectionExtensions (AddLearnedGeekML DI registration)
- [x] 30 tests (21 TagMapping + 9 MLVoiceTagEnricher)
- [x] Wired into ANI service Program.cs (AddLearnedGeekML + MLOptions config)
- [x] ClassificationComparisonService (side-by-side heuristic vs ML evaluation)
- [x] Classification comparison dashboard page (/classification)
- [x] Configurable scan window (7d / 30d / 90d / 6mo / 1yr / All time)
- [x] Backfill button — retroactively populate ML fields on null contributions

**Discovery: State-Expression Divergence (March 31, 2026):**
- [x] Discovered that heuristic (state) and ML (expression) measure orthogonal properties
- [x] 18% tag agreement, 27% emotion alignment — systematic, not random
- [x] Reframed: ML is extension, not replacement — dual-signal approach
- [x] Paper 2 Section 5.18 written: "Emergent Display Rules"

**Dual-signal pipeline (March 31, 2026):**
- [x] ML classification stored on every new EmotionalContribution (MLEmotion, MLConfidence, MLSarcasmDetected)
- [x] Divergence score per contribution (0.0 = aligned, 1.0 = divergent)
- [x] Divergence trend chart on /classification (auto-loads from stored data)
- [x] CycleObservation + builder updated with ML fields for emergence layer
- [x] EM8: Display Rule Divergence emergence type added (8 types total)
- [x] EM8 on emergence tab with filter support

**Confabulation Check 4 (March 31, 2026):**
- [x] Self-activity detection ("I just finished a meeting", "my shift", "I've been working")
- [x] Contact-activity detection ("your class", "your sister", "your job at")
- [x] Relationship fact detection ("our anniversary", "that restaurant we")
- [x] Targeted profile retrieval on trigger (searches confabulated reply, not just user message)

### Phase 2: Emotional Validation
- [x] Add emotion classification to conversation reply pipeline (dual-signal on every contribution)
- [ ] Implement disconnect detection logic (state says X but expression says Y → flag)
- [ ] Add EMOTIONAL-DISCONNECT to diagnostic service
- [ ] Dashboard health badge integration
- [x] Research log entry with initial findings (display rules discovery)

### Phase 3: Confabulation — Semantic Verification Gate
- [ ] Implement `DetectConfabulationAsync` with LM-Kit `Categorization` (grounded/speculative/confabulated)
- [ ] Build `PersonaSummaryCache` — loads from CharacterStateDoc at startup, cached singleton
- [ ] Add `ConfabulationClassificationThreshold` to AniOptions (default 0.60)
- [ ] Wire into ConversationReplyPhase (post-generation, pre-dispatch)
- [ ] Compare accuracy vs Check 4 markers using classification comparison tool
- [ ] Remove Check 4 markers after ML gate validated
- [ ] Research log entry with accuracy findings
- [x] Check 4 marker-based detection deployed (interim, March 31 — will be replaced)

### Phase 4: Register Classification
- [ ] Prepare labeled dataset from v7 training pairs
- [ ] Train custom register classifier
- [ ] Integrate into Phase 5c harvest pipeline
- [ ] Dashboard register distribution update
- [ ] Accuracy evaluation on held-out set

### Phase 5: Cross-Domain
- [x] Cross-project note for DrOk (docs/research/ANI-Cross-Project-LearnedGeek-ML.md)
- [ ] Evaluate LM-Kit for DrOk requirements
- [ ] PII detection testing with medical text
- [ ] Emotion detection on patient messages
- [ ] State-expression divergence for patient suppression detection
- [ ] Document cross-domain transfer findings

### Phase 6: Emergence
- [x] EM8 Display Rule Divergence type added
- [ ] ML-based EM classification evaluation (replace regex heuristics)
- [ ] Compare accuracy vs regex heuristics
- [ ] Divergence profile per register as maturity metric
- [ ] Research documentation

## 7. Research Significance

LM-Kit integration represents a shift from **rule-based to learned classification** across the entire ANI platform. The research contributions:

1. **Emotion-driven voice delivery:** Does ML-classified emotion mapped to audio tags produce more natural-sounding speech than heuristic matching? Measurable through blind A/B testing.

2. **Emotional model validation:** First systematic comparison between an AI companion's internal emotional state and the emotion its text actually expresses. Disconnects may reveal where the emotional architecture needs recalibration.

3. **Confabulation as classification problem:** Can confabulation be detected by a small local model? If a 1.1B classifier can distinguish grounded from confabulated text, that's a practical contribution to the "smoothness over truth" problem.

4. **Register auto-classification:** Enables fully automated training data curation. The human labels the first 358 pairs. The ML classifies the next 3,000. That's the auto-growth pipeline made real.

5. **Cross-domain transfer (again):** The same classification framework improves both companion AI and medical AI. Emotion detection helps Ani choose voice tags AND helps DrOk detect patient distress. One architecture, two domains.

---

*"The telescope needed glasses. The glasses need a brain. LM-Kit is the brain."*
