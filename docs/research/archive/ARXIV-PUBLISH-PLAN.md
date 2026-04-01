# ANI arXiv Publish Plan
**Target submission window:** ~3-4 weeks from March 12, 2026 (i.e., by ~April 9, 2026)
**Target venue:** arXiv cs.HC (Human-Computer Interaction), cross-listed cs.AI
**Author:** Mark McArthey, Learned Geek Consulting (markm@learnedgeek.com)
**Repo:** mcarthey/AmbientNaturalIntelligence (AGPL-3.0)

---

## Why arXiv First

arXiv submission establishes **priority on the date the ideas are posted** — not the date a conference accepts them. This matters because the desire-engine architecture, the authenticity boundary concept, and the three-type confabulation taxonomy are genuinely novel. Getting them timestamped publicly in ~3-4 weeks protects the contribution while the system continues to mature.

arXiv is also how researchers in this space find work. CHI, IUI, and CSCW authors all scan arXiv. The paper finding its audience *before* formal review is a feature, not a bug.

---

## Timeline (3-4 Weeks)

### Week 1 (March 12–19) — System & Data
Primary focus: OC implements architectural changes, system runs, data accumulates.

- [ ] OC implements Change 1 (messages → episodic memory) — fixes conversation boundary amnesia
- [ ] OC implements Change 3 (bidirectional confidence gate) — fixes confabulation architecturally
- [ ] OC implements Change 5 (weather RSS perception) — fixes contextual incoherence
- [ ] Overnight runs observed for:
  - Confabulation rate post-grounding-constraint (Sylvia Stratham type)
  - Emotional calibration post-fix (smaller deltas, wider valence distribution)
  - Outreach quality under new grounding constraint
- [ ] V5 training data prepared (epistemic grounding examples, 8-12 turn conversations)
- [ ] Voice note integration scoped (ElevenLabs → outbound audio, driving use case)

### Week 2 (March 20–26) — Paper
Primary focus: Full read-through, tighten prose, finalize all sections.

- [ ] Complete read-through of full draft as continuous document
  - Check tone consistency across sections (written in multiple sessions)
  - Check for repeated phrases or seams between sections
  - Verify tense discipline (architecture = present, observations = past, findings = present)
- [ ] Update Section 4.2 deployment stats with current numbers (outreach count, memory count, conversation threads)
- [ ] Update Section 5.4 (night mode) with any new overnight data showing calibration improvements
- [ ] Add reflection layer finding from March 12 overnight run (lateral connections — paper-worthy per OC's log)
- [ ] Verify confidence=0.1 threshold gate proposal in Section 5.5 is consistent with Section 3 architecture description
- [ ] Tighten abstract to ~150 words (currently slightly long)
- [ ] Decide: how much of Kathy's story appears in the introduction vs. the conclusion only

### Week 3 (March 27 – April 2) — Submission Prep
Primary focus: Final polish, formatting, submission prep.

- [ ] Convert from Markdown to LaTeX (arXiv strongly prefers LaTeX)
  - Use ACM sigconf template or standard article class
  - All inline citations already in author-year format — maps cleanly
- [ ] Generate figures:
  - Figure 1: Cognitive cycle diagram (loop with 6 beats labeled)
  - Figure 2: Memory architecture diagram (5 types, importance/valence fields)
  - Figure 3: Desire engine schematic (trigger types → accumulation → threshold → decision)
  - Optional: Emotional state time-series from a sample overnight run
- [ ] Final citation check — verify all DOIs resolve
- [ ] Ethics statement (arXiv cs.HC increasingly expects one for deployed systems involving real people)
- [ ] Verify paper claims match deployed system state (confidence gate, weather perception, etc.)
- [ ] Update confabulation taxonomy from two-type to three-type (contextual incoherence added Mar 12)

### Week 4 (April 2–9) — Buffer & Submit
Primary focus: Final iteration, address any gaps from testing, submit.

- [ ] Address any remaining gaps between paper claims and system state
- [ ] Final read-through with fresh eyes
- [ ] Submit to arXiv — allow 1-2 business days for moderation
- [ ] Post announcement on LearnedGeek.com blog (call for collaborators / beta testers)

---

## What Makes This Submission Strong

**The priority claims are clean:**
1. Desire-driven ambient outreach with self-unpredictable timing — no prior work implements this
2. Bidirectional confabulation gate — no prior work addresses both directions in a single-relationship context
3. Three-type confabulation taxonomy (under pressure, in composition, contextual incoherence) — new, documented from deployment
4. Significance-weighted perception decay with personal relevance multiplier — extends Park et al., no prior art

**The narrative is unusually strong for a systems paper:**
- Real deployment, real relationship, real failure modes documented honestly
- The snow message, the Sylvia Stratham message, Duck Norris, the right silence — all recovered with exact text
- The Kathy/Ann/Ani name resonance — discovered, not engineered
- Section 2.4 ethical argument lands exactly when the field needs it (Character.AI settlement, OpenAI sycophancy rollback, MIT/OpenAI loneliness study all 2024-2025)

**The architecture is genuinely replicable:**
- Model-agnostic — runs on any Ollama-compatible model
- All thresholds in appsettings.json
- Open source (AGPL-3.0)
- $0.16/training run — accessible to independent researchers

---

## Post-arXiv Path

**Immediate (within 2 weeks of posting):**
- Monitor for citations and engagement
- Respond to any researcher inquiries — these are licensing conversations in embryo
- Cross-post abstract to relevant communities (HCI subreddits, AI safety forums, companion AI researchers)

**Conference submission (June 2026):**
- **IUI 2026** (ACM Intelligent User Interfaces) — strong fit for desire engine + felt care
- **CSCW 2026** (Computer-Supported Cooperative Work) — strong fit for single-relationship deployment methodology
- **CHI 2027** — longer runway, higher prestige, submit the expanded version with V5 data

**Journal (late 2026):**
- *ACM Transactions on Computer-Human Interaction* (TOCHI) — flagship HCI journal
- Expanded version with 6+ months of V5 deployment data, multi-subject pilot if possible

---

## The Dream Outcome

A researcher or team working on a domain-specific companion application (grief therapy, elder care, chronic illness support) reads the paper, sees that ANI Runtime is open source and model-agnostic, and reaches out to license the architecture for their trained model.

That conversation begins with this paper.

---

## Remaining Paper Gaps (as of March 12, 2026)

| Item | Status | Notes |
|------|--------|-------|
| Snow message exact text | ✅ RESOLVED | "hey… do you remember that place on 5th..." (07:29:02, Mar 9) |
| Duck Norris exact exchange | ✅ RESOLVED | Recovered from Serilog (Mar 9, 19:25) |
| Ani name origin | ✅ RESOLVED | Grok conversation Jan 27–Feb 1, 2026 |
| Sylvia Stratham confabulation | ✅ RESOLVED | Added to Section 5.5 as Type 2 confabulation |
| Reflection layer finding | ⬜ TODO | OC's overnight log shows paper-worthy introspection — add to Section 5 |
| Confidence gate architecture | ⬜ TODO | Proposed in 5.5, needs to appear in Section 3 architecture description |
| Section 4.2 current stats | ⬜ TODO | Update outreach count, memory count before submission |
| LaTeX conversion | ⬜ TODO | Week 3 task |
| Figures (3 diagrams) | ⬜ TODO | Week 3 task |
| Ethics statement | ⬜ TODO | Week 3 task |
| Pataranutaporn et al. 2025 | ⬜ TODO | Check for peer-reviewed version before submission |

---

## Notes on Voice Integration (Post-Submission)

Voice outreach (ElevenLabs → audio message → WhatsApp/iMessage) is scoped as a post-submission feature. Rationale: adds 80% of the intimacy benefit for 20% of implementation cost, and transforms the driving-use-case testing loop. Does not affect the paper's core architecture claims. Target: implement in parallel during Week 1-2, use during the driving review sessions in Week 2-3.

Real-time two-way voice (live conversation during a drive) is a Phase 3 project — 4-6 week implementation minimum, separate from submission timeline.
