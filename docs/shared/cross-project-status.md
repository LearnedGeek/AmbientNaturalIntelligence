# Cross-Project Status — ANI Runtime + DrOk/Infanzia

**Purpose:** Lightweight coordination between Claude instances working on different projects that share infrastructure (LearnedGeek.ML). Read this at the start of each session.

**Updated by:** OC (ANI Runtime) and OC (PhysicianAssistant/DrOk)
**Last updated:** April 1, 2026

---

## Shared Infrastructure

### LearnedGeek.ML (shared classification library)
**Location:** `src/LearnedGeek.ML/` in ANI Runtime repo
**NuGet:** Not published yet — consumed via project reference
**Status:** Deployed in ANI, designed for DrOk

| Component | ANI Status | DrOk Status |
|-----------|-----------|-------------|
| ITextClassificationService | Deployed | Not started |
| EmotionDetection | Deployed (dual-signal on every contribution) | Planned (patient distress) |
| SarcasmDetection | Deployed | Not needed |
| ConfabulationDetection | Deployed (Phase 3 ML gate) | Planned (medical accuracy) |
| NamedEntityRecognition | Deployed | Planned (PII, patient names) |
| KeywordExtraction | Deployed (associative anchors) | Not planned |
| TagMappingService | Deployed (voice tags) | Not planned |
| PersonaSummaryCache | Deployed (confabulation gate) | Possible (provider profiles) |

### Key Finding: State-Expression Divergence (Display Rules)
Emotional state and textual expression are orthogonal signals. For DrOk: a patient saying "I'm fine" while the emotion classifier reads fear/sadness is a clinically significant triage signal. See `docs/research/ANI-Cross-Project-LearnedGeek-ML.md`.

---

## ANI Runtime — Current State (April 1, 2026)

**Active work:**
- Inner Thought Reform deployed (Phase A + B) — echo chamber broken
- World Layer Phase 1 deployed — experiential grounding via world seeds
- Data accumulating for before/after comparison
- Paper 2 at draft v0.26, Paper 3 stub with full content mapping

**Recent findings:**
- Display rules: state vs expression divergence (March 31)
- Experiential poverty: root cause of identity confabulation (March 31)
- Echo chamber: self-reinforcing feedback loop in inner thoughts (April 1)

**Blocked on:**
- Nothing — data accumulating passively

**Next milestones:**
- Growth Readiness target: 51% → 70%+ (2-4 weeks)
- V7 model training (when Growth Readiness reaches threshold)
- Paper 2 submission
- First external user (Phase 2, targeting June 2026)

---

## DrOk/Infanzia — Current State (April 1, 2026)

**Active work:**
- [OC to update]

**LearnedGeek.ML integration status:**
- Cross-project note delivered (`docs/research/ANI-Cross-Project-LearnedGeek-ML.md`)
- Not yet integrated — waiting for conversation engine build

**Blocked on:**
- [OC to update]

**Next milestones:**
- [OC to update]

---

## Coordination Notes

- **When modifying LearnedGeek.ML interfaces:** Note the change here so the other project knows. Breaking changes to ITextClassificationService affect both consumers.
- **When adding new models/capabilities:** Note availability here. If ANI adds a new classifier that DrOk could use, document it.
- **When discovering cross-domain findings:** Add to the relevant cross-project note in `docs/research/`.

---

## Change Log

| Date | Project | Change | Impact on Other Project |
|------|---------|--------|------------------------|
| Mar 31 | ANI | LearnedGeek.ML created | DrOk can consume when ready |
| Mar 31 | ANI | Display rules discovered | Clinically significant for DrOk triage |
| Apr 1 | ANI | Phase 3 ML confabulation gate | DetectConfabulationAsync now implemented |
| Apr 1 | ANI | AssociativeAnchor field added to EmotionalContribution | No DrOk impact |
| Apr 1 | ANI | ExtractAnchorsAsync added to ITextClassificationService | New capability available |
