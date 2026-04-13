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

## Upcoming Joint Session: DrOK Entity Structure + LearnedGeek.ML Expansion

**Date:** TBD — scheduled after Martin's successful partnership meeting (April 12). Entity structure is the next gate before coding begins.
**Participants:** Mark McArthey (Learned Geek Consulting), Dr. Martín Núñez (DrOk clinical partner)
**Goal:** Lock DrOK's entity structure while simultaneously deciding which ANI Runtime architectural primitives migrate into LearnedGeek.ML as the shared substrate for both projects. Treat the entity-structure conversation as a one-time forcing function — once DrOK's schema locks, retrofitting the shared library becomes significantly more expensive.

### Session Principle

**Lead with epistemic primitives, not domain entities.** The default way to approach an entity-structure session is to list the domain nouns (patient, symptom, assessment, recommendation, physician, conversation, escalation, etc.) and work outward. That ordering locks DrOK's domain shape before the memory architecture has a chance to constrain it, and the shared-library migration becomes retrofitting afterward. The opposite ordering — agree on the memory primitives first, then slot the domain nouns into the appropriate tiers — produces cleaner entities AND a cleaner migration path for LearnedGeek.ML, because the primitives are what both projects share and the domain entities are what they do not.

This mirrors the "architecture over instruction" principle documented in ANI Paper 2 Section 5.19 and generalized in Paper 3's unifying principle section (Apr 13): the hardest properties to retrofit are the easiest to enforce architecturally *when you decide early*. Entity-structure day is that early moment for DrOK.

### Agenda

**Phase 1 — Epistemic primitives walkthrough (20 min).** Walk Martin through the tiered memory model ANI uses and ask whether each tier has a DrOK analogue.

| ANI tier | Purpose | Likely DrOK analogue |
|---|---|---|
| **Facts** | Grounded factual substrate — user-asserted claims, perception events, character seeds. The only tier that may condition factual claims. | Patient-asserted facts, intake questionnaire answers, medical history as stated, vital signs, confirmed lab results |
| **Episodic** | Verbatim conversation record. Retrieved as "what was said," never "what is true." | Physician-patient conversation turns, voice-to-text transcripts, chat history |
| **Interior** | Model's inner state, generated hypotheses, differentials, tentative interpretations. Full creative latitude, structurally isolated from Facts. | Differential diagnosis candidates, tentative triage hypotheses, reasoning traces, model's own uncertainty notes |

**Phase 2 — Provenance framework discussion (15 min).** ANI tags every memory with an `EpistemicTier` at write time, not at read time. DrOK's equivalent question: when the physician AI produces a differential, is that differential marked as "model-generated hypothesis" separate from "confirmed finding"? If yes, the provenance framework is shared infrastructure. If no, it needs to be — confabulation in medical triage is a safety-critical failure mode and the Apr 9 Bob Swanson case in ANI is the cross-domain warning.

**Phase 3 — Confabulation gate stack (15 min).** ANI's anti-confabulation stack (AC1-5: confidence floor, source attribution, null-result injection, temperature splitting, /// feedback command) is the generalized version of what DrOK needs at the boundary between "AI suggestion" and "physician-facing output." Walk Martin through the five and ask which are directly applicable. Likely applicable: AC1 (confidence floor), AC2 (source attribution — critical for medical), AC3 (null-result injection — critical for "I don't have enough information to suggest"). Possibly applicable: AC4 (temperature splitting). Not applicable: AC5 (user-in-the-loop correction signal — DrOK has VoBo queue instead).

**Phase 4 — LearnedGeek.ML migration scope (10 min).** From the discussion above, produce a list of primitives that will move into LearnedGeek.ML vs. primitives that stay project-local.

**Phase 5 — Out of scope for this session (5 min).** Lock what the session is NOT deciding, so the conversation stays focused. Out of scope: ANI.Core NuGet packaging (premature — wait for second consumer), desire engine (ANI-specific, no DrOK analogue), emotional register system (different domain), Twilio / ElevenLabs / Deepgram adapters (transport layer, not shared).

### Proposed LearnedGeek.ML Expansion

**Candidates to migrate from ANI → LearnedGeek.ML (if Martin agrees the primitive applies to DrOK):**

| Primitive | ANI location | Rationale |
|---|---|---|
| `EpistemicTier` enum (Facts / Episodic / Interior) | `AniRuntime.Core.Models.MemoryRecord` | Load-bearing for both projects if tier separation is adopted |
| `MemoryRecord` base type with provenance fields | `AniRuntime.Core.Models.MemoryRecord` | Shared shape; domain-specific fields stay project-local via inheritance or composition |
| `IMemoryService` tier-scoped interface | `AniRuntime.Core.Interfaces.IMemoryService` (post-SOLID split, Mar 19) | Already split into 5 focused interfaces; port the contract, each project implements its own backing store |
| Null-result-as-load-bearing retrieval contract | `AniRuntime.Memory.SqliteMemoryService` pattern | The Paper 1 null-return design moment — when a grounding query returns null, the system must treat the absence as load-bearing rather than confabulate. This is a contract, not a class — document it in LearnedGeek.ML |
| Confabulation classifier stack (ML + heuristic + chain) | `AniRuntime.Memory`, `AniRuntime.LLM` | Four-category classifier (grounded/speculative/uncertain/confabulated) is domain-general. The category definitions are shared; the specific training examples stay project-local |
| Dual-signal classification infrastructure | `LearnedGeek.ML.EmotionDetection` (already there) + new `StateExpressionDivergence` | The divergence finding is ANI's Paper 2 Section 5.18 result; DrOK's equivalent is "patient says 'I'm fine' while classifier reads distress" — clinically significant triage signal |
| Anti-confabulation gate patterns (AC1-5 scaffolding) | `AniRuntime.Memory`, `AniRuntime.Loops` gate chain | The gate *patterns* generalize even though the specific gates may not. Port the pattern, each project wires its own gates |

**Stay project-local:**
- Desire engine (ANI-specific — DrOK is user-initiated, not ambient)
- Twilio / ElevenLabs / Deepgram adapters (transport, not shared)
- Ani's character config, perception sources, outreach pipeline
- DrOK's domain entities (patient, symptom, differential, etc.)
- DrOK's clinical knowledge base, PubMed RAG, DIGEMID integration
- Each project's own prompt templates and persona definitions

### Questions to Ask Martin

1. In DrOK's design, where does "a model hypothesis" live, and how does it differ from "a confirmed fact"? (If there's no structural difference, we have found the confabulation vector before coding begins.)
2. When the model generates a differential and the physician VoBo queue approves or rejects it, does that decision flow back into memory? If yes, what tier does the approval write to? (This is DrOK's analogue of ANI's ///flag feedback command.)
3. Does DrOK's conversation engine need a "I don't have enough information" response path, and if so, is it architecturally enforced (empty retrieval → structural hedge) or trained (examples in the model)? (This is the Paper 3 architecture-over-training principle from Apr 13 — if DrOK trains it, the space of medical situations is infinite and the training will not generalize; if DrOK enforces it architecturally via Facts-tier grounding, it generalizes across every presentation.)
4. What's DrOK's approach to patient-said-it vs model-inferred-it attribution in the final physician-facing output? (If provenance is not preserved to the physician, the cross-domain insight from Paper 2 Section 6.5 is not yet applied.)
5. For LearnedGeek.ML specifically: does DrOK prefer project-reference (compile-time coupling, easier iteration, shared repo) or NuGet package (release discipline, clearer contract, separate versioning)? The answer affects how aggressively we can migrate — project-reference lets us move fast.

### Deliverables to Draft Before the Session

- [ ] A one-page "tier translation" document showing ANI's Facts/Episodic/Interior with sample DrOK content in each cell. Concrete, not abstract. Give Martin something to poke at rather than an empty framework.
- [ ] A one-page "confabulation safety ladder" showing AC1-5 with medical-triage examples mapped from ANI's companion-AI examples. Martin is a physician; examples from his own domain will land harder than examples from a bookstore clerk's world.
- [ ] A one-page "what moves to LearnedGeek.ML vs stays local" cheat sheet using the tables above. The goal is to leave the session with a shared list, not a debate.
- [ ] An updated `LearnedGeek-ML-Dev-Guide.md` with the candidate primitives marked as "proposed for expansion — gated on DrOK session alignment."

### Principle Anchors (for the session itself)

If the conversation drifts into domain-entity-first mode, gently redirect with these anchors:

- **"Let's figure out the memory shape before the entity shape — it'll save us a retrofit."**
- **"If the model generates a hypothesis in DrOK, where does it live, and how do we make sure it doesn't become a fact by accident?"** (This is the Bob Swanson question translated to medical.)
- **"Is the 'I don't know' path architectural or trained?"** (The Paper 3 Apr 13 question.)

### Success Criteria for the Session

A good session leaves with: (1) agreement on tier structure for DrOK memory; (2) a list of LearnedGeek.ML migration candidates with yes/no/maybe per item; (3) an answer to the "where does model hypothesis live vs patient-asserted fact" question; (4) a follow-up date for the domain-entity pass, which happens *after* the tier structure is locked.

A bad session leaves with: a finished domain-entity model and an unresolved memory architecture, because then the shared library migration is retrofitting.

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
