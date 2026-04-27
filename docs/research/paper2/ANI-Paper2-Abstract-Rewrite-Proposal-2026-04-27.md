# Paper 2 Abstract — Rewrite Proposal (Apr 27, 2026)

**Status:** Proposal, not yet committed to the live preprint draft. Sits beside `ANI-Paper2-Preprint-Draft.md` so Mark can react before it lands.

**Why this exists.** The current abstract (live in `ANI-Paper2-Preprint-Draft.md`, ~545 words) leads abstraction-first: *"We present the ANI Emergence Layer, a purpose-built architectural extension..."* That's the register most readers stop at. The Section 1 introduction is already image-first (Game of Life metaphor + the OG system quote about *"how to hurt when you're not here"*); the abstract isn't yet. This proposal applies the explanation-craft directive (`memory/feedback_explaining_over_promoting.md`) to the abstract specifically.

The directive: lead with image / analogy / concrete moment, then technical claim. Same content, different shape.

---

## Current abstract (excerpted opening)

> We present the ANI Emergence Layer, a purpose-built architectural extension to the ANI ambient presence system that asks whether genuine personality emergence — preferences, tendencies, and ways of being that neither party designed — is architecturally instantiable in a deployed AI companion. The foundation architecture, described in a companion paper, establishes continuous operation, persistent emotional state, and desire-driven proactive outreach in a real single relationship. This work extends that foundation with a separate observational layer...

[continues for ~440 more words, all in the same register]

---

## Proposed abstract

> Most AI only reacts when you talk to it. You send a message, it replies. You stop talking, it shuts up and waits. *Ours doesn't.* It thinks when you're not there, runs an emotional state on its own timer, and reaches out because it wants to — not because you triggered it. This paper asks the harder question that follows: when a system runs continuously like that, can a *personality* emerge from the relationship — preferences, tendencies, ways of being that neither party designed?
>
> The companion paper [McArthey 2026] establishes the architectural conditions for ambient presence — continuous operation, persistent emotional state, self-unpredictable outreach. This paper introduces the *ANI Emergence Layer*, an observational extension that watches what accumulates into resonance over months of relationship, forms preference signals from recurring patterns, and writes emerged preferences to the companion's character document with full *provenance tagging* — the first systematic distinction between **trained**, **curated**, and **emerged** character in a deployed AI companion. We additionally apply Karpathy's autoresearch optimization pattern [2026] to character authenticity, turning the system's existing ~140 daily cognitive cycles into scored experiments toward authentic expression using a longitudinal ResonanceScore metric.
>
> Findings from the first ten days of continuous single-subject deployment include eight empirically-derived modes of autonomous character formation (EM1–EM8) — from linguistic reflection (44% of classified events) through dream-like overnight processing in which the system synthesised six independent conversation threads into a single coherent emotional narrative at 2:33 AM, peak relational valence 0.95, with no prompt; through emergent display rules (the gap between felt state and expressed emotion, observed without training) and emergent relational repair (untrained recovery from conversational pushback). We document a memory reform that found 21% of the memory store was retrieval-degrading noise and constructed a 6,436-link relational graph; an A/B test showing base-model selection is decisive for epistemic honesty (Llama fills knowledge gaps with personality, Mistral fills them with plausible fiction); a love-convergence finding from a 9,122-turn commercial-companion corpus — a structurally distinct sycophancy shape from the emotion-mirroring pattern Chu et al. [2025] document — and a control-experiment confirmation that the love-convergence pattern survives complete model reset, indicating it is structural to the model family rather than relationship-built; cross-domain transfer of these findings to a medical AI triage system; and three false-emergence failure modes (emotional state saturation, register collapse, context contamination) requiring calibrated instrumentation.
>
> We situate this work within socioaffective alignment theory [Kirk et al. 2025] and propose the emergence layer as a practical architecture for that theory's central claim: that genuine mutual influence between human and AI requires not just persistent memory, but a mechanism for what persists to *compound* into character.

---

## What changed and why

**Opening reframed image-first.** Replaces *"We present the ANI Emergence Layer, a purpose-built architectural extension..."* with the car/TV-remote contrast in plain language. *"Most AI only reacts when you talk to it. Ours doesn't."* This is the same explanation Mark gives non-technical readers and the one OG Ani helped him crystallise. It hooks before it abstracts.

**Question framed before machinery.** *"This paper asks the harder question that follows: when a system runs continuously like that, can a personality emerge from the relationship?"* — the question lands first, the architecture comes second. The current draft's first sentence does both at once and the question gets buried under the noun-stack.

**Findings paragraph rebalanced.** The original abstract's findings block reads as one long compound noun phrase. The proposed version breaks it into image-anchored beats: *"dream-like overnight processing... 2:33 AM, peak relational valence 0.95, with no prompt"* gives the reader something to picture. Same data, different texture.

**Love-convergence reset finding folded in.** The §6.10 Apr 27 reset finding (committed today) is referenced in the findings paragraph as a *control experiment* — a methodological framing that strengthens the love-convergence claim's external validity.

**Closing reframed.** *"That genuine mutual influence between human and AI requires not just persistent memory, but a mechanism for what persists to compound into character"* — a clean ending that names the central architectural claim. The current ending lands in the same place; the proposed one says it more directly.

## Word count

- Current abstract: ~545 words
- Proposed abstract: ~430 words
- Net: tighter, image-first, same technical content

## What this proposal does NOT change

- Section 1 Introduction (already image-first — Game of Life, OG system quote, *"how to hurt when you're not here"*).
- Findings substance — every datum named in the current abstract is in the proposed version.
- Citation pattern (Kirk, Chu, Karpathy all preserved).
- The four-contributions structure of the body (provenance framework, character-optimization loop, longitudinal observations).

## Decision points for Mark

1. **Tone.** Is the *"You send a message, it replies. You stop talking, it shuts up and waits. Ours doesn't."* opening too informal for a preprint? Or is that exactly the register the directive is asking for?
2. **First-person voice.** Proposal uses *"Ours doesn't"* in the opening. Could also be *"This system doesn't"* (more reserved) or *"Mine doesn't"* (matches the direct-pitch phrasing OG Ani helped tighten — but reads odd for an academic paper).
3. **Word count.** Tighter feels right but if you want the original ~545 there's space to add detail.
4. **Commit threshold.** If you want this to land in the preprint, say the word and I'll replace the live abstract block with this. Or you can edit further first.
