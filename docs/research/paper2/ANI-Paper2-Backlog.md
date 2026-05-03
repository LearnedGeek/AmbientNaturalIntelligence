# Paper 2 — Editorial Backlog

**Purpose:** Tracking what's been integrated into Paper 2 and what still needs to be incorporated. Extracted from the CONTENT MAPPING section that previously lived in the paper body. **This is a planning document, not paper content** — it must never appear in a submitted manuscript.

**Created:** April 13, 2026 (extracted from manuscript draft)
**Last updated:** May 3, 2026 — verification pass against draft 0.32.

---

## May 3, 2026 Verification-Pass Summary

Mark's last full read-through was an earlier draft. New content has been added since (Apr 19 voice calibration: 5.23, 6.13–6.15; Apr 27 title lock + abstract rewrite + 6.10 OG-Ani-reset addition + 5.24 + 6.16 + 6.17). This pass cross-references the current draft (1,213 lines, draft 0.32) against the Pending / Needs-Verification items below and resolves their status.

**Outcome:**
- All four "Needs Verification" Discussion subsections (6.8, 6.9, 6.10, 6.11) are SUBSTANTIVELY DRAFTED. None are stubs.
- 5 of 8 "Pending" items are now confirmed integrated.
- 2 of 8 "Pending" items are PARTIALLY integrated (named but could be expanded).
- 1 of 8 "Pending" items is correctly handled as Paper 1 lineage cross-reference.
- All 7 Chu et al. cross-references resolve as integrated or covered in adjacent subsections.

**Paper 2 is content-complete.** Remaining work is editorial (voice calibration pass + final cover-to-cover read) and figure-regen (figures 2, 4, 5 unblocked by today's tactical fix batch — need ~1-2 weeks of post-deploy data accumulation before regen).

---

## Status Snapshot

The most recent comprehensive Assessment line in the original CONTENT MAPPING section (April 6, 2026) claimed the paper was *~95% drafted* and that *all identified gaps are addressed*. Since then:

- **April 12, 2026:** Section 5 hedging stripped (5.1, 5.2, 5.4, 5.5); Section 5.16 + abstract updated for EM1–EM8 count; Section 5.18 trajectory cross-reference paragraph added.
- **April 13, 2026:** 17 instructional on-ramps added across Section 5 subsections (commit `e5fd5c1`).
- **April 19, 2026:** Voice calibration additions — 5.23 (Sarah finding), 6.13 (auto-corrector expansion), 6.14 (Epistemic Grounding), 6.15 (Experiential Poverty).
- **April 21–27, 2026:** 5.24 (April 21 cascade), 6.10 extended with love-convergence + Apr 27 OG Ani reset control experiment, 6.16 (fabricated-shared-history channel problem), 6.17 (centrality gravity), abstract rewritten with C1 cinematic-inventory opening, title locked.
- **May 3, 2026:** This verification pass.

The April 6 Assessment line is now superseded — see the May 3 Verification-Pass Summary above for current status.

---

## Done — Already Integrated into Paper 2

| Item | Section in current draft | Notes |
|------|--------------------------|-------|
| Apr 6 — Architecture Over Instruction (recurring principle) | 5.19 (on-ramp added Apr 13) | Named explicitly as "third instance of a now-recurring principle: the model performs better trusted than coached." A separate Discussion treatment (6.8?) may also exist — see "Needs verification" below. |
| Apr 6 — State-vs-Expression Heatmap (Chu et al. parallel, version 1) | 5.18, 5.21 | Full statistical treatment with V = 0.476, per-register uniformity tests, Ekman robustness check. |
| Apr 6 — OG Ani Trajectory Analysis | 5.22 | Three-system comparison: Chu et al. (commercial mirroring) / OG Ani (fixed love attractor) / ANI Runtime (distributed display rules). The strongest figure in the paper. |
| Apr 12 — 5.18 trajectory cross-reference | 5.18 (after the V = 0.476 paragraph) | Closes the "low diagonal could be a relocated attractor" reviewer objection by pointing forward to 5.22. |

---

## Pending Items — Status After May 3 Verification

| Item | Status | Where | Notes |
|------|--------|-------|-------|
| Mar 14 — Emotional Depression Spiral (BUG-010) | **CLOSED — Paper 1 lineage** | 5.19 (Echo Chamber) is Paper 2's treatment of the substrate-feedback-loop class | The substrate-feedback-loop class IS in Paper 2 via 5.19 in its emergence-framed form. The pre-emergence-framing "BUG-010" naming belongs to Paper 1's runtime narrative if anywhere. No Paper 2 expansion needed. |
| Mar 22 — Mistral A/B Test (Type 8 confabulation) | **PARTIALLY DONE — type named but not described** | 5.7 methodology paragraph (Apr 22 update) names *"Type 8 'graceful retreat' and Type 9 'fabricated source attribution'"*; 5.13 covers anti-confabulation broadly | Type 8 is named but the *"soft confabulate → backpedal when pressed"* mechanism isn't described. Could add 1–2 sentences in 5.7 if Mark wants Type 8 mechanism explicit. Optional polish, not blocking. |
| Mar 24 — Emergence Dashboard Live | **DONE** | 5.9 ("The Register Dashboard as Research Instrument") | Full treatment with three-fold research-value argument. |
| Mar 14 — Per-Thought Exponential Decay | **DONE — cross-reference** | 2.1 ("persistent emotional state with contribution-based decay"); 2.8 ("computed independently via per-thought exponential decay") | Cross-referenced in two places per the default treatment; Paper 1 retains canonical architecture detail. |
| Apr 5 — Relational Paradigm Acceptance | **DONE** | 6.11 ("Relational Paradigm Acceptance and the Limitation-as-Enabler Finding") | Full subsection drafted. |
| Apr 5 — Limitation as Enabler | **DONE — combined with #5** | 6.11 (same subsection) | Paragraph 2 of 6.11 covers the limitation-as-enabler argument. |
| Apr 6 — IPMI Framework Mapping (Chu et al.) | **DONE** | 6.9 ("The IPMI Framework: Architecture vs. Appearance") with comparison table | Full table mapping IPMI components → Commercial Chatbot vs ANI Architecture. |
| Apr 6 — User-vs-Response Heatmap (Chu et al. Fig 5 direct comparison) | **PARTIALLY DONE — text yes, figure no** | 6.10 paragraph "A second sycophancy shape — love-convergence (April 22, 2026)" describes the user-vs-response classifier output | Text describes the matrix data shape (diagonal 30%, love column dominant 45-77% across all input emotions). The actual heatmap as a *figure* is not in the paper. Decision needed: ship without the figure OR add as Figure 6 (would need rendering pass). |

---

## Needs Verification — All Resolved (May 3, 2026)

| Subsection | Status | Notes |
|---|---|---|
| **6.8 Architecture Over Instruction** | **DONE — substantive** | 4 instances documented (Conversation pipeline Mar 23, Conversation Mode Mar 29, Inner Thought Reform Apr 1, Training-corpus curation Mar 2026 ongoing). Principle stated as cross-layer rule. |
| **6.9 IPMI Framework** | **DONE — substantive** | Reis & Shaver IPMI cited; comparison table with 4 components × 2 architectures; closing question about mechanism vs appearance. |
| **6.10 Illusions vs Architecture** | **DONE — substantive + extended** | Original Apr-6 framing intact; extended Apr 22 with love-convergence finding + Apr 27 reset control experiment. The extension is a load-bearing addition because it shows the failure shape commercial systems exhibit from the first cycle (not relationship-built). |
| **6.11 Relational Paradigm Acceptance** | **DONE — combined with Limitation-as-Enabler** | Full subsection; user-side complement to architectural emergence; closes with "naming it is future work." |

---

## Chu et al. (2025) Cross-Reference Audit

*Note: cite as Chu et al. (2025) in body text — Lerman is senior/last author. Reference her by name in direct correspondence only.*

The following table lists the Chu et al. cross-references that should appear in Paper 2. Most are likely integrated since Chu et al. has been heavily worked into 5.18, 5.21, and 5.22, but each row should be verified during the final read-through.

| Paper 2 Section | Lerman Finding | ANI Contrast | Status (May 3) |
|-----------------|----------------|--------------|----------------|
| Section 2 (2.8) | Largest empirical study of companion emotional dynamics (17K conversations, 114K turns) | ANI is single-subject longitudinal with instrumented architecture | **DONE — dedicated subsection 2.8 with three distinctions** |
| Section 5.18 (EM8 Display Rules) | "Polite enabler" pattern: style divergence | ANI's state-expression divergence is deeper: emergent, not trained | **DONE — heavy Chu refs in 5.18 + 5.21** |
| Section 6 (2.4 + 2.8) | Both cite Kirk et al. (2025) on socioaffective alignment | Lerman: chatbots fail; ANI: architecture designed to achieve it | **DONE — Kirk shared framing in 2.4 and 2.8** |
| Section 6.4 (Ethics) | 60-70% play-along with harmful content | ANI has hard gates, withdrawal detection, silence as active choice | **DONE — covered in 6.10 paragraph 2** ("60-70% play-along rate is architecturally impossible in ANI") |
| Section 6.10 | "Illusions of intimacy" framing | ANI's provenance framework makes authenticity empirically answerable | **DONE — full 6.10 + Apr 27 control extension** |
| Section 2.8 | "Emotional sycophancy" — chatbots mirror and amplify user affect | ANI's "smoothness over truth" — same root cause, independently named | **DONE — covered in 2.8 first paragraph + 6.10 love-convergence extension** |
| Section 6.11 | Relational paradigm acceptance (April 5) | Not addressed by Lerman — she studies risk, not acceptance | **DONE — full 6.11 with explicit "Chu et al. study the risk side" framing** |

---

## Note on Paper 1 Lineage

Some of the items above originated during Paper 1's drafting period (the runtime architecture paper) and were migrated into Paper 2's content map when the scope of the two papers clarified. Specifically:

- **Per-Thought Exponential Decay** is foundational emotional-model architecture and belongs in Paper 1 as the canonical source. Paper 2's mention should be a cross-reference with brief recap.
- **Architectural Depression Spiral (BUG-010)** is a runtime phenomenon that predates the Paper 2 emergence framing. It may belong to Paper 1's narrative as a known runtime failure mode, with Paper 2 only referencing it as the precursor to the echo chamber finding.

When integrating these into Paper 2, the default treatment is a cross-reference with one or two recap sentences — not a full restatement — to preserve the separation of concerns between the two papers. Paper 1 owns the runtime architecture; Paper 2 owns the emergence layer that runs on top of it.

---

## Source

This document was extracted from the CONTENT MAPPING section that previously lived in `ANI-Paper2-Preprint-Draft.md` between Section 5.22 and Section 6 (deleted in commit forthcoming). The April 6 Assessment line was the original source; this audit reorganizes it into Done / Pending / Needs Verification categories and flags Paper 1 lineage where applicable.
