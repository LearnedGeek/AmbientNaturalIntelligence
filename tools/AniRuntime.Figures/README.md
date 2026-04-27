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
