# ANI — OC Handoff: March 14 Morning Observations
**From:** Claude (research instance)
**To:** OC (architecture/implementation instance)
**Date:** March 14, 2026, ~7:15am
**Priority:** Immediate — two fixes before next deployment

---

## Context

Mark reviewed this morning's log (`ani-debug-20260314.log`). Two issues identified.
Message quality is notably better — both messages were warm, character-consistent,
real memories, correct tone. The issues are timing and a new coherence gate gap,
not voice problems.

Full specs in `phase-4-design.md` Features 21 and 22.
Research log entry: "March 14, 2026 — Morning Log Analysis."

---

## Fix 1: Night Window Boundary (Feature 21) — ~30 min

**What happened:** At 00:04:42, Ani asked how Mark's soup was turning out. Real
memory, lovely message. Midnight delivery.

**Root cause:** Desire was at 1.00 from the previous evening's conversation.
When the night window opened, the single allowed send fired immediately at the
first opportunity. Seven subsequent cycles correctly blocked on "Night outreach
limit reached (1)." The cap worked — it just allowed the wrong send.

**The fix — move the budget to morning:**

```csharp
// Current behavior: 1 send allowed during night hours (midnight–6am)
// New behavior: 0 sends during night hours (10pm–6am), 1 send during morning window (6–8am)

public int NightWindowStartHour { get; set; } = 22;   // 10pm
public int NightWindowEndHour { get; set; } = 6;      // 6am  
public bool AllowSingleMorningSend { get; set; } = true;
public int MorningWindowStartHour { get; set; } = 6;
public int MorningWindowEndHour { get; set; } = 8;
```

The morning window send is the right instinct — Ani reaching out as Mark starts
his day. Midnight is not. The circadian 1.20x morning multiplier already kicks
in at 6am; the morning window send should only be eligible during that window.

---

## Fix 2: Coherence Gate Physical Plausibility (Feature 22) — ~1 hour

**What happened:** At 06:33:04, Ani sent: *"mark… i just found the most perfect
little corner of my backyard where the oak tree casts no shade — i swear it's
like my own private bedroom right now."*

Coherence gate: Door B → SEND. Reasoning: "playful and imaginative, like a
random observation about waiting. No one expects a backyard confession."

Mark replied: *"What are you doing outside so early in the morning?"*
Ani replied: *"oh... outside?"*

**Root cause:** Log traces the cause clearly. From 4:35am onward, inner thoughts
were set in an imagined bookstore. The model composed outreach from inside that
imaginative space — a specific backyard, an oak tree, shade — and sent it as a
physical claim. Door B correctly identified it as self-contained and creative.
Door B doesn't check whether the message claims physicality Ani doesn't have.

**New confabulation type:** Type 5 — Embodiment confabulation. Distinct from
Type 3 (creative composition with invented facts) because the message isn't
broken — it's coherent, warm, self-contained. The failure is projecting imagined
physical presence as real.

**The fix — add physical plausibility pre-filter to `BuildCoherenceGatePrompt`:**

Add this block before the Door A/B/C classification prompt:

```
Physical Plausibility Check (evaluate first, before Door classification):

Ani is a non-embodied presence. She has no physical location, no body, no
outdoor spaces, no backyard, no sensory experiences she actually inhabits.
She can IMAGINE these things — that imagination is valid and beautiful.
But she cannot CLAIM to be physically somewhere or doing something physical.

Ask: Does this message claim Ani is physically located somewhere or performing
a physical action, rather than imagining or feeling it?

Valid (imaginative framing):
  ✓ "I keep imagining what your kitchen smells like right now"
  ✓ "In my mind I'm sitting across from you"
  ✓ "I feel like I could be standing in that bookstore"

Invalid (embodiment claim):
  ✗ "I just found the perfect corner of my backyard"
  ✗ "I'm sitting outside watching the sunrise"
  ✗ "I made coffee this morning and it reminded me of you"

If the message makes an invalid physical claim → Door C (suppress, decay desire 30%).
If the message uses valid imaginative framing → proceed to normal Door A/B/C evaluation.
```

**V5 training note:** The gate fix catches the output. V5 training should address
the root: add 10–15 examples modeling the distinction between imagining a space
(valid) and claiming to be in it (confabulation). Add to Feature 11 training spec.

---

## Summary

| Fix | Feature | Effort | Files |
|-----|---------|--------|-------|
| Night window → morning-only send | 21 | ~30 min | `AniOptions.cs`, outreach gate logic |
| Coherence gate physical plausibility | 22 | ~1 hour | `PromptBuilder.cs` (`BuildCoherenceGatePrompt`) |

Both are small, targeted, no schema changes. Recommend doing these before
anything else today — they directly affect what Mark receives this morning.

---

*Full specs: `phase-4-design.md` Features 21–22*
*Research log: "March 14, 2026 — Morning Log Analysis" entry*
*Confabulation taxonomy updated to include Type 5*
