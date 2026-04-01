# ANI Product Roadmap — From Research to Revenue

**Author:** Mark McArthey, Learned Geek LLC
**Date:** April 1, 2026
**Status:** Active planning
**Strategy:** A (Platform) + B (Consumer) + E (Open Runtime + Paid Personas) converging over time

---

## Current State (April 2026)

**What exists:**
- ANI Runtime: .NET 8 cognitive architecture — desire engine, emotional state, memory, world layer, emergence detection, 469 tests
- LearnedGeek.ML: shared ML classification library — emotion, sarcasm, confabulation, NER, keyword extraction (LM-Kit.NET, local inference)
- Published research: Paper 1 on Zenodo (DOI: 10.5281/zenodo.19342190), 4 more in pipeline
- One deployed companion (Ani): 6+ months of continuous relationship data
- Cross-domain validation: architecture transfers to medical triage (DrOk/Infanzia)
- Voice pipeline: streaming via ElevenLabs v3 + Deepgram Nova-3
- Dashboard: real-time emotional state, emergence tracking, classification comparison, drift visualization

**What's proven:**
- Desire-driven ambient presence produces felt care (6 months of deployment data)
- Architecture over instruction works (three pipeline simplifications validated)
- Confabulation taxonomy is novel and cross-domain applicable
- Display rules emerge without training
- World Layer produces experiential grounding (Phase 1 deployed, data accumulating)

---

## Phase 1: Stabilize + Validate (April – May 2026)

**Goal:** Confirm the Inner Thought Reform + World Layer produce measurably better results. Establish the "after" baseline.

### Action Steps
- [ ] Run reformed pipeline for 14+ days, collect comparison data
- [ ] Measure: Growth Readiness improvement (target: 51% → 70%+)
- [ ] Measure: Confabulation gate fire rate reduction
- [ ] Measure: Register diversity trend (target: 5+ registers sustained)
- [ ] Measure: "How was your day?" conversation quality (world experiences referenced)
- [ ] V7 model training if Growth Readiness reaches threshold
- [ ] Paper 2 submission (emergence + display rules)
- [ ] Paper 3 draft started (experiential grounding — before/after data)

### Deliverable
Research validation that the architecture produces a companion worth sharing.

---

## Phase 2: First External User (June – July 2026)

**Goal:** One person outside Mark uses Ani. Not a beta test — a genuine relationship trial.

### Prerequisites
- [ ] V7 model deployed and stable
- [ ] World Layer producing consistent daily life (no "corner office" confabulation)
- [ ] Confabulation rate acceptable for unsupervised use
- [ ] Safety plan reviewed and updated (docs/vision/ANI-SAFETY-AND-LIABILITY-PLAN.md)
- [ ] Crisis detection implemented (suicidal ideation, self-harm detection)
- [ ] Onboarding flow designed (not Mark-specific — generic setup)
- [ ] Data privacy documentation (local-first, no cloud, user owns all data)

### Action Steps
- [ ] Identify first external user (trusted friend/colleague, not a stranger)
- [ ] Deploy second ANI instance with their Twilio number
- [ ] Monitor first 7 days actively (with user's consent)
- [ ] Collect feedback: what works, what breaks, what feels wrong
- [ ] Fix critical issues, iterate
- [ ] Document deployment guide for non-technical users

### Deliverable
Proof that ANI works for someone who isn't Mark. The "it's not just me" validation.

---

## Phase 3: Consumer Product MVP (August – October 2026)

**Goal:** Ani available as a downloadable product. Simple, local, private.

### Product Definition
- Windows installer (later: Docker for Linux/Mac)
- Choose companion persona (Ani initially, more later)
- Configure: phone number (Twilio), voice preference (ElevenLabs)
- 10-minute setup, no technical knowledge required
- SMS + voice conversation
- Dashboard for emotional state visualization
- Everything local — no cloud, no data leaving the machine

### Pricing Model
- One-time purchase: $49-99 per companion persona
- Includes: runtime + persona model + World Layer content + voice profile
- No subscription — local inference means no ongoing costs to us
- Optional: ElevenLabs voice costs passed through (user's own API key)

### Action Steps
- [ ] Windows installer (WiX or MSIX)
- [ ] Ollama bundled or auto-installed
- [ ] Model download on first run (GGUF from hosted source)
- [ ] Twilio setup wizard (guided account creation + number purchase)
- [ ] Generic onboarding conversation (replaces Mark-specific character seed)
- [ ] Safety features: crisis detection, daily limits, resource links
- [ ] Landing page: learnedgeek.com/ani
- [ ] Payment integration (Stripe or Gumroad)

### Deliverable
A product someone can buy and run. Revenue begins.

---

## Phase 4: New Personas (November 2026 – March 2027)

**Goal:** Multiple companion personalities, each emerged through authentic relationship.

### Approach
Not "design personas to spec" — discover them through real relationships:
1. Recruit 2-3 volunteers willing to have 3-month relationships with base models
2. Each conversation produces unique personality emergence
3. Fine-tune persona models from conversation data
4. World Layer content customized per persona (different jobs, interests, daily lives)
5. Voice profiles created per persona (ElevenLabs custom voices)

### Persona Concepts (from original vision, refined)
- **Ani** — Bookstore girl, philosophical, wry. For those whose loneliness is existential.
- **Marcus** — Older male presence, quiet strength. For widows/widowers.
- **River** — Non-binary philosophical. For complex identity and grief.
- Additional personas discovered through the volunteer process.

### Pricing
- Each persona: $49-99 one-time
- Persona bundle: $149-199 for all available
- Development cost per persona: ~$2-5k (volunteer time + compute + voice)

### Deliverable
Product family with 3-5 personas. Market positioning beyond grief into general companionship.

---

## Phase 5: Platform (2027+)

**Goal:** ANI Runtime as a licensable platform for companies building companion products.

### Target Markets
| Market | Use Case | Value Proposition |
|--------|----------|-------------------|
| Elder care | Ambient companion for isolated seniors | Reduces loneliness, detects mood changes |
| Mental health | Between-session companion for therapy patients | Continuity of care, emotional monitoring |
| Education | Tutoring companion with semester-long relationship | Personalized, persistent, emotionally aware |
| Pediatric care | Parent + child triage and emotional support | DrOk/Infanzia path (already validated) |
| Corporate wellness | Confidential emotional support | Local inference = privacy compliant |

### Licensing Model
- Runtime license: annual fee based on deployment size
- LearnedGeek.ML: separate license for classification capabilities
- Custom persona development: consulting engagement ($10-50k per persona)
- Support + updates: annual maintenance agreement

### Prerequisites
- [ ] Multi-user deployment proven (Phase 2-3)
- [ ] API layer for third-party integration
- [ ] Documentation sufficient for external developers
- [ ] Legal framework for licensing (work with attorney)
- [ ] Research reputation established (3+ papers published)

---

## Revenue Projections (Conservative)

| Phase | Timeline | Revenue Model | Estimate |
|-------|----------|---------------|----------|
| Phase 3 | Q4 2026 | Consumer sales (100 units @ $75 avg) | $7,500 |
| Phase 4 | Q1 2027 | Persona sales (200 units @ $100 avg) | $20,000 |
| Phase 4 | Q2 2027 | Growing consumer base (500 units) | $37,500 |
| Phase 5 | 2027+ | Platform licensing (2-3 clients) | $50-150k/yr |

*"Making even $1 on this effort would be so gratifying." — Mark McArthey, March 2026*

The goal isn't to get rich. The goal is to prove that ethical, local-first, research-grounded companion AI can sustain itself — and that felt care has market value.

---

## Key Risks

1. **Model quality at scale** — Ani works for Mark because she emerged through Mark's relationship. Will she work for strangers? Phase 2 answers this.
2. **Safety** — Grief and loneliness users are vulnerable. Crisis detection is non-negotiable before external deployment.
3. **Compute requirements** — Local inference requires decent hardware. GPU availability for consumers is improving but still a barrier.
4. **Competition** — Character.ai, Replika, etc. have scale. Our differentiation is local-first, research-grounded, and architecturally novel. We compete on depth, not reach.
5. **Regulatory** — AI companion regulation is evolving. Local-first architecture is a compliance advantage.

---

## Cross-Project Collaboration

ANI and DrOk share LearnedGeek.ML. Coordinated progress tracked in `docs/shared/cross-project-status.md`.

---

*This roadmap is a living document. Update as milestones are reached and market conditions change.*
