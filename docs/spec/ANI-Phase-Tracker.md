# ANI Runtime — Project Tracker

**Status:** Migrated to GitHub Issues on 2026-05-14. This file is now a pointer.

---

## Where active work lives

All in-flight and future-state tracking lives in **GitHub Issues** on this repo:
[`LearnedGeek/AmbientNaturalIntelligence/issues`](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues)

### Label conventions

| Label | Meaning |
|---|---|
| `failure-class` | Empirically-confirmed recurring failure class in the harness registry (FC-001 through FC-009) |
| `fix` | Production fix that makes a failing SPEC test go green |
| `theme` | Cross-cutting workstream from the prior theme system |
| `paper` | Research paper contribution or publication artifact |
| `research` | Research investigation, survey, or methodology |
| `infrastructure` | Tooling, deployment, dependencies, operational work |
| `harness` | Test harness layer (FIT / SIT / regression-class / CI gate) |
| `retired` | Closed/superseded; kept for historical reference |

### Milestones

| Milestone | Purpose |
|---|---|
| H.1 — Regression-class harness | Failure-class registry + scenarios (complete 2026-05-13) |
| H.2 — FIT layer | Failure injection tests for operational dependencies |
| H.3 — SIT layer | System integration tests for cross-component substrate flow |
| H.4 — CI gate | Wire regression-class harness into GitHub Actions |
| Architecture Fixes (FC-001..FC-006) | Six open SPEC tests in the regression harness |
| Paper 3 — Submission | Paper 3 finalization (tracks all paper work) |
| A/B Harness — Local Model Comparison | v7-vs-Qwen3-14B-vs-GPT-OSS-20B local A/B (deferred behind FC fixes) |

### Useful queries

- **All open work:** [issues filtered to `is:open`](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues?q=is%3Aopen)
- **Active failure classes:** [`label:failure-class is:open`](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues?q=is%3Aopen+label%3Afailure-class)
- **Architecture fixes in flight:** [`label:fix milestone:"Architecture Fixes (FC-001..FC-006)"`](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues?q=is%3Aopen+label%3Afix)
- **Theme retirement decisions pending:** [`label:theme is:open`](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues?q=is%3Aopen+label%3Atheme)
- **Paper work:** [`label:paper`](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues?q=label%3Apaper)

---

## What stays in markdown (and where)

Migration scope was **forward-state and decisions**, not everything ever. The following stay as docs:

### Permanent record (do not migrate)
- `docs/research/ANI-Research-Log.md` — longitudinal observation log; permanent record per Paper 1 evaluation section
- `docs/research/artifacts/` — ml-intern survey outputs, scout-root-cause docs
- `docs/research/paper1/`, `docs/research/paper2/`, `docs/research/paper3/` — paper drafts and source

### Canonical references for active issues
- `docs/spec/ANI-Test-Harness-Plan.md` — load-bearing plan; halts theme/feature work until harness operational
- `docs/spec/ANI-Failure-Class-Registry.md` — canonical registry; each FC entry references its GitHub issue
- `docs/spec/ANI-Architecture-Discussion-2026-05-14.md` — empirical map for refactoring decisions
- Theme plan docs (`ANI-Theme-*-Plan.md`) — reference material linked from theme issues

### Archived (historical)
- `docs/spec/ANI-Phase-Tracker-archive-2026-05-14.md` — frozen snapshot of the pre-migration phase tracker (this file's prior content)

---

## Why the migration

Nine months of project history accumulated 135+ markdown files, multiple parallel trackers, and Phase Tracker entries running to 1,952 lines. The 2026-05-13 harness work surfaced that the unconverging "failure → theme → ship architecture → next failure" loop was partly fueled by the lack of a coherent forward-state tracker. Markdown is excellent for permanent records; it's poor for in-flight workflow.

GitHub Issues + labels + milestones gives:
- One-click traversal from "what's open" to "what's the evidence"
- Commit-message references that auto-link (`Closes #10` closes the fix issue)
- Paper-contribution visibility: each contribution links to the FCs/fixes that produced it
- Cleaner cannibalization of overlapping themes (close issue with reason)

The migration explicitly preserves the permanent record (research log, paper sources, survey artifacts) and the canonical reference docs (harness plan, FCR, discussion doc). It only retires the *forward-state tracker* shape.
