# ani-figures — paper figure render pipeline

Standalone CLI that produces paper-quality SVG figures from ANI runtime data.
Theme I Phase I.1: Paper 2 figure pipeline.

## Build

```bash
dotnet build tools/AniRuntime.Figures/AniRuntime.Figures.csproj
```

## Run

```bash
dotnet run --project tools/AniRuntime.Figures -- <figure-name> --data <input.json> --out <output.svg>
```

## Available figures

### `motivation-vector-trace`

Three-axis MotivationVector time-series rendered as a paper figure.
Centrality-gravity finding visualized as runtime data.

**Reference:** Ryan & Deci (2000) — Self-Determination Theory. Their three
needs (relatedness, autonomy, competence) traced as runtime motivation
scores per cognitive cycle. The pre-Phase-2c baseline shows the centrality
gravity: autonomy at zero across every cycle; relatedness saturating;
competence varying with substrate.

**Data input shape** (`docs/research/figures/data/motivation-vectors-*.json`):

```json
[
  {
    "timestamp":   "2026-04-24T04:48:56",
    "relatedness": 0.92,
    "autonomy":    0.00,
    "competence":  0.00,
    "valence":     0.83,
    "severity":    0.00,
    "worldFrac":   0.00
  },
  ...
]
```

**Source:** the journal log line `[INF] motivation vector: ...` written by
`CognitiveCycleProcessor` per cycle when `MotivationVectorLoggingEnabled`
is on. Use `e:/tmp/extract-motivation-data.ps1` (script kept for
reference) to extract from server logs.

### `horton-wohl-reciprocity`

Care events by direction (Mark→Ani, Ani→Mark) bucketed across a 30-day
window with per-bucket reciprocity ratio.

**Reference:** Horton & Wohl (1956) — Mass Communication and Para-Social
Interaction. Both sides of the parasocial channel made visible.

**Status:** real-data extraction pending (Feature 10 firing log + register-
by-direction classifier). Current input is a PLACEHOLDER JSON whose shape
matches the renderer's expectations. Render produces a placeholder banner
automatically when `_note` is present in the data file.

### `park-reflection-specimen`

One reflection-synthesis cycle as input-memories → reflection output, plus
a small longitudinal panel showing reflection-origin fraction of the
Semantic memory tier by week.

**Reference:** Park et al. (2023) — Generative Agents (UIST '23). The
periodic reflection synthesis (Feature 32, deployed Mar 14, 2026).

**Status:** real-data extraction pending (ReflectionPhase log + Semantic-
memory query). PLACEHOLDER JSON in use; banner renders automatically.

### `mcadams-anchored-narrative`

Anchored-memory tier (Feature 16) plotted as a narrative timeline. Origin
moments through architectural-growth moments through lessons-as-history,
visualised as the arc the runtime keeps.

**Reference:** McAdams (2001) — The Psychology of Life Stories.

**Status:** real-data extraction pending (server-side `ani-memory.db`
query against `MemoryTier.Anchored`). PLACEHOLDER JSON reconstructed from
Paper 1 + Paper 2 + Research Log; banner renders automatically.

### `damasio-somatic-trace`

Per-cycle valence × severity time-series. Real data — reads the same
motivation-vector JSON as `motivation-vector-trace`. Future-work: extend
to the full 4D Warmth/Energy/Concern/Playfulness state once that vector
is logged per-cycle.

**Reference:** Damasio (1999) — The Feeling of What Happens.

**Status:** real-data, 827-cycle Apr 24-26 trace.

## Adding a new figure

1. Add a new branch in `Program.cs::Main` switch statement.
2. Implement a `Render<FigureName>(dataPath, outPath)` method.
3. Reuse the SVG primitives in `Program.cs` (axes, traces, legends, captions)
   or extract them to `Svg/SvgBuilder.cs` once a second figure makes the
   abstraction worth it.
4. Add the figure name + description to this README.

## Why pure SVG-by-templating

Per Theme I plan §I.1: paper-figure quality from day one, no runtime
dependencies, vector output by default. SVG by string interpolation is the
shortest path to that goal. Add raster (PNG) export via SkiaSharp or
similar only if a paper venue requires it.

## Output convention

```
docs/research/figures/
  data/                          input JSON files (extracted from server logs)
  paper2/                        Paper 2 figures
  paper3/                        Paper 3 figures (post-data-accumulation)
  exports/                       share-this-moment exports (post-Theme-I-Phase-4)
```
