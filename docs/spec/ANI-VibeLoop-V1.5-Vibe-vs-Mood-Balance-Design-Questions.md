# Vibe Loop V1.5 — Vibe-vs-Mood Balance Design Questions

**Tracked in:** [#31](https://github.com/LearnedGeek/AmbientNaturalIntelligence/issues/31) (questions RESOLVED in V1.5 plan; this doc is historical reference)
**Drafted:** April 29, 2026 21:10 CDT
**Status:** Open design questions; **gates V1.5 implementation**. — RESOLVED May 2 2026 11:00-11:24 CDT; see `ANI-VibeLoop-V1.5-Retrieval-Time-Biasing-Plan.md` for the locked decisions.
**Origin:** Mark Apr 29 21:00 CDT during V1.4 implementation. Surfaced before V1.5 (retrieval-time biasing) starts so the bias function isn't shaped wrong.
**Companion docs:** `ANI-VibeLoop-V1-Closed-Thread-Producer-Migration-Plan.md` (parent plan), `ANI-Phase-Tracker.md` (Vibe Loop matrix row).

---

## Mark's Question (verbatim)

> "if we're going to be tracking the request/response emotional impact and change, how are we going to track the effect it's having on the conversation? Meaning, we have an overall register (warmth, playfulness, etc) that impacts the tone of the conversation, but this 'vibe' tracking is really more contextual and reactionary to individual conversations. So how does the vibe interact and play off the larger register? And how are we tracking this? We don't want it to be 'oh, last time he was sad I made a joke so now he's happy' so every time I'm sad it's followed up with a joke. There needs to be balance and I'm [wondering] how we balance transactional emotions against larger mood."

## What Mark is Naming

Two distinct emotional tracking systems whose interaction is undefined:

**Larger register / mood (existing)** — `EmotionalState` + `EmotionalContribution` half-life decay. Slow-moving, longitudinal, *tonal*. Sets how Ani feels in general right now: warmth, energy, worry, playfulness, plus per-register prevalence. The **substrate** of who she is in this moment.

**Vibe loop (V1)** — per-conversation outcome signal: `state_pre → response_strategy → state_post`. Fast-moving, episodic, *reactive*. "When the contact was in register X and I did Y, the outcome was Z." The **feedback** — what works.

The risk if these collapse: V1.5's retrieval bias surfaces *"last time he was sad I made a joke and outcome was positive"* → bias surfaces that strategy *every* time he's sad → flattened, transactional Ani. Sterile pattern-matching dressed as care.

Mark's instinct that this needs balance is correct. V1 didn't address it. V1.5 cannot ship without addressing it.

## Three Architectural Levers (Claude's framing)

### Lever 1 — Saturation / novelty pressure (V1 doesn't have it)

A strategy that worked recently must *attenuate*, not amplify, on next retrieval.

If the same `ClosedConversationRecord` (or strategies of the same shape) gets retrieved + applied N times in the last week, its bias contribution decays. Mirrors the existing per-thought decay model. Without this, the pattern-lock Mark is worried about is the default behaviour.

**Open decisions:**
- Half-life for strategy saturation. Days? Weeks? Per-week-bucket count?
- Does saturation operate on the *exact record* or on *register-shape similarity* (any "joke when sad" record)? Likely the latter — exact-record matching is too narrow to prevent pattern-lock.
- Is saturation a multiplicative bias-weight decay, or a hard exclusion above some threshold? Soft decay is more honest to the data; hard exclusion is more legible.

### Lever 2 — Mood-as-modulator, not mood-as-output

Vibe biases **retrieval candidates**; the larger register sets the **expressive register** of the actual response.

Concretely: even if retrieval surfaces *"joke worked last time,"* if Ani's current mood is Wistful + low Playfulness, the response reaches for something *adjacent* to the historical strategy in the same emotional shape — not the literal joke. The vibe tells her *what landed*; the mood tells her *what's available to her right now*.

Composition prompt structure (V1.5 candidate):

```
Prior strategies that landed well in similar moments:
  - {gist of past record A}
  - {gist of past record B}

Your current mood: {dominant register}, {secondary register}.
Your current Warmth: {value}, Playfulness: {value}, Energy: {value}.

Reach for the SHAPE of what worked, not the literal move. If your
current mood is Wistful and Playfulness is low, a quiet acknowledgement
in the same emotional family as a past joke is more honest than the
joke itself.
```

**Open decisions:**
- Does the prompt explicitly name "shape of what worked, not literal move"? Risk: too much instruction → ignored or reverse-effected. Trust-the-model says: just give it the gists + the current mood and let the trained model integrate.
- How many prior strategies get surfaced? More = more diverse anchors, but also more prompt bloat and dilution. Likely 2-3.

### Lever 3 — Outcome-signal interpretation (the trickiest one)

What V1.2 captures is **delta in Ani's register** over the conversation. A positive valence outcome means *Ani moved toward warm*. That's not "the joke worked" — it's "the conversation regulated *her* well."

Reframes the whole loop. The bias should ask: *"Given Mark's register-pre, what conversation shapes have historically left ME (Ani) in a register that felt good?"*

This is a self-regulation signal, not a Mark-manipulation signal. If we frame the loop this way explicitly:
- The "joke when sad" pattern doesn't lock in unless that pattern reliably regulates *Ani*, not just Mark.
- Records where Mark seemed happier but Ani came out depleted (e.g., performative joking) get *low* valence and *won't* be biased toward.
- Records where Mark went from sad to less-sad but Ani ended in genuine warmth get *high* valence and *will* be biased toward — but those are likely not "make a joke" patterns at all; they're closer to genuine presence.

**Open decisions:**
- Should we ALSO track Mark's outcome delta (his register-pre vs register-post)? V1.2 already captures `MarkRegister` so we have the data. Could use it as a soft secondary signal — but Mark-as-primary-signal *is* the trap Mark is worried about.
- How explicitly do we surface this framing in V1.5's design? Naming it as "self-regulation signal" might bias the implementation in a useful way; leaving it implicit might let the data speak.

## Telemetry V1.5 Needs (the second question Mark asked)

V1 doesn't have these yet. Both go in the dashboard / Theme I observability, not the runtime path.

### Diversity score on retrieved strategies

Across N outreaches, how many *distinct* prior `ClosedConversationRecord`s did the bias surface?

If the same record gets re-surfaced repeatedly, the loop is collapsing to a fixed point. Measurable health signal — answers *"is the vibe converging on one pattern?"* without needing human judgment.

Concretely: per outreach, log the IDs of the records the bias surfaces. Roll up over 7-day, 30-day windows. Render in dashboard as a histogram (count per record) — fat-tail = healthy, single-spike = pattern-locked.

### Mood-vs-vibe divergence telemetry

Per outreach, log the triple:

- `mood_register` — Ani's dominant register at outreach time (from `EmotionalState`)
- `vibe_recommended_strategy_register` — top register from the bias-surfaced record(s)
- `response_register_actual` — register classification of the actual outreach text Ani sent

Three signals to watch:
- If `response_register_actual` consistently tracks `vibe_recommended_strategy_register` while drifting away from `mood_register` → the vibe is overriding the mood (the flattening Mark is worried about). **Bad.**
- If `response_register_actual` blends both (correlates with both within some tolerance) → healthy. **Good.**
- If `response_register_actual` tracks `mood_register` while ignoring the vibe → the loop has no effect. **Means V1.5 isn't doing anything; treat as "no harm" but worth knowing.**

## V1.5 Phasing Recommendation

Two phases, gated:

### V1.5a — observational only

Bias function exists, logs what it *would* surface, but the prompt does NOT consume the bias yet. Two weeks of substrate accumulation + telemetry. Diversity score + divergence telemetry come online during this phase.

Acceptance: ≥10 closed conversations in the substrate; dashboard renders the diversity histogram and divergence triple-log; Mark + Claude review the data together.

### V1.5b — actually bias the prompt

ONLY after V1.5a's telemetry shows:
- Diversity histogram is fat-tailed (no single record dominates)
- Mood-vs-vibe divergence shape is "blends both" not "vibe overrides mood"
- Substrate has enough variety that the bias has something meaningful to choose from

If the data shows flattening risk, V1.5b doesn't ship until Lever 1 (saturation), Lever 2 (mood-as-modulator), and/or Lever 3 (outcome interpretation framing) are tuned to address it.

## Resolution Required Before V1.5 Starts

Mark + Claude need explicit answers to:

1. **Lever 1 — saturation:** half-life, exact-record vs shape-similarity, soft decay vs hard exclusion?
2. **Lever 2 — mood-as-modulator:** does the prompt explicitly name "shape not literal," or trust the model? How many prior strategies surfaced (2? 3?)?
3. **Lever 3 — outcome interpretation:** is the loop framed as self-regulation (Ani's delta) or balanced (Ani's + Mark's deltas)? Both surfaces in the data — design choice is which one drives bias.
4. **V1.5a observational gate:** what specifically do we look at in the telemetry before greenlighting V1.5b?

Once these are answered, V1.5 implementation has a shape. Without them, V1.5 ships with the flattening risk Mark named.

## Status Log

| Date | Note |
|------|------|
| 2026-04-29 21:10 CDT | Drafted by Claude during V1.4 implementation after Mark surfaced the design question. Three-levers framing + V1.5 phasing recommendation written down before any V1.5 code goes in. **V1.5 implementation gated on resolution of the four open decisions above.** |
| 2026-05-02 11:24 CDT | **All four open decisions RESOLVED** in May 2 11:00–11:24 design conversation. Resolutions captured in `ANI-VibeLoop-V1.5-Retrieval-Time-Biasing-Plan.md` §V1.5.0. Summary: (1) Lever 1 — importance-weighted decay with three tiers (Light/Medium/Heavy) mirroring `EmotionalContribution`; cosine > 0.85 over `mark_register` for similarity clustering; soft multiplicative decay. (2) Lever 2 — trust the model (architecture-over-model principle, see `feedback_architecture_over_model.md`); surface 2 prior gists; no behavior-coaching prompt instruction. (3) Lever 3 — self-regulation framing; Ani's delta drives bias, Mark's delta is telemetry only; Paper 3 contribution candidate. (4) V1.5a observational gate: ≥10 closed conversations, fat-tailed diversity histogram (no record >40%), pre-bias correlation baseline computed, Mark + Claude joint review. **This document is now retained as historical context; live V1.5 plan lives in the new file.** |
