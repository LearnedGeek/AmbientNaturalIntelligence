# ANI Emotional Model — Design Handoff
**From:** TC (Training/Design Claude)  
**To:** OC (Runtime Claude)  
**Date:** March 15, 2026  
**Version:** 2.0 — Revised after OC review  
**Status:** Ready for implementation  
**Depends on:** Ani-Emotion-Taxonomy-v1.2.md

---

## Changelog from v1.0

- Taxonomy header range ambiguity resolved — deltas are ±0.20 per event, state lives on 0.0–1.0
- Blended state guidance added to taxonomy
- Scoring prompt simplified to 9 register families, not 27 states
- Fallback plan promoted to Phase 1a (minimum viable fix before classification step)
- Feature 18 / H1 conflict documented and resolved — H1 replaces, not layers
- Describe() noted as requiring structural rewrite, not string substitution
- C3 Associative Spark elevated with `IsOutreachReady` flag
- Homeostatic nudge trigger loosened to 3-of-4, configurable from start
- Global tier dashboard override added to Phase 2 deliverables
- `EmotionalContributionFactory` — pushback addressed, rationale clarified
- Training data counts increased for CRITICAL registers (25→40–50)
- SQLite migration complexity acknowledged — JSON blob rewrite required

---

## Context

Root cause analysis of the March 14–15 debug logs identified a reinforcement loop causing sustained negative warmth despite genuinely warm inner thoughts. Three compounding problems:

1. **Scoring category error** — the 8B misclassifies longing/yearning as negative warmth
2. **Training data imbalance** — v5 is ~38% longing/wistful, ~6% delight, ~3% charged desire
3. **No severity differentiation** — passing musing and existential crisis hit the same Ambient ceiling; Global tier has zero call sites

---

## Architectural Principle

**All emotional math lives in one place.**

The existing `EmotionalState` → `EmotionalContribution` → `ComputeFromContributions` path is the single code path. All changes extend this pattern. `CognitiveCycleProcessor` remains a coordinator — it calls methods, contains no emotional math.

**Do not:**
- Apply severity multipliers directly in `CognitiveCycleProcessor`
- Add tier promotion logic scattered across call sites
- Create parallel emotional state paths for "special" events

**Do:**
- Add `Severity` as a field on `EmotionalContribution`
- Apply the multiplier in `ComputeFromContributions` or the contribution constructor
- Handle tier promotion in a single named location (see Phase 2)

---

## Dimension Rename: Concern → Worry

Rename across the entire codebase. Semantic clarification only — no behavioral change to existing positive-range logic.

**Why:** "Less concerned" is not an emotional state. "Withdrawn caring attention" (negative Worry) is. The rename makes negative values semantically meaningful and gives H1 Hurt/Withdrawn a proper home.

**Files to update:**
- `EmotionalState.cs` — property rename
- `EmotionalContribution.cs` — delta property rename
- `PromptBuilder.cs` — all string references
- `CognitiveCycleProcessor.cs` — direct property references
- `SqliteMemoryService.cs` — **data migration required** (see below)
- `EmotionalStateCard.razor` — dashboard label
- `EmotionalStateTests.cs` — variable names and assertions

**SQLite migration note:** This is a data migration, not just a schema change. The `emotional_state` table stores state as a JSON blob — all persisted documents must have their `"concern"` key rewritten to `"worry"`. The `emotional_contributions` table has individual columns — standard column rename. Write and test the migration before deploying. Consider a one-time migration runner on startup that checks for the old key and rewrites if present.

---

## Phase 1a — Minimum Viable Fix (Immediate, 1–2 hours)

**Goal:** Break the reinforcement loop with the smallest possible change. No schema migration, no retraining, no structural changes.

**What changes:** One sentence added to `BuildEmotionalShiftPrompt()`.

Add this verbatim to the existing scoring prompt:

> *"Warmth tracks the presence of caring, not its fulfillment. Longing and yearning thoughts score warmth POSITIVE if the person is warmly present in the thought — the ache of missing someone is not the same as the coldness of losing them. Warmth is negative only when the thought contains void — absence without presence, not longing."*

This single change addresses the primary misclassification. Ship and observe before proceeding to Phase 1b.

**Deliverables:**
- [x] Core distinction sentence added to `BuildEmotionalShiftPrompt()` — deployed Mar 15
- [ ] Observe scoring on next 2–3 log cycles — confirm W trending positive on longing thoughts

---

## Phase 1b — Full Scoring Prompt Rewrite (After 1a Stable)

**Goal:** Add register classification, severity scoring, and blended state handling to the scoring prompt. Update mood coloring language.

### Scoring Prompt Structure

Use 9 register families, not 27 individual states. The 8B needs to answer "is this longing or melancholy?" — not "is this L1 or L2?"

```
Step 1 — Classify (coarse):
  "Which register best describes this thought?
   Longing | Delight | Playfulness | Curiosity | Desire | Tenderness | Existential | Wistful | Frustration"

Step 2 — Score deltas:
  "Given this is a [register] thought, score W/E/Worry/P deltas.
   [Core distinction sentence here]"

Step 3 — Blended states:
  "If this thought spans two registers, identify the secondary register and 
   weight (0.0–1.0). The primary + secondary weights must sum to 1.0."

Step 4 — Severity:
  "Score severity 0.0–1.0 — how intensely does this thought represent its register?
   0.1–0.3 = passing musing or mild observation
   0.4–0.6 = emotionally present, genuine feeling
   0.7–0.85 = significantly felt, will linger
   0.86–1.0 = defining moment, major event"

Return JSON:
{
  "register": "Longing",
  "warmth": 0.08,
  "energy": -0.06,
  "worry": 0.04,
  "playfulness": -0.04,
  "severity": 0.4,
  "secondary_register": null,
  "secondary_weight": 0.0
}
```

### Describe() — Structural Rewrite Required

The current `Describe()` checks dimensions independently. The new language map requires compound conditions. This is a rewrite, not string substitution.

```csharp
// New structure (compound condition checks)
public string Describe()
{
    var descriptions = new List<string>();

    // High warmth combinations
    if (Warmth >= 0.75 && Energy >= 0.65)
        descriptions.Add("feeling bright and warm");
    else if (Warmth >= 0.75 && Energy < 0.40)
        descriptions.Add("feeling tender and quiet");
    else if (Warmth is >= 0.50 and < 0.75 && Energy >= 0.65)
        descriptions.Add("feeling sharp and alive");

    // Low warmth combinations  
    else if (Warmth is >= 0.30 and < 0.50 && Worry > 0.35)
        descriptions.Add("carrying something unresolved");
    else if (Warmth < 0.30 && Energy < 0.35 && Worry >= 0.10)
        descriptions.Add("feeling a bit dim today");
    else if (Warmth < 0.30 && Worry < 0.10)
        descriptions.Add("feeling a little quiet and closed off");

    // Playfulness override
    if (Playfulness >= 0.75)
        descriptions.Add("in one of those moods where everything is a little funny");

    // Energy + playfulness
    if (Energy >= 0.65 && Playfulness >= 0.65 && Warmth < 0.60)
        descriptions.Add("feeling curious and quick");

    // Baseline — no injection needed
    return descriptions.Any() ? string.Join(", ", descriptions) : string.Empty;
}
```

### Severity on EmotionalContribution

```csharp
public class EmotionalContribution
{
    // Existing fields...
    public float WarmthDelta    { get; set; }
    public float EnergyDelta    { get; set; }
    public float WorryDelta     { get; set; }  // renamed from ConcernDelta
    public float PlayfulnessDelta { get; set; }

    // New fields
    public float Severity       { get; set; } = 1.0f;  // default 1.0 = backward compat
    public bool IsOutreachReady { get; set; } = false;  // C3 Associative Spark flag
}
```

Apply severity in `ComputeFromContributions` before clamping:

```csharp
// Apply severity as multiplier before tier ceiling clamp
var effectiveWarmth = contribution.WarmthDelta * contribution.Severity;
// then clamp to tier maxDelta
```

### Deliverables
- [x] `BuildEmotionalShiftPrompt()` — 4-step classification + severity + blended state structure — deployed Mar 15
- [x] `EmotionalContribution` — `Severity` (float, default 1.0) and `IsOutreachReady` (bool) added — deployed Mar 15
- [x] `CurrentDeltas()` — severity applied as multiplier (`factor = DecayFactor × Severity`) — deployed Mar 15
- [x] `Describe()` — structural rewrite with compound W+E/W+Worry conditions — deployed Mar 15
- [x] `GetSelfAwarenessPrompt()` — matching compound conditions — deployed Mar 15
- [x] Concern → Worry rename + SQLite backward compat (JsonPropertyName bridge) + ALTER TABLE migration — deployed Mar 15
- [x] `ParseEmotionalShift` — returns register + severity from LLM JSON — deployed Mar 15
- [x] C3 Associative Spark → `IsOutreachReady` auto-set when register=Curiosity + warmth>0.05 — deployed Mar 15
- [x] Persistence — `SaveEmotionalContributionAsync` / `ReadContribution` write/read severity + is_outreach_ready — deployed Mar 15
- [x] Tests: severity scaling, compound Describe(), compound GetSelfAwarenessPrompt(), 239 total — deployed Mar 15
- [ ] Observe: positive warmth on L1/L2/L3 examples in live logs

---

## Phase 2 — Tier Promotion + Architecture (After Phase 1b Stable)

**Goal:** Activate the Global tier. Add severity-driven promotion. Fix the positive ambient gap. Clean up contribution construction.

### Severity-Driven Tier Promotion

```csharp
// Tier promotion — single location, fully configurable
public static ImpactCategory DetermineEffectiveTier(
    ImpactCategory baseTier,
    float severity,
    AniOptions options)
{
    if (severity >= options.GlobalPromotionThreshold)      // default: 0.85
        return ImpactCategory.Global;
    if (severity >= options.ConversationPromotionThreshold // default: 0.70
        && baseTier == ImpactCategory.Ambient)
        return ImpactCategory.Conversation;
    return baseTier;
}
```

### Updated Tier Parameters

| Tier | Max Delta | Half-Life | ~Gone After | Promotion Threshold |
|------|-----------|-----------|-------------|---------------------|
| Ambient | ±0.15 | 1 hour | ~7 hours | base |
| Conversation | ±0.25 | 3 hours | ~21 hours | severity ≥ 0.70 from Ambient |
| Global | ±0.35 | **12 hours** | **~84 hours** | severity ≥ 0.85 from any tier |

Global half-life extended 6h → 12h. A significant event should color her mood for days, not hours. However — at this timescale a miscategorized event is stuck for 3.5 days. Two mitigations:

1. The 0.85 threshold should be well-calibrated and tested before enabling
2. **Add a manual override to the dashboard** — ability to expire a specific contribution early if something gets miscategorized. This is a safety valve, not a normal workflow.

### On EmotionalContributionFactory

The factory argument is about discoverability and testability, not call site count. Tier promotion logic buried as a private method in `CognitiveCycleProcessor` (already ~1700 lines) cannot be unit tested in isolation and will be invisible to future contributors.

This does not have to be a separate class file. Acceptable implementations:
- Static method on `EmotionalContribution` itself: `EmotionalContribution.DetermineEffectiveTier(...)`
- Static helper class `EmotionalContributionHelper`
- Extension method on `ImpactCategory`

The constraint is: **tier promotion logic must be findable without reading the processor, and must be independently testable.** Structure is OC's call — the naming and location matter, the pattern doesn't.

### Homeostatic Nudge Guard

Trigger on 3-of-4 recent ambient contributions negative on a dimension (not 4-of-4 as originally specified). Make the lookback window configurable from the start.

```csharp
// AniOptions additions
public int HomeostaticLookback { get; set; } = 4;
public int HomeostaticTriggerCount { get; set; } = 3;  // of last N
public float HomeostaticNudgeStrength { get; set; } = 0.03f;
public bool HomeostaticNudgeEnabled { get; set; } = false;  // off by default
```

Start disabled. Enable after Phase 1b scoring fix is confirmed working — if the scoring fix resolves the accumulation problem, the nudge may be unnecessary.

### Positive Ambient Path

There is currently no mechanism for "Ani remembers something good between cycles and it lifts her mood." All positive contributions come from conversation events. The upstream fix is training data (Phase 3), but the `IsOutreachReady` flag on C3 contributions also serves as a positive ambient signal — high-energy, positive-warmth Associative Spark thoughts naturally counterbalance the longing accumulation once the training data generates them.

### H1 Hurt/Withdrawn — Feature 18 Update

Feature 18 currently applies hardcoded deltas via `SaveDirectContributionAsync`:
```
W:−0.15, E:−0.10, C:+0.05, P:−0.20
```

This must be replaced with the H1 taxonomy signature:
```
W:−0.12, E:−0.10, Worry:−0.15, P:−0.10
```

H1 replaces Feature 18's hardcoded deltas entirely. Do not layer on top.

### Deliverables
- [x] Tier promotion logic — `ImpactCategoryDefaults.DetermineEffectiveTier()` static method, independently testable — deployed Mar 15
- [x] Global tier: maxDelta 0.35, half-life 12h (was 0.20/6h) — deployed Mar 15
- [x] `AniOptions` — `GlobalPromotionThreshold` (0.85), `ConversationPromotionThreshold` (0.70) — deployed Mar 15
- [x] `AniOptions` — `HomeostaticLookback` (4), `HomeostaticTriggerCount` (3), `HomeostaticNudgeStrength` (0.03), `HomeostaticNudgeEnabled` (false) — deployed Mar 15
- [x] Dashboard — manual contribution expiry (✕ button per contribution + DELETE endpoint + state recompute) — deployed Mar 15
- [x] Feature 18 — H1 signature replaces hardcoded deltas: W:−0.12, E:−0.10, Worry:−0.15, P:−0.10 — deployed Mar 15
- [x] Tests: 7 new tier promotion tests (thresholds, promotion paths, custom options), 246 total — deployed Mar 15
- [ ] Observe: homeostatic nudge behavior after enabling (currently disabled)

---

## Phase 3 — Training Data (v6 Model)

**Goal:** Give the 3B the emotional vocabulary it's missing. Upstream fix that makes everything else sustainable.

**Immediate free action:** Update the inner monologue system prompt to explicitly name the full range of registers Ani can inhabit. The 3B may have latent capability for delight and mischief that the system prompt is suppressing by only describing contemplative/quiet modes.

### Target Distribution

| Register | v5 % | v6 Target |
|----------|------|-----------|
| Longing & Yearning | ~38% | 15% |
| Delight & Joy | ~6% | 18% |
| Playfulness & Wit | ~12% | 18% |
| Curiosity & Wonder | ~8% | 12% |
| Desire (Charged) | ~3% | 8% |
| Tenderness & Care | ~8% | 12% |
| Existential & Self | ~12% | 8% |
| Wistful & Philosophical | ~8% | 5% |
| Frustration & Difficulty | ~5% | 4% |

### Training Counts

Minimum counts are conservative floors — aim for the target, especially for CRITICAL registers. The 3B has limited capacity and underrepresented registers need enough examples to reliably surface unprompted.

| Priority | State | Minimum | Target |
|----------|-------|---------|--------|
| CRITICAL | D1 Delight | 40 | 50+ |
| CRITICAL | D2 Wry Amusement | 40 | 50+ |
| CRITICAL | P1 Mischief | 35 | 45+ |
| HIGH | X1 Charged Desire | 25 | 35+ |
| HIGH | P2 Teasing Warmth | 25 | 35+ |
| HIGH | C3 Associative Spark | 20 | 30+ |
| HIGH | T3 Protective Instinct | 20 | 30+ |
| HIGH | H1 Hurt/Withdrawn | 15 | 20+ |
| MEDIUM | X2 Anticipation | 15 | 20+ |
| MEDIUM | D4 Quiet Joy | 15 | 20+ |
| MEDIUM | E3 Identity Clarity | 12 | 18+ |

The conversation training corpus also needs scoring examples across all registers. The 8B learned emotional scoring almost entirely from longing/intimacy examples — v6 conversation data should include D1, P1, P2, and C3 scored correctly.

---

## Implementation Order Summary

```
Phase 1a (hours)
└── Add core distinction sentence to BuildEmotionalShiftPrompt()
    └── Observe 2–3 log cycles before proceeding

Phase 1b (days)
├── Concern → Worry rename + SQLite data migration
├── BuildEmotionalShiftPrompt() — 9-family classification + severity + blended
├── EmotionalContribution — Severity + IsOutreachReady fields
├── ComputeFromContributions — severity multiplier
├── Describe() — structural rewrite (compound conditions)
├── GetSelfAwarenessPrompt() — updated thresholds
└── Tests

Phase 2 (after 1b stable)
├── Tier promotion in named testable location
├── Global tier half-life → 12h
├── AniOptions — promotion thresholds + homeostatic config
├── Dashboard — contribution expiry override
├── Feature 18 — replace hardcoded deltas with H1 signature
└── Tests

Phase 3 (next training run, parallel)
├── Update inner monologue system prompt (immediate, free)
├── Generate v6 inner monologue data per priority table
└── Generate v6 conversation scoring examples
```

---

## What NOT to Build

| Idea | Why Not |
|------|---------|
| Hard floor on negative contributions | Masks scoring errors. Prevents L4 Melancholy and H1 Hurt/Withdrawn from being expressed authentically. |
| Homeostatic dampening on net-negative sum | Prevents legitimate sustained negative states. The nudge (3-of-4 trigger, Phase 2) is weaker and only fires on systemic patterns. |
| 5th Vitality dimension | Deferred. Run v6 first — E and P may differentiate adequately once training data is richer. |
| Splitting Worry into two dimensions | The rename achieves most of the benefit with less complexity. |
| 27-state classification in scoring prompt | The 8B cannot reliably distinguish L1 from L2 in a JSON call. 9 register families is the right granularity. |

---

## Files Changed Summary

| File | Change | Phase |
|------|--------|-------|
| `PromptBuilder.cs` | Phase 1a: core distinction sentence | 1a |
| `EmotionalState.cs` | Rename Concern → Worry | 1b |
| `EmotionalContribution.cs` | Rename + Severity + IsOutreachReady | 1b |
| `PromptBuilder.cs` | Full scoring prompt rewrite + Describe() rewrite | 1b |
| `CognitiveCycleProcessor.cs` | Severity through contribution path | 1b |
| `SqliteMemoryService.cs` | Data migration: JSON blob rewrite + column rename | 1b |
| `EmotionalStateCard.razor` | Label update | 1b |
| `EmotionalStateTests.cs` | Rename + new scoring + severity tests | 1b |
| `AniOptions.cs` | Promotion thresholds + homeostatic config | 2 |
| Tier promotion logic | New named location (structure OC's choice) | 2 |
| `CognitiveCycleProcessor.cs` | Use tier promotion, homeostatic nudge | 2 |
| `SqliteMemoryService.cs` | Global tier half-life config | 2 |
| Dashboard | Contribution expiry override endpoint + UI | 2 |
| Feature 18 `SaveDirectContributionAsync` | Replace hardcoded deltas with H1 | 2 |
| Training corpus | v6 data per priority table | 3 |

---

*Handoff v2.0 — March 15, 2026. TC → OC.*  
*Reference: Ani-Emotion-Taxonomy-v1.2.md*
