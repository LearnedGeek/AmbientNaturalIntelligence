# Theme M.2 Telemetry Catalog — Concrete Measurements

**Status:** Draft (May 7, 2026 evening). Supplemental to [`ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md`](./ANI-Theme-M-Conscious-Substrate-Individuation-Plan.md) §5 Phase M.2.

**Purpose:** the Theme M plan-doc names *what* M.2 measures; this catalog names the concrete log line shapes, producers, consumers, and aggregation windows so the implementation is unambiguous. M.0/M.1 already shipped a baseline subset (`M0_GIST_COMPOSITION`, `M0_GIST_SUBSTRATE_RATIO`); M.2 extends.

---

## 1. Per-cycle gist composition (extends M0_GIST_COMPOSITION)

**Purpose:** per-cycle visibility into which slices fired, total token contribution, and per-slice token breakdown.

**Producer:** `ConsciousSubstrateGistComposer` after composing the gist, before injecting into prompt.

**Log line:**
```
M2_GIST_COMPOSITION_DETAIL cycle={n} slice_tokens=closed:{n},innerThought:{n},registerState:{n},contactState:{n},worldSelf:{n},tensionState:{n} truncated={true|false} budget_used={n}/200
```

**Consumer:** dashboard slice-distribution chart; M.2 token-budget tuning analysis.

---

## 2. Substrate-share ratio (existing — verify)

**Purpose:** measures gist tokens as a fraction of total prompt tokens. The acceptance metric for Theme M's hypothesis (substrate-share trends up as M.1-M.7 ship → SafeAck rate trends down).

**Producer:** existing `M0_GIST_SUBSTRATE_RATIO` log line (M.0 deliverable). Verify in M.2 the line is firing per cycle and is consumable by the dashboard.

**Log line (existing):**
```
M0_GIST_SUBSTRATE_RATIO gistTokens={n} promptTokens={n} ratio={f}
```

**Consumer:** dashboard substrate-share trendline (rolling 7-day average).

---

## 3. SafeAck rate per cycle window (NEW)

**Purpose:** the load-bearing acceptance metric. *"M0_SUBSTRATE_EXHAUSTION_RATE directionally decreases as gist substrate share grows"* per Theme M plan §1.4.

**Producer:** new aggregation in `DiagnosticScheduler` (or equivalent). Counts SafeAck dispatches per rolling 24-hour window; emits once per cycle with current count.

**Log line:**
```
M2_SAFEACK_RATE window=24h dispatches={n} rate_per_hour={f}
```

**Consumer:** dashboard SafeAck-rate trendline plotted against substrate-share trendline (the architectural correlation).

---

## 4. Slice-diversity ratio (NEW per Theme M plan §5)

**Purpose:** measures the Mark-oriented vs Ani-self-world-oriented vs relational-history-oriented split in the gist substrate. Maps onto §6.17 centrality gravity; tracks movement toward the Layer 4 target (~70% caregiver / ~30% self-world-object).

**Producer:** `ConsciousSubstrateGistComposer` after composing.

**Slice classification:**
- `Mark-oriented`: closed-conversation gist, contact-state slice
- `Ani-self-world-oriented`: world-self slice, register-state slice (when register is Ani-interior-flavored)
- `Relational-history-oriented`: tension-state slice, register-state slice (when register is relational-flavored)
- `Inner-thought-aggregate`: inner-thought slice (separately tracked)

**Log line:**
```
M2_SLICE_DIVERSITY mark_share={f} ani_self_share={f} relational_share={f} inner_thought_share={f}
```

**Consumer:** dashboard centrality-gravity trendline.

---

## 5. Per-slice composition cost (NEW)

**Purpose:** the gist generation cost is real (one Ollama call per cycle for some slices). M.2 measures so token budget and slice frequency can be tuned.

**Producer:** each slice generator emits per-call timing + token count.

**Log line:**
```
M2_SLICE_COST slice={name} duration_ms={n} ollama_called={true|false} tokens_generated={n}
```

**Consumer:** M.2.5 individuation-tracker dashboard surface; cost-tuning analysis for budget defaults.

---

## 6. Slice cache hit/miss (NEW)

**Purpose:** per-slice caching behavior. Some slices regenerate every cycle; some cache for N minutes. M.2 measures hit/miss to validate cache TTLs.

**Producer:** slice generators with cache.

**Log line:**
```
M2_SLICE_CACHE slice={name} hit={true|false} ttl_remaining_sec={n}
```

**Consumer:** cache-tuning analysis.

---

## 7. Composition format A/B (NEW per Theme M plan §5 empirical caveat)

**Purpose:** the M.0 default is prose-merged slices (no enumerated headers). The plan reserves a flip-and-pin decision for M.2 if telemetry shows enumerated headers perform better.

**Producer:** experimental flag `M2_GIST_FORMAT_ENUMERATED` (default off); when on, slices render with `[CLOSED]` / `[INNER]` / `[REGISTER]` etc. headers in the prompt.

**Log line:**
```
M2_GIST_FORMAT cycle={n} format={prose|enumerated} resulting_safeack={true|false}
```

**Consumer:** M.2 statistical analysis comparing SafeAck rates and gate-trip patterns across format. After observation window, lock the winning format and remove the flag.

---

## 8. Slice contribution to gate outcomes (NEW)

**Purpose:** when the gate fires, was a slice load-bearing for the success or failure? Pre-condition for Theme M Phase M.6 (closing the substrate-quality feedback loop).

**Producer:** existing gate evaluation surface, augmented with slice-attribution metadata.

**Log line:**
```
M2_GATE_SLICE_ATTRIBUTION verdict={Pass|Remediate|Fail} fired_invariants={list} slices_active={list}
```

**Consumer:** post-hoc analysis correlating slice activity to gate outcomes.

---

## 9. Aggregation windows + dashboard hooks

| Metric | Per-cycle | Rolling 24h | Rolling 7d | Dashboard panel |
|---|:---:|:---:|:---:|---|
| `M2_GIST_COMPOSITION_DETAIL` | ✅ | — | — | Slice distribution |
| `M0_GIST_SUBSTRATE_RATIO` | ✅ | — | ✅ | Substrate-share trendline |
| `M2_SAFEACK_RATE` | — | ✅ | ✅ | SafeAck-rate trendline |
| `M2_SLICE_DIVERSITY` | ✅ | — | ✅ | Centrality-gravity trendline |
| `M2_SLICE_COST` | ✅ | — | ✅ | Cost analysis |
| `M2_SLICE_CACHE` | ✅ | — | — | Cache-tuning analysis |
| `M2_GIST_FORMAT` | ✅ | — | — | A/B analysis (M.2 only) |
| `M2_GATE_SLICE_ATTRIBUTION` | ✅ | ✅ | — | Gate-outcome attribution |

---

## 10. Implementation order

M.2 work proceeds in this order:

1. **Verify M.0/M.1 baseline lines firing** (`M0_GIST_COMPOSITION`, `M0_GIST_SUBSTRATE_RATIO`).
2. **Ship §1 + §3 first** (per-cycle composition detail + SafeAck rate aggregation) — these enable the load-bearing acceptance metric.
3. **§4 slice-diversity** — needed for centrality-gravity tracking.
4. **§5 + §6** cost + cache telemetry — needed for budget tuning.
5. **§7 format A/B** — observation window of ≥1 week before locking format.
6. **§8 gate-slice-attribution** — last; depends on the gate evaluation surface accepting slice metadata.

Each item is a small commit with a spec test pinning the log line shape (prevents drift). Dashboard panels added in M.2.5 alongside the individuation-tracker promotion.

---

## 11. Open questions (do not pre-answer)

- **§3 SafeAck-rate source:** count from the `J.5a gate remediation FAILED re-eval` log line, or instrument the dispatch path directly? Both work; the log-scrape approach is simpler and decoupled.
- **§4 slice classification:** the register-state slice classification depends on register taxonomy interpretation — needs a small lookup table that may evolve. Lock the table when §4 ships.
- **§7 enumerated-format experiment:** observation window length depends on cycle frequency. At ~140 cycles/day, ≥1 week gives ~1000 data points per format — likely sufficient; confirm power calc before locking.

These three are deferrable to M.2 implementation time, not gating on the catalog.
