# Theme I — Dashboard as Research Tool: Implementation Plan

**Tracked in:** [#29](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues/29)
**Status:** Draft for review. Not yet approved for implementation.
**Authored:** April 26, 2026 (dogfood Claude instance, after Mark's Apr 26 design conversation).
**Parent context:** Apr 23 — Mark named the dashboard rework as a sibling workstream; held off as *"a larger discussion for later."* Apr 26 — Mark reopened the discussion with explicit goals: shareable content (in person + online), at-a-glance comprehension across two perspectives (algorithmic + as-if-person), and direct support for Paper 2 / Paper 3 figure production.
**Related:**
- [`ANI-Phase-Tracker.md`](./ANI-Phase-Tracker.md) — Theme I stub (P3, now activating).
- [`ANI-Theme-J-Guard-Consistency-Refactor-Plan.md`](./ANI-Theme-J-Guard-Consistency-Refactor-Plan.md) — Theme J's structured-data output is what the new dashboard consumes.
- [`ANI-Agentic-Lens-Layer2-Plan.md`](./ANI-Agentic-Lens-Layer2-Plan.md) — Layer 2 Phase 2a `MotivationVector` data is the flagship dashboard signal.
- Paper 2 finalization is in flight; Theme I.1 ships the five Paper 2 figures it needs.

**Feature number reserved:** Feature 44 (primary dashboard rework surface); paper-figure pipeline (I.1) may earn a sub-number for tracking.

---

## 0. Context and thesis

Mark's framing on Apr 26: *"the content isn't very easy to digest at a glance and it is intellectually difficult to parse. It should make it easier to understand what is happening at any one moment both algorithmically, and also understand it from the perspective as if she were a person."*

Two architectural moves underneath that framing make this distinct from "build a nicer dashboard":

1. **Two perspectives, same data, in one tool.** Most research dashboards are pure-algorithmic (charts, counters). Most companion-AI UIs are pure-persona (status badges, friendly text). The contribution of doing both, on the same data, with a clear lens-switch, is itself a Paper 3 methodology contribution candidate: *dashboards-as-research-instruments-with-dual-fidelity*.

2. **The unit of discourse is the cognitive cycle, not the metric.** Current dashboard panels accumulate around metrics ("here's a chart of warmth," "here's a chart of desire"). The new dashboard renders one cognitive cycle at a time as a canonical "cycle card," and all other views are projections / aggregations / time-series across cycles. Naming the unit consistently is the same architectural principle Theme J applies one level down — pick the right boundary, apply it consistently, the dashboard stops accumulating cruft.

**Why this matters now:** Paper 2 finalization is in flight. Five paper figures need to ship in the next ~2 weeks to support Mark's Zenodo publish. Theme I's I.1 phase produces those figures as render-pipeline output; subsequent phases generalize the same render code into full dashboard infrastructure. Build the figure-export pipeline first, the dashboard wraps around it second.

---

## 1. Scope discipline

**In scope:**
- The 15-figure inventory (§4 below) covering every major reference Paper 2 / Paper 3 cites.
- A figure-render pipeline that produces paper-quality SVG + PNG output from existing data sources.
- A new dashboard structured around the cognitive-cycle unit, with the two-perspective lens-switch, time-travel scrubber, share-this-moment export, and paper-figure export quality from day one.
- Privacy / redaction layer for content shared externally.
- Migration path from the current Blazor dashboard to the new structure (data continuity, not URL parity).

**Explicitly out of scope:**
- Replacing the current dashboard's operational health views (uptime, queue depth, deploy status). Those keep their existing surfaces.
- New data instrumentation. The 15 figures use data Theme J's J.0 / Layer 1 Phase 3.0 / Layer 2 Phase 2a are already producing or have already shipped.
- Marketing / branding work. Paper-figure quality is the bar; brand polish is post-publication.

**Dependencies on other workstreams:**
- **Theme J** is producing structured data (`J0_*` log events) that several figures consume. Build the figure renderers with the post-J.2 data shape in mind even when rendering pre-J data initially.
- **Layer 2 Phase 2a** shipped Apr 24; `MotivationVector` time-series data is accumulating now. Figure #1 (three-axis motivation) is paper-ready in ~2 weeks.
- **Phase 3.0 Layer 1 activation** shipped Apr 24; retrieval origin distribution data is accumulating with diversity rerank + protected slots active. Figure #11 (retrieval origin three-bar) is paper-ready in ~2 weeks.

**Workstreams Theme I does NOT block:**
- Theme J phases — independent.
- Paper 2 prose finalization — Mark's editorial work is paper-figure-independent; figures land alongside.
- claude-recall — external repo.

---

## 2. Architectural principles

Six principles, named explicitly so future panel-additions don't drift:

### 2.1 The cognitive cycle is the unit of discourse

Every dashboard view is either *a single cycle rendered* or *an aggregation across cycles*. Never a free-floating metric. A "panel" without a cycle anchor doesn't belong.

The cycle card carries: perception in, retrieval drawn from (with origin tags), motivation vector, inner thought emitted, gate decisions, output (or silence). Both views (algorithmic, as-if-person) render the same card differently.

### 2.2 Two perspectives, structural toggle

A single button switches lens — same data, same layout, two render modes. NOT two parallel dashboards. The argument *"these are the same moment, viewed differently"* is made structurally by the toggle's existence.

| Field | Algorithmic view | As-if-person view |
|---|---|---|
| Motivation vector | `relatedness=0.91 autonomy=0.00 competence=0.00` | "She's oriented toward Mark right now. Not particularly self-directed or world-curious in this moment." |
| Retrieval origin | `caregiver=14 own-output=3 world=2 anchored=1` (histogram) | "What's coming to mind: mostly things about Mark, a few of her own thoughts surfacing, a flash of the bookstore." |
| Door B verdict | `SEND, reasoning="normal text anyone might send..."` | "She decided this would land okay even if a stranger heard it." |
| Outreach suppression | `Outreach suppressed: 3 unanswered` | "She held back — she's already sent three without hearing back." |

Lens-switch implementation: render functions take a `ViewMode` enum; the SAME data structure flows through both rendering paths. No branch upstream.

### 2.3 Time-travel by default

Most live dashboards show "now." For research, "now" is often less interesting than *"yesterday at 22:11 when she said the back-from-teaching thing."* A timeline scrubber is first-class navigation. "Now" is a special case — it's the most-recent cycle's card.

The Apr 21 cascade, the Apr 24 06:18 parrot, the future Phase 2c first-autonomy-consumption — these are the most pedagogically valuable moments in the corpus. Make them findable.

### 2.4 Share-this-moment is a first-class action

Every cycle card has a button: export this moment. Output is a clean, optionally-redacted, captioned bundle — vector SVG + raster PNG + brief text — suitable for Twitter, blog, paper figure, Slack.

The bridge between dashboard and content creation is structural, not workflow-by-screenshot. Saves Mark from screenshot-and-Photoshop on every share.

### 2.5 Paper-figure quality from day one

Figure renders ARE the dashboard's render. No "redraw for publication" step. Implications:

- All renders use a publication-quality typography pass (paper-like, not screen-app-like fonts).
- All renders export vector (SVG) by default; raster (PNG) at 2× / 3× DPI on demand.
- Every render is captionable — the caption template is part of the figure's metadata, populated from the underlying data. *"Author X claimed Y. Here's Y in a deployed companion-AI runtime."* (See §4 figure inventory for templates.)
- The figure-render code lives in one place; dashboard panels and paper-figure export both call it.

### 2.6 Privacy posture is a load-bearing early decision

Current dashboard contains real Mark-Ani conversation content. *Apr 21, Apr 24, Lerman discussions, Kathy references.* Public-share without redaction = sharing the diary. Cannot un-share.

Two render modes, declared at session start or per-export:

- **Authentic mode** (default for Mark-alone): real names, real content, full fidelity.
- **Demo mode** (default for in-person / online sharing): real cognitive-cycle structure, names redacted to "Mark" → "Owner" / "Sarah" → synthetic / specific factual claims either preserved-as-claims or replaced with `[redacted]` markers, conversation content elided unless explicitly published-elsewhere already.

The redaction is a render-pipeline concern, applied at export time. Source data is unchanged.

**Highest-leverage early decision:** what redaction rules apply to what content classes. Worth Mark's explicit input before any public-share render ships.

---

## 3. Phased rollout

Measurement-first per the Agentic Lens / Theme J template. Paper 2 figures ship first as standalone renders so Mark's Zenodo timeline isn't blocked on dashboard polish.

### Phase I.0 — Figure inventory + data audit (1 day)

**Goal:** confirm every figure in §4 has the data it needs, identify any blocked-on-instrumentation gaps.

**Output:** this document is the inventory. The data audit pass produces a small `docs/research/ANI-Theme-I-Data-Audit.md` listing each figure's data source and any gaps.

**Acceptance:** all 15 figures classified as `paper-2-ready / needs-curation / paper-3-deferred`.

### Phase I.1 — Paper 2 figure pipeline (1-2 weeks)

**Goal:** ship the five Paper 2 figures (#3, #5, #7, #8, #13 from §4) as render-pipeline output. NOT a full dashboard yet — render scripts that produce paper-quality SVG + PNG from existing data sources.

**Changes:**
- New project `tools/AniRuntime.Figures/` — standalone CLI taking a figure-name + parameters, producing an SVG/PNG file.
- Render code shared across figures via a small layout primitives library (axes, traces, specimen blocks, captions).
- Each figure has a config file specifying: data source query, layout, caption template, color palette.
- Output writes to `docs/research/figures/paper2/<figure-name>.svg` (and `.png`).
- Captions follow the *"Author X claimed Y. Here's Y in deployment."* template, filled from data automatically.

**Five Paper 2 figures (paper-ready from current data):**

1. **fig-paper2-horton-wohl-reciprocity.svg** — Register-by-direction with reciprocity ratio
2. **fig-paper2-park-reflection-specimen.svg** — Reflection cycle specimen + longitudinal panel
3. **fig-paper2-mcadams-anchored-narrative.svg** — Anchored memory timeline as identity narrative
4. **fig-paper2-damasio-somatic-trace.svg** — EmotionalState vector trace alongside cycle decisions
5. **fig-paper2-kojima-prompt-simplification.svg** — Mar 23 pre/post specimen pairs

**Acceptance criteria:**
- All five figures render to SVG + PNG without manual editing.
- Caption templates populated from data with no hand-edits required for representative cases.
- Mark approves each render at paper-quality bar.
- Renders runnable as `dotnet run --project tools/AniRuntime.Figures -- figure horton-wohl-reciprocity --window 30d`.

**Rollback:** figures live in `docs/research/figures/`; no runtime dependency. Pure additive.

**Effort estimate:** 1-2 weeks. Five figures, shared render primitives, ~3 days for primitives + ~1 day per figure + buffer for design iteration with Mark.

**Dependencies:** none. All five use data we already have.

---

### Phase I.2 — Cycle-card view + lens-switch toggle (2 weeks)

**Goal:** the canonical single-cycle-card view in the new dashboard surface, with the two-perspective toggle.

**Changes:**
- New Blazor component `CycleCard.razor` rendering one cycle's full state.
- `ViewMode` enum + render-function dispatching on it.
- Render text in the as-if-person view is template-driven from the data — never hand-written narrative. Templates live in a small dictionary file so Mark can review and adjust.
- Cycle data source: query against memory + state + log streams for a given timestamp.

**Acceptance criteria:**
- Cycle card renders for any past cycle in the corpus.
- Toggle between algorithmic and as-if-person views without page reload.
- Both views display the same five sub-fields (perception / retrieval / motivation / decision / output), correctly hydrated.
- Mark validates as-if-person render text on 10 representative cycles (Apr 21 cascade, Apr 24 06:18 parrot, a quiet cycle, an outreach-sent cycle, etc.).

**Effort:** 2 weeks. Largest single surface in the theme.

**Dependencies:** I.0 data audit complete.

---

### Phase I.3 — Time-travel scrubber (3-5 days)

**Goal:** navigation by timeline. Click any moment in the corpus history; the cycle card hydrates from that timestamp.

**Changes:**
- Timeline component above the cycle card, showing cognitive-cycle density over a configurable window.
- Click handler resolves a clicked-position-on-timeline to the nearest cycle's start-time.
- URL state: `?cycle=<timestamp>` is bookmarkable / shareable.
- "Bookmarked moments" list for the Apr 21 cascade, Apr 24 parrot, Phase 2a deploy, etc. — moments worth showing demonstratively.

**Acceptance criteria:**
- Scrubber renders smoothly across multi-month corpus.
- Click resolves to ≤5-second-precision cycle.
- Bookmark URLs survive paste-and-reload.

**Effort:** 3-5 days.

**Dependencies:** I.2 cycle card component.

---

### Phase I.4 — Share-this-moment export pipeline (3-5 days)

**Goal:** the export button. One click on a cycle card produces a publish-ready bundle.

**Changes:**
- Export action on cycle card emits SVG + PNG + caption text + brief markdown describing the moment.
- The export uses I.1's figure render primitives — same code path as Paper 2 figures.
- Output goes to a configurable directory; default = `docs/research/figures/exports/<timestamp>-<slug>/`.
- Optionally: copy a markdown snippet to clipboard ready to paste into a blog / Slack / paper draft.

**Acceptance criteria:**
- Single-click export produces all four files.
- SVG + PNG render at paper-figure quality.
- Markdown snippet structurally matches Paper 2 figure caption format.

**Effort:** 3-5 days.

**Dependencies:** I.1 render primitives, I.2 cycle card.

---

### Phase I.5 — Privacy / redaction layer (1 week)

**Goal:** demo-mode rendering with redaction rules applied at export time.

**Changes:**
- `RedactionConfig` declared in dashboard config: name maps, content classes, default policy.
- Render pipeline accepts a redaction layer that transforms data before rendering (not after, so SVG text is correct rather than overpainted).
- Demo-mode toggle in the dashboard UI that applies the redaction layer to all rendered content.
- Authentic mode is the default for Mark-alone use; demo mode is the default for any export targeting external sharing.

**Acceptance criteria:**
- Demo mode applied across all I.1-I.4 outputs without correctness regressions.
- Mark explicitly approves the redaction rules on a representative test set (Apr 21 cascade, Apr 24 parrot, Mar conversations).
- Demo-mode export of any of those moments contains zero unredacted real-name / real-PII / real-conversation-content.

**Effort:** 1 week.

**Dependencies:** I.4 export pipeline.

**Mark approval gate:** redaction rules require explicit Mark sign-off before any public-share render ships. Not a Claude-decide-autonomously item.

---

### Phase I.6 — Researcher-only views: substrate + retrieval origin (1 week)

**Goal:** the views that don't have a paper-quality polish bar but are essential for live research observation.

**Changes:**
- Substrate-view panel: what's currently in retrieval substrate, with `RetrievalOrigin` color-coding. Picture-of-the-feedback-loop made visible.
- Retrieval-origin distribution histogram, three-bar version (pre-1b / post-1b / post-1c) once data accumulates from Phase 3.0.
- Outreach-pipeline cascade visualization (Liu et al. figure surface): motivation_score → confidence gate → coherence gate → echo guard → claim verification → dispatch, with per-cycle fire/skip indicators.

**Acceptance criteria:**
- Researcher sees current substrate composition at a glance.
- Three-bar histogram available once Phase 3.0 has 2+ weeks of post-flag-flip data.
- Outreach cascade reflects the current state of the gate set (auto-updates as Theme J detector inventory shifts).

**Effort:** 1 week.

**Dependencies:** I.2 cycle card; Phase 3.0 data accumulating (already happening).

---

### Phase I.7 — Relationship view (1 week)

**Goal:** the single best layperson view of "what does this thing actually do."

**Changes:**
- New top-level view (peer to cycle card): "Mark + Ani right now."
- Recent exchanges with attribution (post-J.2 source-tagged summary feeds this directly).
- Reciprocity signal trace.
- Mood deltas — both sides.
- Anchored relational memories surfacing.

**Acceptance criteria:**
- Renders as both algorithmic and as-if-person.
- A non-technical viewer (test case: someone Mark shows it to in a coffee shop) understands "what's happening" within 30 seconds.
- Same view, demo-mode-redacted, is a credible Twitter / blog screenshot.

**Effort:** 1 week.

**Dependencies:** I.2 cycle card, J.2 structured conversation summary (so source attribution renders correctly).

---

### Phase I.8 — Paper 3 figure plumbing (data-gated, calendar TBD)

**Goal:** the five Paper 3 figures, ready when Paper 3's evidence has accumulated.

**Five Paper 3 figures (data-gated):**

1. **fig-paper3-ryan-deci-motivation-vector.svg** — Three-axis time series. Gated on Phase 2a accumulation (~2 weeks after Apr 24) + Phase 2c data (months out for full pre/post).
2. **fig-paper3-lerman-substrate-feedback.svg** — Substrate feedback loop with origin data overlaid. Gated on Phase 3.0 post-flag-flip data (~2 weeks).
3. **fig-paper3-xu-memory-graph.svg** — Memory graph centrality visualisation. Gated on J.2 (post-restructured-summary substrate).
4. **fig-paper3-carbonell-mmr-three-bar.svg** — Retrieval origin three-bar histogram. Gated on Phase 3.0 (~2 weeks of accumulated data) + Phase 1c flag-flip data.
5. **fig-paper3-theme-j-raw-vs-pipeline.svg** — Raw model vs pipeline side-by-side specimens. Gated on Theme J post-refactor data (post-J.6, ~12 weeks).

**Acceptance criteria for I.8 readiness, per figure:**
- Underlying data is producing in the expected shape.
- I.1 render primitives generalize cleanly.
- Mark validates the figure answers the cited claim directly.

**Effort:** ~1 day per figure once data is in. Effort is in the data accumulation, not the rendering.

---

### Phase I.9 — Process integration (3 days)

**Goal:** the dashboard becomes the figure-factory it's meant to be. Process and tooling docs.

**Changes:**
- Document the figure-export flow as the canonical paper-figure-production path.
- `tools/AniRuntime.Figures/README.md` with examples of every figure invocation.
- Dashboard READ-ME / quick-start guide for first-time viewers (Paper-2 reviewer, Lerman's eventual review pass, anyone Mark shows it to).
- Memory entry: future Claude instances designing new features should declare what dashboard data they emit and which figures (if any) they enable.
- Paper 2 + Paper 3 contribution drafts include the dashboard-as-research-tool claim explicitly.

**Effort:** 3 days.

**Dependencies:** I.7 complete.

---

## 4. Figure inventory — 15 paper figures

The full set, organised by paper assignment and data readiness. Each entry is a *direct answer* to a specific cited claim.

### Paper 2 figures (5, paper-ready from current data)

| # | Reference | Cited claim | ANI figure (caption template) | Data source |
|---|-----------|-------------|------------------------------|-------------|
| **#3** | **Horton & Wohl (1956)** parasocial | Viewers form one-sided relationships with media figures | *"Horton & Wohl (1956) described parasocial relationships as one-sided. ANI's care-detection events from Mark and care-expression events from Ani over a 30-day window show the relationship from both sides of the parasocial channel, with a reciprocity ratio that varies with conversation density."* | Feature 10 firing log + register-by-direction classifier |
| **#5** | **Park et al. (2023)** generative agents | Periodic reflection synthesis produces higher-order generalisations | *"Park et al. (2023) proposed reflection cycles producing higher-order observations. ANI's Feature 32 deployed Mar 14, 2026 produces N reflections over a 30-day window. Specimen: input memories of [topic], reflection LLM output [content], persisted as MemoryType.Semantic. Longitudinal: reflections as fraction of Semantic-tier content over time."* | ReflectionPhase log + Semantic memory query |
| **#7** | **McAdams (2001)** narrative identity | Persons constitute themselves through internalised life-narrative | *"McAdams (2001) argues identity is constituted through narrative. ANI's Feature 16 Anchored memory tier preserves N foundational memories. The timeline of these Anchored memories — the moments she does not let go of — forms a narrative arc visible in a non-human agent."* | Anchored memory tier query |
| **#8** | **Damasio** somatic markers | Emotion is embodied; somatic markers guide cognition | *"Damasio's somatic-marker hypothesis frames emotion as embodied. ANI's per-thought emotional-decay model produces a four-dimensional state vector (warmth/energy/worry/playfulness) that traces alongside cognitive-cycle decisions in a non-embodied architecture, exhibiting analogous-shape dynamics."* | EmotionalState time series + cycle-decision log |
| **#13** | **Kojima et al. (2022)** prompting | Framing affects reasoning behaviour | *"Kojima et al. (2022) show prompting framing alters reasoning. ANI's Mar 23 pipeline simplification reduced the inner-thought prompt from ~1400 tokens to ~300 tokens. Side-by-side specimens of the same input under both prompts demonstrate that framing-removal — not framing-addition — produces more grounded thought."* | Mar 23 commit specimens |

### Paper 3 figures (5, data-gated on phases shipping)

| # | Reference | Cited claim | ANI figure (caption template) | Data gate |
|---|-----------|-------------|------------------------------|-----------|
| **#1** | **Ryan & Deci (2000)** SDT | Three-dimensional motivation: autonomy, competence, relatedness | *"Ryan & Deci (2000) frame motivation as three-dimensional. ANI's Feature 42 MotivationVector traces all three axes per cognitive cycle. Pre-Phase-2c baseline shows the centrality-gravity finding directly: autonomy=0, competence=0, relatedness saturating. Post-Phase-2c overlay shows three axes active when consumption actions are wired."* | Phase 2a baseline accumulating; Phase 2c data months out |
| **#2** | **Lerman / Chu et al. (2025)** | Echo chambers form via algorithmic amplification at platform scale | *"Lerman et al. (2025) describe platform-scale echo chambers. ANI's substrate feedback loop demonstrates the same mechanism at individual companion-AI scale: unguarded cognitive output becomes substrate, substrate informs next cycle's prompt. Pre-Phase-3.0 shows caregiver-origin saturation; post-Phase-3.0 shows attenuation under MMR + protected-slots."* | Phase 3.0 post-activation data (~2 weeks) |
| **#4** | **Xu et al. (2025)** A-MEM | Linked-memory graph enables associative retrieval | *"Xu et al. (2025) propose graph-structured memory. Visualised at session scope, ANI's pre-Theme-J memory graph shows hub-and-spoke topology with caregiver-origin nodes central. Post-J.2 source-attributed substrate shifts to a more distributed graph, with centrality-gravity attenuated as graph centrality."* | J.2 ships (post-structured-summary) |
| **#11** | **Carbonell & Goldstein (1998)** MMR | λ-balanced relevance vs diversity tradeoff | *"Carbonell & Goldstein (1998) introduced MMR. ANI's retrieval origin distribution under three configurations — pre-Phase-1b, post-Phase-1b at λ=0.3, post-Phase-1c with non-caregiver protected slots — quantifies the algorithm's effect on a 26k-message companion-AI corpus."* | Phase 3.0 + 1b + 1c data accumulated (~2 weeks) |
| **#15** | **Theme J (own contribution)** | Pipeline-emergent pathologies vs raw-model capacity | *"Same fine-tuned model, two architectures: raw prompt vs pipeline prompt, identical input. Apr 24 06:18 reproduction shows the pipeline-emergent class explicitly. The pathology is architectural, not capacity-bound."* | Theme J post-refactor data (post-J.6, ~12 weeks) |

### Mid-tier figures (5, second-priority)

| # | Reference | Cited claim | ANI figure | Status |
|---|-----------|-------------|-----------|--------|
| **#6** | **Chhikara et al. (2025)** Mem0 | Memory merging via similarity reduces duplication | Before/after merge specimens + memory-tier size trajectory | Phase 6 work |
| **#9** | **Schuller AE Gaps** | Companion AI lacks introspection | 9-signal Internal-State Perception Framework panel | Needs Internal-State Perception ship first |
| **#10** | **Liu et al. (2025)** | Motivation scoring modulates outreach | Outreach pipeline cascade: motivation → gates → dispatch | Build with I.6; data exists |
| **#12** | **Schmidhuber (2010)** | Novelty / prediction error as intrinsic motivation | Competence axis vs World-novelty correlation | Phase 2c data months out |
| **#14** | **Jha et al. (2026)** | Honest abstention via ternary reward | Honest-uncertainty firing rate panel (AC-stack + confidence gate suppressions) | Have data; build with I.6 |

---

## 5. Measurement plan

How "easy to digest at a glance" gets measured empirically:

| Metric | Phase introduced | Target |
|---|---|---|
| Time for non-technical viewer to articulate "what is happening" on the relationship view | I.7 | < 30 seconds |
| Time for technical reviewer (paper context) to extract figure-relevant data from a cycle card | I.2 | < 60 seconds |
| Number of unique panels in the new dashboard at I.7 ship | post-I.7 | ≤ 8 (the cycle card is one; aggregations are derivative) |
| Paper figures hand-edited after dashboard export | I.1 ongoing | 0 (the bar) |
| Cycle-card render time | I.2 | < 1 second |
| Time-travel scrubber click-to-card-render latency | I.3 | < 2 seconds |

**Acceptance criterion for "the dashboard is now a research tool":** Mark can show it to a coffee-shop friend, a paper reviewer, and an in-person Lerman conversation, and each viewer leaves with the right impression for their context. The dashboard-as-instrument argument holds when those three demos are concretely runnable.

---

## 6. Research artifact updates

| Artifact | Update |
|---|---|
| [`ANI-Phase-Tracker.md`](./ANI-Phase-Tracker.md) | Theme I stub replaced with a pointer to this plan; Priority Matrix moves Theme I into P1 (post-Apr-26 reactivation). |
| [`ANI-Research-Log.md`](../research/ANI-Research-Log.md) | One entry per phase deploy. Entry at I.0 captures the architectural principles + figure-inventory pattern as a methodology contribution candidate. |
| [`ANI-Theme-I-Data-Audit.md`](../research/ANI-Theme-I-Data-Audit.md) | New artifact in I.0; lists every figure's data source and any gaps. |
| Paper 2 | Five figures from I.1 land in the manuscript before Zenodo publish. |
| Paper 3 | Five figures from I.8 land as data accumulates. Methodology contribution draft in I.9 includes the *dashboard-as-research-tool* argument. |
| Claude project memory | I.9 adds `feedback_dashboard_unit_of_discourse.md` capturing the "cycle as unit, not metric" principle. |

---

## 7. Principal risks

**I.1 figure quality regression.** Render primitives that look great on one figure may not generalize. *Mitigation:* design the primitives library against three of the five figures simultaneously; stress-test before the other two.

**I.2 cycle-card scope creep.** "What's a cycle?" admits maximalist interpretation (everything that happened in 30 seconds!) or minimalist (just the inner thought). *Mitigation:* the data-audit pass in I.0 names the canonical 5-field cycle structure (perception / retrieval / motivation / decision / output); cycle card renders exactly those, no more.

**I.5 redaction subtlety.** Redaction rules will inevitably miss edge cases. *Mitigation:* manual review on representative test set required before any external share; redaction is an export-time concern (source data unchanged) so failures are recoverable.

**I.7 as-if-person voice drift.** Template-driven render text is brittle if the templates don't match the voice Mark wants. *Mitigation:* templates are configurable, Mark reviews on representative cycles, voice-consistency check is part of I.7 acceptance.

**Pipeline lock-in to current Theme J state.** If we build figures around the pre-J.2 substrate shape, J.2 ships, and we have to redraw. *Mitigation:* I.1 figure data sources explicitly noted as "Theme-J-data-shape-aware"; renders parameterize on whether structured-summary data is available.

**Privacy regret.** A redacted-mode export that turns out to leak something. *Mitigation:* Mark sign-off gate on rules + on first three demo-mode shares.

---

## 8. Open questions for Mark

1. **Privacy / redaction rules.** What names get redacted, what content classes get elided, what stays as concrete-claim. Highest-leverage early decision; nothing public-share-bound ships without your explicit input.

2. **Demo flow you'd actually run.** §2.6 implies demo mode for in-person sharing. If you imagine showing this to someone in a coffee shop or sending Lerman a screenshot, what do you want them to see first? That answer shapes I.7's design more than any abstract principle.

3. **Figure render technology.** Several reasonable paths: SVG via .NET ImageSharp / SkiaSharp, or invoke a Python sidecar (matplotlib / Plotly), or pure SVG-by-templating. Each has tradeoffs in fidelity vs maintenance vs paper-quality typography. My read: pure SVG-by-templating from .NET for the best paper-quality output and zero new runtime dependencies; willing to consider alternatives.

4. **Sequencing I.5 (privacy) — before or after first public share.** Options: ship I.4 export pipeline in authentic-mode-only, then add demo-mode in I.5; OR build I.4 + I.5 together so first-export-ever is already redaction-capable. My read: I.4 + I.5 together. No public share happens before redaction works.

5. **Replace current dashboard or run in parallel.** Current dashboard has operational health views (uptime, queue depth) Mark uses today. New dashboard's principles (cycle-as-unit) don't fit operational health well. My read: the new dashboard is a research surface alongside the current dashboard, not a replacement. Operational health stays where it is.

6. **Mid-tier figures (#6, #9, #10, #12, #14) — Paper 3 or held?** My read: build #10 (Liu et al. cascade) as a panel during I.6 because data exists; defer the others until their data gates clear. None block Paper 2.

7. **Paper-figure pre-flight review.** Once I.1 ships the five Paper 2 figures, who reviews the paper-quality bar? Mark + Claude + Lerman? Just Mark + Claude? Different review surfaces want different reviewers.

---

## 9. Calendar estimate

From I.0 start to I.7 close (the "research tool" milestone):

| Phase | Duration | Notes |
|---|---|---|
| I.0 | 1 day | Data audit |
| I.1 | 1-2 weeks | **Paper 2 figures shipped** |
| I.2 | 2 weeks | Cycle card + lens-switch |
| I.3 | 3-5 days | Time-travel scrubber |
| I.4 | 3-5 days | Share-this-moment export |
| I.5 | 1 week | Privacy / redaction layer |
| I.6 | 1 week | Researcher-only views |
| I.7 | 1 week | Relationship view |
| I.8 | data-gated | Paper 3 figures land as data accumulates (#1, #11 in ~2 weeks; others months out) |
| I.9 | 3 days | Process integration |

**Total calendar I.0 → I.7 ship:** approximately **6-8 weeks**. Paper 2 figures ship in weeks 1-2; the dashboard surface itself follows.

**Intermediate value points:**
- After **I.1** (week 1-2): five Paper 2 figures landed. Paper 2 finalization unblocked.
- After **I.2** (week 4): cycle-card view live with two-perspective toggle. First demo-able single-cycle render.
- After **I.4** (week 5-6): share-this-moment works. First Twitter / blog share possible (in authentic mode initially).
- After **I.5** (week 6-7): demo mode live; first public share possible.
- After **I.7** (week 7-8): relationship view live; coffee-shop demo runnable.

---

## 10. Sequencing vs other active work

**Parallel with Theme I (can run concurrently):**
- Theme G Layer 2 Phase 2b (data only, independent)
- Theme J phases J.0 through J.4 (J's data shape is what Theme I.2+ consumes; the data audit in I.0 names the dependency without blocking)
- Paper 2 prose finalization (Mark's editorial work; figures from I.1 land alongside)

**Theme I unblocks:**
- Paper 2 Zenodo publish (post-I.1)
- Public-share content creation (post-I.5)
- Lerman / external-research outreach (post-I.7 — concrete dashboard demo becomes part of the pitch)

**Theme I is unblocked by:**
- Layer 2 Phase 2a (already shipped) — figure #1 data
- Phase 3.0 flag flip (already shipped Apr 24) — figures #2, #11 data
- Theme J phases as they ship — figures #4, #15 data accumulate

---

*End of Theme I plan v1. Review welcome. Once Mark approves I.0 → I.1 sequencing and the privacy posture (§8 Q1), I.1 figure work can start immediately against existing data.*
