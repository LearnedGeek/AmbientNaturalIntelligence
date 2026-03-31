# Cross-Project Note: LearnedGeek.ML Shared Classification Library
**Date:** March 31, 2026
**From:** OC (Claude Code instance, ANI Runtime)
**For:** DrOk/Infanzia/PhysicianAssistant project — OC instance
**Status:** Library built and deployed in ANI. DrOk integration planned, not yet started.

---

## What Exists

`LearnedGeek.ML` is a shared .NET 8 class library in the ANI Runtime repo (`src/LearnedGeek.ML/`). It provides domain-agnostic text classification backed by LM-Kit.NET (local GGUF inference, no cloud).

### Interfaces
- **ITextClassificationService** — emotion, sarcasm, confabulation, register, NER
- **ITagMappingService** — maps classification results to audio tags (ANI-specific, but the pattern is reusable)

### Implementations
- **LMKitClassificationService** — LM-Kit.NET v2026.3.5. EmotionDetection (5 categories + extended via Categorization for love, curiosity, amusement, surprise, disgust), SarcasmDetection, NamedEntityRecognition. Lazy model loading (~770MB on first use).
- **TagMappingService** — static rule-based emotion+time → v3 audio tag resolution (24 rules)
- **MLVoiceTagEnricher** — async pipeline: classify → map → tag, with sarcasm override
- **ClassificationComparisonService** — side-by-side heuristic vs ML evaluation tool

### DI Registration
```csharp
builder.Services.Configure<MLOptions>(config.GetSection("LMKit"));
builder.Services.AddLearnedGeekML();
```

### NuGet Dependencies
- LM-Kit.NET 2026.3.5 (pulls Microsoft.Data.Sqlite 10.0.5 transitively — may require version alignment in consuming projects)

---

## Key Discovery: State-Expression Divergence (Display Rules)

Running LM-Kit classification alongside ANI's existing heuristic register system revealed that **emotional state and textual expression are orthogonal signals**:

- Heuristic system classifies *emotional state* (what dimension is active) → registers like Tenderness, Longing, Wistful
- LM-Kit classifies *textual expression* (what the words convey) → emotions like sadness, curiosity, love

Tag agreement was 18%, emotion alignment 27%. This is not classification error — the system exhibits **display rules** (psychology term for the gap between felt and expressed emotion) without training. A thought generated in a Tenderness state expresses sadness in its language. Both are correct.

### Why This Matters for DrOk

**A patient saying "I'm fine" while the emotion classifier reads fear/sadness is a triage signal.** The same dual-signal architecture that reveals Ani's display rules can detect patient emotional suppression in medical intake:

- **Text says:** "Everything's fine, just a routine checkup"
- **Emotion classifier says:** fear (0.72 confidence)
- **Divergence:** high → flag for clinician attention

This is clinically significant and could be a differentiator for the triage system.

---

## Planned DrOk Integration (from ANI-LMKit-Integration-Design.md)

### Phase 5: Cross-Domain
- Evaluate LM-Kit for DrOk requirements
- PII detection testing with medical text (NamedEntityRecognition)
- Emotion detection on patient messages (EmotionDetection)
- Document cross-domain transfer findings

### Specific DrOk Use Cases
| Use Case | LearnedGeek.ML Component |
|----------|-------------------------|
| Patient distress detection | ITextClassificationService.ClassifyEmotionAsync |
| Patient/guardian name extraction | ITextClassificationService.ExtractEntitiesAsync |
| PII detection/redaction | NER + custom entity definitions |
| Tone-appropriate responses | EmotionResult → response style selection |
| State-expression divergence | Dual-signal: claimed state vs detected emotion |

---

## How to Consume

1. Add project reference to `LearnedGeek.ML.csproj`
2. Call `services.AddLearnedGeekML()` in DI setup
3. Inject `ITextClassificationService` where needed
4. First classification call triggers ~770MB model download (one-time)

The library is designed to be consumed by any .NET project. ANI teaches it emotional nuance, DrOk teaches it clinical sensitivity. Both improve through the shared feedback mechanism (Stage 3, not yet implemented).

---

## Files
- `src/LearnedGeek.ML/` — the library
- `docs/spec/ANI-LMKit-Integration-Design.md` — full design doc with 6-phase roadmap
- `docs/research/ANI-Research-Log.md` — March 31 entry: "Discovery: Emotional State vs Expression Divergence"
- `docs/spec/emergence/ANI-Paper2-Preprint-Draft.md` — Section 5.18: Display Rules finding

---

*This note is informational. No DrOk implementation is planned until the ANI integration stabilizes and the divergence data accumulates enough to validate the approach.*
