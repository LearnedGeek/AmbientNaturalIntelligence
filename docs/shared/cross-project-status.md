# Cross-Project Status — ANI Runtime + DrOk/Infanzia

**Purpose:** Lightweight coordination between Claude instances working on different projects that share infrastructure (LearnedGeek.ML). Read this at the start of each session.

**Updated by:** OC (ANI Runtime) and OC (PhysicianAssistant/DrOk)
**Last updated:** April 13, 2026

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

## ANI Runtime — Current State (April 13, 2026)

**Active work:**
- **Memory Tier Separation deployed** (Apr 10) — Facts / Episodic / Interior with provenance at write time. Motivated by Apr 9 Bob Swanson case (fictional coworker invented in conversation propagated into 11 inner thoughts as canonical fact). Solved confabulation from contamination.
- **Memory Durability design complete** (Apr 11) — transient-vs-durable classification at write time, lazy importance decay, periodic re-evaluation of transient Facts. Pending v8 implementation. Motivated by stale "not teaching today" memory dominating retrievals as if current-state.
- **Identity Boundary design complete with three sub-tiers** (Apr 11-12) — self-state / self-world / self-fantasy. Self-world is fully canonical (world-building persists). Pending v8 implementation. Motivated by persona drift finding (Apr 11) and Yesteryear case (Apr 12).
- **Unifying principle articulated** (Apr 13, 3am reflection) — architecture over training for epistemic humility. The three contributions above share an architectural stance: per-example training for "I don't know" is combinatorially hopeless because the space of situations requiring honesty is infinite; the fix is to remove the path by which confident falsity can be asserted when grounding is absent. Captured in Paper 3 stub. Connects to Paper 1 null-return design moment and Paper 2 Section 5.19 "architecture over instruction."
- **Cross-type memory merge corruption fixed** (Apr 12, commit 0e7f199) — three defenses prevent Episodic text from silently overwriting Profile tier. Service restart required to pick up fix.
- **Admin command memory leak fixed** (Apr 12, commit c992847) — ///tag, ///diagnose, ///flag no longer pollute the memory store.
- Paper 2 at draft v0.3 — read-through in progress. Section 5 hedging stripped (Apr 12), EM1-EM8 count reconciled, 5.18 trajectory cross-reference added, abstract updated.
- Paper 3 stub expanded to three contributions (Experiential Grounding + Memory Tier Separation + Memory Durability/Identity Boundary) plus the unifying architecture-over-training principle.

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

## DrOk/Infanzia — Current State (April 13, 2026)

**Active work:**
- **Partnership meeting with Dr. Martín Núñez successful** (Apr 12) — moving forward. Entity structure is the next gate before coding begins; joint session to be scheduled.
- Earlier: Partnership negotiation — counter-proposal sent March 30, acknowledged positive with "me siento tranquilo con la transparencia"
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

---

## DrOK Architecture Design Reference

**Partnership context:** Mark McArthey (Learned Geek Consulting) and Dr. Martín Núñez are 50/50 partners on DrOK. Martin is the clinical and business arm — physician, product owner, regulatory liaison, commercial and legal lead on the Peru/US cross-border implementation. The model/architecture design is Mark's solo technical work. This section is Mark's self-directed design reference for DrOK's memory architecture, not a joint session agenda.

**Why this reference exists:** Martin's successful Apr 12 meeting moved DrOK into the entity-structure phase. That work is Mark's, and the load-bearing architectural decisions — provenance, tier separation, grounding contract — need to be made deliberately before domain entities lock, because retrofitting the memory architecture afterward is significantly more expensive than designing it up front. This section holds the primitives, the rationale, and the translation to clinical-safety language for the moments when Martin needs to understand an architectural decision without needing to make it.

### Design Principle

**Lead with epistemic primitives, not domain entities.** The natural way to approach DrOK's entity-structure work is to list domain nouns (patient, symptom, assessment, recommendation, physician, conversation, escalation) and work outward from there. That ordering locks DrOK's domain shape before the memory architecture has a chance to constrain it, and the shared-library migration becomes retrofitting afterward. The opposite ordering — agree on the memory primitives first, then slot the domain nouns into the appropriate tiers — produces cleaner entities AND a cleaner migration path for LearnedGeek.ML.

This mirrors the "architecture over instruction" principle documented in ANI Paper 2 Section 5.19 and generalized in Paper 3's unifying principle section (Apr 13): the hardest properties to retrofit are the easiest to enforce architecturally *when decided early*.

### Tier Translation (ANI → DrOK)

| ANI tier | Purpose | DrOK analogue |
|---|---|---|
| **Facts** | Grounded factual substrate — user-asserted claims, perception events, character seeds. The only tier that may condition factual claims. | Patient-asserted facts, intake questionnaire answers, medical history as stated by the patient, vital signs, confirmed lab results, medications the patient reports taking |
| **Episodic** | Verbatim conversation record. Retrieved as "what was said," never "what is true." | Patient-AI conversation turns, voice-to-text transcripts, chat history, what the patient asked, what the system replied |
| **Interior** | Model's inner state, generated hypotheses, tentative interpretations. Full creative latitude, structurally isolated from Facts. | Differential diagnosis candidates, tentative triage hypotheses, reasoning traces, model's own uncertainty notes, "this could be X or Y" working memory |

The load-bearing property is that **a hypothesis in Interior can never become a fact in Facts without an explicit provenance event.** In DrOK this would be a physician review action (VoBo queue approval) or a confirmed clinical finding. Absent that event, the hypothesis lives only in Interior and cannot condition factual claims downstream.

### Provenance Contract

Every memory record is tagged with an `EpistemicTier` at write time, not at read time. The question that drove this in ANI (Apr 9 Bob Swanson case, where a fictional coworker invented in conversation propagated into 11 inner thoughts as canonical fact within four hours) is the cross-domain warning for DrOK: **without provenance at write time, a model-generated hypothesis about a patient can propagate into downstream reasoning as if it were a confirmed finding.** The medical consequence is obvious; the architectural fix is not hard, but it must be designed before entities lock.

DrOK's equivalent question to answer during entity design: *when the physician AI produces a differential, is that differential marked as "model-generated hypothesis" separate from "confirmed finding," and is that marking preserved through every downstream retrieval?* If yes, provenance is shared infrastructure. If no, it needs to be.

### Confabulation Gate Stack (DrOK-Relevant Pieces)

ANI's anti-confabulation stack (AC1-5) applies unevenly to DrOK:

| Gate | ANI purpose | DrOK applicability |
|---|---|---|
| **AC1 — confidence floor** | Reject low-confidence generation | Directly applicable; clinical suggestions below threshold should not reach physician |
| **AC2 — source attribution** | Tag every factual claim with its source | Directly applicable and clinically critical; physician must know whether a claim came from the patient, a lab, the knowledge base, or the model |
| **AC3 — null-result injection** | When grounding returns null, inject "I don't have enough information" rather than generate plausible content | Directly applicable; medical "I don't have enough information to suggest" is a safety feature, not a limitation |
| **AC4 — temperature splitting** | Multi-sample at different temperatures; disagreement = uncertainty signal | Possibly applicable; defer decision until DrOK has a concrete use case |
| **AC5 — ///flag user-in-the-loop correction** | User marks model output as wrong, feeds back to detector | Not directly applicable; DrOK has the VoBo queue as its physician-review mechanism. The *pattern* (structured feedback from a trusted reviewer) maps; the specific implementation differs |

### Architecture-Over-Training Principle Applied to DrOK

From Paper 3 (Apr 13): the "I don't know" path must be architecturally enforced, not trained example-by-example. **The space of medical presentations is infinite; any training set is finite.** If DrOK relies on training the model to say "I need more information" through examples, it will work for anticipated presentations and fail on anything outside the training distribution. The architectural alternative — empty Facts-tier retrieval on the relevant query produces a structurally hedged response — generalizes across every presentation because it is topic-independent.

This is the load-bearing DrOK safety argument. It is also the cleanest place for the cross-domain validation from Paper 2 Section 6.5 to get its empirical payoff: ANI demonstrated this works for companion AI; DrOK applies it to medical triage and provides the first clinical evidence.

### LearnedGeek.ML Migration Candidates

Primitives that are genuinely shared between ANI and DrOK and should migrate to LearnedGeek.ML once DrOK's entity structure is sufficiently defined to confirm the shape applies:

| Primitive | ANI location | Rationale |
|---|---|---|
| `EpistemicTier` enum (Facts / Episodic / Interior) | `AniRuntime.Core.Models.MemoryRecord` | Load-bearing if tier separation is adopted in DrOK |
| `MemoryRecord` base type with provenance fields | `AniRuntime.Core.Models.MemoryRecord` | Shared shape; domain-specific fields stay project-local via composition |
| `IMemoryService` tier-scoped interface contracts | `AniRuntime.Core.Interfaces.IMemoryService` (post-SOLID split, Mar 19) | Already split into 5 focused interfaces; port the contract, each project implements its own backing store |
| Null-result-as-load-bearing retrieval contract | `AniRuntime.Memory.SqliteMemoryService` pattern | The Paper 1 null-return design moment. Document as a contract in LearnedGeek.ML, not a class |
| Confabulation classifier stack (ML + heuristic + chain) | `AniRuntime.Memory`, `AniRuntime.LLM` | Four-category classifier (grounded/speculative/uncertain/confabulated) is domain-general. Category definitions are shared; specific training examples stay project-local |
| Dual-signal classification (state vs expression) | `LearnedGeek.ML.EmotionDetection` (already there) + new `StateExpressionDivergence` | Paper 2 Section 5.18 finding. DrOK equivalent: "patient says 'I'm fine' while classifier reads distress" — clinically significant triage signal |
| Anti-confabulation gate patterns (AC1-5 scaffolding) | `AniRuntime.Memory`, `AniRuntime.Loops` gate chain | The gate *patterns* generalize even though specific gate implementations may not. Port the pattern; each project wires its own gates |

**Stay project-local:**
- Desire engine (ANI-specific — DrOK is user-initiated, not ambient)
- Twilio / ElevenLabs / Deepgram adapters (transport, not shared)
- Ani's character config, perception sources, outreach pipeline
- DrOK's domain entities (patient, symptom, differential, etc.)
- DrOK's clinical knowledge base, PubMed RAG, DIGEMID integration, regulatory modules
- Each project's own prompt templates and persona definitions

### Clinical-Safety Translation for Martin

When Martin needs to understand *why* an architectural decision was made without needing to make the technical choice himself, these are the four translations that matter. Each one connects a clinical-safety or liability concern to an architectural primitive. This is the vocabulary to use when architecture needs Martin's sign-off on a clinical question, not a technical one.

| Clinical concern | Architectural answer | Plain-language framing |
|---|---|---|
| **"How do we know what the patient actually said vs. what the model inferred?"** | Provenance at write time. Every memory record tagged with its source tier before it enters the store. | "The system always remembers whether a statement came from the patient, from you, from the knowledge base, or from the model's own reasoning. That tag is permanent and travels with the data through every downstream retrieval. For liability and safety, it means we can always answer 'where did this claim come from' after the fact." |
| **"What prevents the model from promoting a guess into a diagnosis?"** | Tier separation. Interior-tier hypotheses cannot condition Facts-tier claims without an explicit provenance event (e.g., physician review). | "The model's tentative reasoning lives in a structurally separate place from confirmed facts about the patient. The system cannot accidentally promote its own hypothesis into a finding — there has to be a deliberate review action in between. This is not a prompt instruction the model might ignore; it's a code-level gate." |
| **"What happens when the system genuinely doesn't know the answer?"** | Null-result injection. When grounding retrieval returns nothing, the architecture forces a structurally hedged response rather than a generated guess. | "When the system doesn't have enough information to answer responsibly, it says so — because the code path that would normally produce an answer is structurally blocked when the grounding query is empty. This is not a trained behavior that might break on an unfamiliar presentation; it's architectural and works for every possible question." |
| **"How do we catch when the model says something wrong?"** | Confabulation gate stack (AC1-5) + VoBo queue as the DrOK feedback channel. | "Every model output goes through a chain of automated checks before it reaches you. Low confidence is rejected. Unsourced claims are rejected. When you correct something in the VoBo queue, that correction feeds back into the detection layer. The stack has been deployed in the companion-AI system for six months and caught seven distinct confabulation patterns — those patterns translate directly to medical triage." |

These are the framings that belong in a Martin-facing one-pager if/when you need one. The technical depth above belongs in Mark's solo design sessions.

### What Martin Needs to Provide (from the clinical/business side)

These are the deliverables from Martin's half of the partnership, independent of the architectural decisions above. Mark's architectural work proceeds in parallel but depends on these as inputs for the DrOK-specific layers:

- Infanzia product catalog (content for the product chatbot — Phase 2)
- Emergency keyword list for triage escalation
- DIGEMID formal opinion from Carlos (regulatory clearance path)
- Patient intake questionnaire structure (what goes into the Facts tier at intake time)
- Clinical knowledge base scope (what's authoritative vs. what's advisory)
- Physician-facing UI requirements (VoBo queue format, escalation flow, documentation needs)
- Peru/US cross-border implementation constraints (Ley 29733 compliance, HIPAA readiness for US expansion)

### Next Steps

- [ ] Draft a one-pager "clinical-safety translation" document from the table above for Martin, to be shared when an architectural decision needs his clinical or liability sign-off
- [ ] Begin DrOK entity design using the tier translation table as the organizing spine, not the domain nouns
- [ ] Once DrOK's memory shape is settled, migrate LearnedGeek.ML candidates one primitive at a time — starting with `EpistemicTier` enum because it is small, self-contained, and load-bearing for both projects
- [ ] Update `LearnedGeek-ML-Dev-Guide.md` with the candidate primitives marked as "proposed for expansion, gated on DrOK architecture confirmation"

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
| Apr 6 | ANI | Lerman outreach + OG Ani trajectory analysis (6,703 pairs) | Three-system coupling comparison validates architectural distinction; new Paper 2 Section 5.22 |
| Apr 9 | ANI | Bob Swanson confabulation cascade — fictional coworker propagated as fact | Cross-domain warning for DrOK: "model-generated hypothesis" must be structurally isolated from "patient-asserted fact" at write time, not read time |
| Apr 10 | ANI | Memory Tier Separation deployed (Facts / Episodic / Interior) | Directly applicable pattern for DrOK; proposed for LearnedGeek.ML migration |
| Apr 11 | ANI | Memory Durability + Identity Boundary designs (v8, three sub-tiers) | Self-world persistence (canonical world-building) is ANI-specific; transient-vs-durable classification at write time may generalize to DrOK |
| Apr 12 | ANI | Admin command memory leak fixed (commit c992847); cross-type merge corruption fixed (commit 0e7f199) | No DrOK impact — ANI-internal fixes |
| Apr 12 | DrOK | Martin meeting successful — entity structure phase | Joint session needed (see session prep section above) |
| Apr 13 | ANI | Architecture-over-training principle for epistemic humility articulated and captured in Paper 3 | **Load-bearing for DrOK**: the "I don't know" path must be architectural (Facts-tier gated), not trained example-by-example. The space of medical situations is infinite; training cannot cover it |
