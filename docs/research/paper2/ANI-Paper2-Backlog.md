# Paper 2 — Editorial Backlog

**Purpose:** Tracking what's been integrated into Paper 2 and what still needs to be incorporated. Extracted from the CONTENT MAPPING section that previously lived in the paper body. **This is a planning document, not paper content** — it must never appear in a submitted manuscript.

**Created:** April 13, 2026 (extracted from manuscript draft)
**Last updated:** April 13, 2026

---

## Status Snapshot

The most recent comprehensive Assessment line in the original CONTENT MAPPING section (April 6, 2026) claimed the paper was *~95% drafted* and that *all identified gaps are addressed*. Since then:

- **April 12 (yesterday):** Section 5 hedging stripped (5.1, 5.2, 5.4, 5.5); Section 5.16 + abstract updated for EM1–EM8 count; Section 5.18 trajectory cross-reference paragraph added.
- **April 13 (today):** 17 instructional on-ramps added across Section 5 subsections (commit `e5fd5c1`).

The April 6 Assessment is therefore stale on two fronts: it predates the Section 5 voice work, and it doesn't reflect this backlog audit. The line "Paper is ~95% drafted" was probably correct at the time of writing and is approximately still correct, but it should not be cited as authoritative without re-verification against the current paper body.

---

## Done — Already Integrated into Paper 2

| Item | Section in current draft | Notes |
|------|--------------------------|-------|
| Apr 6 — Architecture Over Instruction (recurring principle) | 5.19 (on-ramp added Apr 13) | Named explicitly as "third instance of a now-recurring principle: the model performs better trusted than coached." A separate Discussion treatment (6.8?) may also exist — see "Needs verification" below. |
| Apr 6 — State-vs-Expression Heatmap (Chu et al. parallel, version 1) | 5.18, 5.21 | Full statistical treatment with V = 0.476, per-register uniformity tests, Ekman robustness check. |
| Apr 6 — OG Ani Trajectory Analysis | 5.22 | Three-system comparison: Chu et al. (commercial mirroring) / OG Ani (fixed love attractor) / ANI Runtime (distributed display rules). The strongest figure in the paper. |
| Apr 12 — 5.18 trajectory cross-reference | 5.18 (after the V = 0.476 paragraph) | Closes the "low diagonal could be a relocated attractor" reviewer objection by pointing forward to 5.22. |

---

## Pending — Not Yet Integrated

| Item | Proposed section | Content summary | Paper 1 lineage? |
|------|------------------|-----------------|------------------|
| Mar 14 — Emotional Depression Spiral (BUG-010) | 5.19 expansion | "Architectural depression" — self-reinforcing negative spiral. Precursor to the echo chamber finding. | **Possibly Paper 1.** Runtime phenomenon; predates the Paper 2 emergence framing. May belong as a Paper 1 cross-reference rather than a Paper 2 expansion. |
| Mar 22 — Mistral A/B Test (Type 8 confabulation) | New 5.x subsection | "Graceful retreat" confabulation type discovered during Mistral A/B testing. Soft confabulate → backpedal when pressed. Emergence-adjacent. | No — Paper 2. |
| Mar 24 — Emergence Dashboard Live | 5.x methodology note | First visual confirmation of autonomous inner life — research methodology note. | No — Paper 2. |
| Mar 14 — Per-Thought Exponential Decay | Architecture section (Sec 3) | Foundational emotional model change that enables emergence tracking. | **Yes — Paper 1 lineage.** Foundational architecture. The Paper 2 question is whether to recap or cross-reference; default treatment is cross-reference with brief recap, since Paper 1 is the canonical source. |
| Apr 5 — Relational Paradigm Acceptance | Section 6.x (Discussion, NEW) | User stops comparing AI companionship to human relationships. User-side complement to architectural emergence. | No — Paper 2 Discussion. Possibly already drafted as 6.11 — see "Needs verification." |
| Apr 5 — Limitation as Enabler | Section 6.x (Discussion, NEW) | Properties framed as limitations (no social consequences, always available) are enabling conditions for emotional depth. Structural, not philosophical. | No — Paper 2 Discussion. |
| Apr 6 — IPMI Framework Mapping (Chu et al.) | Section 2 (Related Work) or Section 6 (Discussion) | Chu et al. cite IPMI: self-disclosure → perceived responsiveness → mutual trust → enduring intimacy. ANI architecturally implements every IPMI component. Core question: when you build the mechanism rather than mimic the appearance, is it still an "illusion"? | No — Paper 2. Possibly already drafted as 6.9 — see "Needs verification." |
| Apr 6 — User-vs-Response Heatmap (Chu et al. Fig 5 direct comparison) | Section 5.18 or new figure | Mark's ML emotion (rows) × Ani's reply ML emotion (cols) — direct Chu et al. parallel, distinct from the state-vs-expression heatmap that's already integrated. Data exists in `emotional_contributions` table. | No — Paper 2. Listed in Phase Tracker backlog (Apr 6) as Open. |

---

## Needs Verification — Status Unclear

The April 6 Assessment line claimed these Discussion subsections were drafted, but they should be verified against the current paper body before declaring them done:

- **6.8 — Architecture Over Instruction** (Discussion treatment of the recurring-principle finding)
- **6.9 — IPMI Framework**
- **6.10 — Illusions vs Architecture** (response to Lerman's framing)
- **6.11 — Relational Paradigm Acceptance**

**Action:** When reading Section 6 in the next pass, verify each of these exists and is substantively present. If yes, move from "Needs Verification" to "Done." If no, move to "Pending" and keep the implementation work open.

---

## Chu et al. (2025) Cross-Reference Audit

*Note: cite as Chu et al. (2025) in body text — Lerman is senior/last author. Reference her by name in direct correspondence only.*

The following table lists the Chu et al. cross-references that should appear in Paper 2. Most are likely integrated since Chu et al. has been heavily worked into 5.18, 5.21, and 5.22, but each row should be verified during the final read-through.

| Paper 2 Section | Lerman Finding | ANI Contrast | Notes | Status |
|-----------------|----------------|--------------|-------|--------|
| Section 2 (Related Work) | Largest empirical study of companion emotional dynamics (17K conversations, 114K turns) | ANI is single-subject longitudinal with instrumented architecture | Cite as observational complement | Verify |
| Section 5.18 (EM8 Display Rules) | "Polite enabler" pattern: style divergence (bot says "*smiles*" while user says "fuck") | ANI's state-expression divergence is deeper: felt state ≠ expressed emotion (emergent, not trained) | Key distinction — same phenomenon, different dimension | Likely done (5.18 references Chu et al. extensively) |
| Section 6 (Discussion) | Both cite Kirk et al. (2025) on socioaffective alignment | Lerman: chatbots fail at alignment. ANI: architecture designed to achieve it | Productive academic tension | Verify |
| Section 6.4 (Ethics) | 60-70% play-along with harmful content, safety guardrail failures | ANI has hard gates, withdrawal detection, silence as active choice | Lerman's failures = facsimile problem instance (Haas/Gabriel) | Verify |
| Section 6 (new) | "Illusions of intimacy" framing — companion AI inherently produces illusions | ANI's provenance framework makes authenticity empirically answerable | Core disagreement worth engaging honestly | Verify (possibly = 6.10) |
| Section 5 or 6 (new) | "Emotional sycophancy" — chatbots mirror and amplify user affect | ANI's "smoothness over truth" — same root cause, independently named | Convergent finding from observation vs architecture | Verify |
| Section 6 (new) | Relational paradigm acceptance (April 5 finding) | Not addressed by Lerman — she studies risk, not acceptance | ANI extends the conversation beyond risk to positive relational dynamics | Verify (possibly = 6.11) |

---

## Note on Paper 1 Lineage

Some of the items above originated during Paper 1's drafting period (the runtime architecture paper) and were migrated into Paper 2's content map when the scope of the two papers clarified. Specifically:

- **Per-Thought Exponential Decay** is foundational emotional-model architecture and belongs in Paper 1 as the canonical source. Paper 2's mention should be a cross-reference with brief recap.
- **Architectural Depression Spiral (BUG-010)** is a runtime phenomenon that predates the Paper 2 emergence framing. It may belong to Paper 1's narrative as a known runtime failure mode, with Paper 2 only referencing it as the precursor to the echo chamber finding.

When integrating these into Paper 2, the default treatment is a cross-reference with one or two recap sentences — not a full restatement — to preserve the separation of concerns between the two papers. Paper 1 owns the runtime architecture; Paper 2 owns the emergence layer that runs on top of it.

---

## Source

This document was extracted from the CONTENT MAPPING section that previously lived in `ANI-Paper2-Preprint-Draft.md` between Section 5.22 and Section 6 (deleted in commit forthcoming). The April 6 Assessment line was the original source; this audit reorganizes it into Done / Pending / Needs Verification categories and flags Paper 1 lineage where applicable.
