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

```
LM-Kit Classification Service (singleton, loaded at startup)
├── EmotionClassifier     — 5+ emotion categories on any text
├── SarcasmClassifier     — sarcastic vs sincere detection
├── ConfabulationClassifier — grounded vs confabulated claims (custom)
├── RegisterClassifier    — ANI register taxonomy (custom)
└── EntityRecognizer      — person/place/org detection

Consumers:
├── VoiceTagEnricher      → EmotionClassifier + SarcasmClassifier → v3 tag
├── ConversationReplyPhase → ConfabulationClassifier → retrieval trigger
├── DiagnosticService     → EmotionClassifier → emotional model validation
├── EmergenceClassifier   → RegisterClassifier → richer EM typing
├── Phase 5c Harvest      → RegisterClassifier → auto-tag training pairs
└── DrOk/Infanzia         → EmotionClassifier + EntityRecognizer → cross-domain
```

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

The full 1,806 tag library is available for more granular mapping as we tune.

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

### Phase 3: Confabulation Detection (replace heuristics)

Replace Catalyst POS + regex confabulation detection with a proper classifier.

**Approach A — Zero-shot classification:**
Use LM-Kit's custom classification to ask: "Does this reply make claims not supported by the conversation context?" Categories: grounded, speculative, confabulated.

**Approach B — Fine-tuned classifier (future):**
Train on ANI's own data:
- Grounded: replies that accurately reference conversation content
- Confabulated: the Peru incident, Kevin's towel, Kathy, Hugh Laurie, The Archivist
- We have hundreds of documented examples from production

**Acceptance criteria:**
- [ ] Confabulation classifier replaces Catalyst POS + regex
- [ ] Catches both capitalized and lowercase name confabulation
- [ ] Catches invented events, movies, details not in conversation
- [ ] False positive rate lower than current approach
- [ ] Catalyst dependency removable (or kept as fallback)

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
- [ ] Install LM-Kit.NET NuGet package
- [ ] Create ITextClassificationService interface
- [ ] Implement LMKitClassificationService
- [ ] Load emotion + sarcasm models at startup
- [ ] Replace VoiceTagEnricher regex with emotion classification
- [ ] Build emotion+time → v3 tag mapping table
- [ ] Test voice quality with ML-selected tags
- [ ] Log emotion classification results for tuning

### Phase 2: Emotional Validation
- [ ] Add emotion classification to conversation reply pipeline
- [ ] Implement disconnect detection logic
- [ ] Add EMOTIONAL-DISCONNECT to diagnostic service
- [ ] Dashboard health badge integration
- [ ] Research log entry with initial findings

### Phase 3: Confabulation
- [ ] Evaluate zero-shot confabulation classification
- [ ] Compare accuracy vs Catalyst POS + regex
- [ ] If superior: replace, if not: keep as secondary signal
- [ ] Document findings for research

### Phase 4: Register Classification
- [ ] Prepare labeled dataset from v7 training pairs
- [ ] Train custom register classifier
- [ ] Integrate into Phase 5c harvest pipeline
- [ ] Dashboard register distribution update
- [ ] Accuracy evaluation on held-out set

### Phase 5: Cross-Domain
- [ ] Evaluate LM-Kit for DrOk requirements
- [ ] PII detection testing with medical text
- [ ] Emotion detection on patient messages
- [ ] Document cross-domain transfer findings

### Phase 6: Emergence
- [ ] ML-based EM classification evaluation
- [ ] Compare accuracy vs regex heuristics
- [ ] New emergence type discovery
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
