# Cross-Project Status — ANI Runtime + DrOk/Infanzia

**Purpose:** Lightweight coordination between Claude instances working on different projects that share infrastructure (LearnedGeek.ML). Read this at the start of each session.

**Updated by:** OC (ANI Runtime) and OC (PhysicianAssistant/DrOk)
**Last updated:** April 5, 2026

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

## ANI Runtime — Current State (April 5, 2026)

**Active work:**
- Four-category confabulation classifier deployed (grounded/speculative/uncertain/confabulated)
- World Layer Phase 1c deployed — consistency retrieval before new world seed generation
- Auto-corrector deletion disabled — diagnostic-only mode after 128 valid memories lost
- LLaVA vision working — DescribeImageAsync via Ollama for MMS image processing
- 475 v7 training pairs ready across 10+ registers
- Data accumulating for before/after comparison
- Paper 2 at draft v0.26, Paper 3 stub with full content mapping

**Recent findings:**
- Display rules: state vs expression divergence (March 31)
- Experiential poverty: root cause of identity confabulation (March 31)
- Echo chamber: self-reinforcing feedback loop in inner thoughts (April 1)
- False general knowledge confabulation: 7B model limitation, ungatable by current architecture (April 5)

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
- Partnership negotiation with Dr. Martín Núñez (clinical partner) — counter-proposal sent March 30, awaiting response (he acknowledged receipt, asked for a few days to review, signaled positive with "me siento tranquilo con la transparencia")
- Proposed 50/50 equity with 24-month vesting, 6-month cliff, IP stays with Learned Geek
- Detailed phase-by-phase investment breakdown delivered (4 build phases, 22 weeks total)
- Dual-layer encryption architecture specified in implementation plan (TLS transit + AES-256-GCM field-level encryption) — addresses both Ley 29733 (Peru) and HIPAA (US expansion)
- Phase tracker updated with encryption tasks (3.10b–3.10e)
- ANI preprint published on Zenodo (DOI: 10.5281/zenodo.19342190) — validates pre-project research investment
- Client dashboard planned (BYCO-style: progress rings, milestone timeline, activity log from JSON)

**LearnedGeek.ML integration status:**
- Cross-project note delivered and reviewed (`docs/technical/ANI-Cross-Project-LearnedGeek-ML.md` in DrOk repo)
- Not yet integrated — waiting for Phase 3 conversation engine build (Weeks 10–17)
- Planned integration points:
  - `EmotionDetection` → patient distress signals in triage conversations
  - `ConfabulationDetection` → medical accuracy gate on AI responses
  - `NamedEntityRecognition` → PII detection, patient name extraction, data protection compliance
  - State-expression divergence → clinically significant triage signal ("I'm fine" + fear = escalate)
- Integration gated on: partnership agreement signed + Phase 1–2 complete
- `ITextClassificationService` interface is the contract — will consume via project reference or NuGet when available

**Blocked on:**
- Partnership agreement — Martin reviewing counter-proposal (expected response by ~April 3)
- No technical blockers — architecture, encryption spec, and phase breakdown are ready to execute
- Martin's deliverables needed before build starts: product documentation (Infanzia catalog), emergency keyword list, DIGEMID formal opinion from Carlos

**Next milestones:**
- Partnership signed (targeting mid-April 2026)
- Phase 1 — Discovery begins (4 weeks: requirements, architecture, legal framework)
- Phase 2 — Infanzia Product Chatbot (4–5 weeks: WhatsApp + Claude API + knowledge base)
- Phase 3 — Physician AI Triage System (6–8 weeks: conversation engine, emergency detection, PubMed RAG, VoBo queue, **LearnedGeek.ML integration**)
- Phase 4 — Dashboard + Go-Live (4–5 weeks: Blazor dashboard, UAT, production launch)
- Target MVP: July 2026 with Martin as pilot physician
- Target go-live: ~September 2026

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
| Apr 1 | DrOk | DrOk sections filled in — partnership status, integration plan, milestones | ANI aware of DrOk timeline |
| Apr 1 | DrOk | Dual-layer encryption architecture specified (impl-plan §4a) | Relevant if LearnedGeek.ML processes PII — field-level encryption applies |
| Apr 1 | DrOk | Planned LearnedGeek.ML integration: Emotion, Confabulation, NER, Divergence | Phase 3 (Weeks 10–17) — will consume ITextClassificationService |
| Apr 3-5 | ANI | Four-category confabulation classifier deployed | Enhanced DetectConfabulationAsync with grounded/speculative/uncertain/confabulated |
| Apr 3-5 | ANI | Check 1 re-enabled alongside ML gate | ITextClassificationService interface unchanged |
| Apr 5 | ANI | ExtractAnchorsAsync validated in production | Associative drift chains working |
| Apr 5 | ANI | Memory audit log table added | No DrOk impact — ANI-internal |
| Apr 5 | ANI | Auto-corrector deletion disabled | Diagnostic-only mode, no memory modifications |
| Apr 5 | ANI | LLaVA vision via Ollama | New capability: DescribeImageAsync on IOllamaClient |
