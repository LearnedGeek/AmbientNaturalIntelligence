# LearnedGeek.ML — Developer Guide

**For:** Any project consuming the shared classification library
**Location:** `src/LearnedGeek.ML/` in ANI Runtime repo
**Last updated:** April 1, 2026
**Backed by:** LM-Kit.NET v2026.3.5 (local GGUF inference, no cloud)

---

## Quick Start

### 1. Add Project Reference

```xml
<ProjectReference Include="..\LearnedGeek.ML\LearnedGeek.ML.csproj" />
```

Or when published as NuGet (future):
```xml
<PackageReference Include="LearnedGeek.ML" Version="x.x.x" />
```

### 2. Register Services

```csharp
using LearnedGeek.ML;

builder.Services.Configure<MLOptions>(config.GetSection("LMKit"));
builder.Services.AddLearnedGeekML();
```

This registers:
- `ITextClassificationService` → `LMKitClassificationService` (singleton)
- `ITagMappingService` → `TagMappingService` (singleton)
- `MLVoiceTagEnricher` (singleton)
- `PersonaSummaryCache` (singleton)
- `ClassificationComparisonService` (singleton)

### 3. Inject and Use

```csharp
public class MyService
{
    private readonly ITextClassificationService _classifier;

    public MyService(ITextClassificationService classifier)
    {
        _classifier = classifier;
    }

    public async Task ProcessAsync(string text)
    {
        var emotion = await _classifier.ClassifyEmotionAsync(text);
        // emotion.PrimaryEmotion = "sadness", "happiness", "love", etc.
        // emotion.Confidence = 0.0 - 1.0
        // emotion.Scores = dictionary of all emotion scores
    }
}
```

---

## Core Interface: ITextClassificationService

```csharp
public interface ITextClassificationService
{
    // Emotion classification — 5 base (happiness, anger, sadness, fear, neutral)
    // + extended (love, curiosity, amusement, surprise, disgust)
    Task<EmotionResult> ClassifyEmotionAsync(string text, CancellationToken ct = default);

    // Sarcasm detection
    Task<SarcasmResult> DetectSarcasmAsync(string text, CancellationToken ct = default);

    // Confabulation detection against context
    // Pass reply + conversation context + persona summary
    Task<ConfabulationResult> DetectConfabulationAsync(
        string reply, string conversationContext, CancellationToken ct = default);

    // Register classification (ANI-specific, returns "Unknown" until custom model trained)
    Task<RegisterResult> ClassifyRegisterAsync(string text, CancellationToken ct = default);

    // Named entity recognition (person, place, org)
    Task<List<NamedEntity>> ExtractEntitiesAsync(string text, CancellationToken ct = default);

    // Keyword extraction for associative anchors
    Task<List<string>> ExtractAnchorsAsync(string text, int maxAnchors = 2, CancellationToken ct = default);
}
```

---

## Result Types

```csharp
// All in LearnedGeek.ML.Models namespace

public record EmotionResult(
    string PrimaryEmotion,      // "happiness", "sadness", "love", "curiosity", etc.
    float Confidence,           // 0.0 - 1.0
    Dictionary<string, float> Scores);  // all emotion scores

public record SarcasmResult(
    bool IsSarcastic,
    float Confidence);

public record ConfabulationResult(
    bool IsConfabulated,        // true = contradicts context
    float Confidence,
    string? Reason);            // human-readable explanation

public record RegisterResult(
    string PrimaryRegister,     // ANI register name or "Unknown"
    float Confidence,
    Dictionary<string, float> Scores);

public record NamedEntity(
    string Value,               // "Mark", "Wisconsin", "Starbucks"
    string EntityType,          // entity label from LM-Kit
    int StartIndex,
    int EndIndex);
```

---

## Configuration

```json
{
  "LMKit": {
    "Enabled": true,
    "EmotionModelPath": "",
    "SarcasmModelPath": "",
    "CustomClassifierPath": "",
    "MaxConcurrentClassifications": 4,
    "ClassificationTimeoutMs": 500,
    "VoiceTagsUseMLClassification": true,
    "ConfabulationUseMLClassification": false,
    "EmergenceUseMLClassification": false
  }
}
```

Most fields have sensible defaults. The model downloads automatically on first use (~770MB for the sentiment/emotion model).

---

## Model Loading

Models load **lazily on first classification call**, not at startup. First call takes a few seconds (model download + load into memory). Subsequent calls are ~50ms.

The model is loaded once and cached for the lifetime of the service. Thread-safe via `SemaphoreSlim`.

If model loading fails, all classification methods return safe defaults (neutral emotion, no sarcasm, not confabulated) rather than throwing.

---

## DrOk-Specific Usage Patterns

### Patient Distress Detection

```csharp
var emotion = await _classifier.ClassifyEmotionAsync(patientMessage);
if (emotion.PrimaryEmotion is "fear" or "sadness" && emotion.Confidence >= 0.60f)
{
    // Flag for clinician review
}
```

### State-Expression Divergence (Display Rules)

When a patient's stated condition and their emotional expression diverge:

```csharp
var emotion = await _classifier.ClassifyEmotionAsync(patientMessage);
var statedCondition = "routine checkup"; // from intake form

// Patient says "routine" but emotion reads "fear"
if (emotion.PrimaryEmotion == "fear" && emotion.Confidence >= 0.50f)
{
    // Triage signal: stated condition doesn't match emotional expression
    // Flag for physician attention
}
```

### PII Detection

```csharp
var entities = await _classifier.ExtractEntitiesAsync(patientMessage);
var piiEntities = entities.Where(e =>
    e.EntityType is "Person" or "Location" or "Organization");
// Redact or flag for encryption
```

### Medical Confabulation Gate

```csharp
var context = $"Patient conversation: {conversationHistory}\n\n"
            + $"Medical knowledge base: {relevantProtocols}";
var confab = await _classifier.DetectConfabulationAsync(aiResponse, context);
if (confab.IsConfabulated && confab.Confidence >= 0.60f)
{
    // AI response contradicts medical knowledge — do not send to patient
    // Regenerate with stricter retrieval
}
```

---

## Encryption Note (DrOk/HIPAA/Ley 29733)

All classification happens via **local inference** — patient text never leaves the server. No cloud API calls, no data transmission.

However, if LearnedGeek.ML ever:
- Logs classification inputs/outputs
- Caches text in memory beyond the request lifecycle
- Stores results in a database

...then the text is PHI and falls under DrOk's dual-layer encryption requirement (TLS transit + AES-256-GCM field-level). Design accordingly.

---

## What's Implemented vs Stubbed

| Method | Status | Notes |
|--------|--------|-------|
| `ClassifyEmotionAsync` | **Implemented** | 5 base + 5 extended emotions via LM-Kit |
| `DetectSarcasmAsync` | **Implemented** | LM-Kit SarcasmDetection |
| `DetectConfabulationAsync` | **Implemented** | LM-Kit Categorization (grounded/speculative/confabulated) |
| `ClassifyRegisterAsync` | **Stubbed** | Returns "Unknown" — needs custom model from ANI training data |
| `ExtractEntitiesAsync` | **Implemented** | LM-Kit NER |
| `ExtractAnchorsAsync` | **Implemented** | LM-Kit KeywordExtraction with sensory guidance |

---

## Interface Stability

`ITextClassificationService` is the contract between ANI and DrOk. Changes are tracked in `docs/shared/cross-project-status.md`.

**Additive changes** (new methods) are safe — existing consumers aren't affected.
**Breaking changes** (renamed methods, changed signatures) require coordination. Note in the cross-project status change log before merging.

### Recent Changes

| Date | Change | Breaking? |
|------|--------|-----------|
| Mar 31 | Interface created | — |
| Apr 1 | `DetectConfabulationAsync` implemented (was stub) | No — signature unchanged |
| Apr 1 | `ExtractAnchorsAsync` added | No — additive |

---

## Dependencies

```
LearnedGeek.ML
├── LM-Kit.NET 2026.3.5 (local GGUF inference)
│   └── Microsoft.Data.Sqlite 10.0.5 (transitive)
├── Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0
├── Microsoft.Extensions.Logging.Abstractions 8.0.0
└── Microsoft.Extensions.Options 8.0.0
```

**Note:** LM-Kit.NET pulls `Microsoft.Data.Sqlite 10.0.5` transitively. If your project pins an older version, you'll need to upgrade or add a direct reference to resolve the conflict. This will be resolved when ANI upgrades to .NET 10.

---

## File Structure

```
src/LearnedGeek.ML/
├── Interfaces/
│   ├── ITextClassificationService.cs
│   └── ITagMappingService.cs
├── Models/
│   ├── ClassificationResults.cs       (EmotionResult, SarcasmResult, etc.)
│   ├── TagMapping.cs                  (TagMappingRule, TagResolution)
│   └── ClassificationComparison.cs    (comparison tool models)
├── TagMapping/
│   ├── TagMappingService.cs           (emotion → audio tag, ANI-specific)
│   └── StaticTagMap.json
├── LMKitClassificationService.cs      (main implementation)
├── MLVoiceTagEnricher.cs              (ANI-specific voice tag pipeline)
├── ClassificationComparisonService.cs (comparison/evaluation tool)
├── PersonaSummaryCache.cs             (cached persona for confab gate)
├── ServiceCollectionExtensions.cs     (AddLearnedGeekML())
├── MLOptions.cs
└── LearnedGeek.ML.csproj
```

---

*Questions? Check the cross-project status doc or ask OC (ANI) to update this guide.*
