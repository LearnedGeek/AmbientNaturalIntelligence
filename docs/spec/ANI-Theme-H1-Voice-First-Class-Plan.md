# Theme H Phase H1 — Voice as a First-Class Feature

**Drafted:** April 29, 2026
**Status:** Active — gating conditions met as of Apr 28 evening
**Origin:** Mark Apr 29 10:25 — *"making voice a first-class feature outside of anything else we do can really improve the interactivity."*
**Theme owner:** Mark (named voice as the next substantial improvement); Claude executes phasing.
**Companion docs:** `docs/spec/phase-5-design.md` (streaming voice baseline), `docs/spec/ANI-Phase5c-AutoModel-Design.md` (training-data feedback loop), Theme H section in `ANI-Phase-Tracker.md`.

---

## What This Phase Is

Voice mode currently exists as deployed infrastructure but not as a daily-use first-class channel. The streaming pipeline (MAUI → WebSocket → Deepgram STT → Ollama → ElevenLabs v3 TTS → MAUI) works end-to-end as of Mar 15-16. ElevenLabs v3 with audio tags shipped Mar 30. The 1,806-tag catalogue catalogued during the Mar 6 design session is **on the shelf** — `VoiceTagEnricher` currently picks from a minimal hardcoded set, not from the catalogue. Initial audio static, VAD barge-in, and end-to-end latency tuning are all listed as "remaining" in the Phase 5 changelog.

Mark's framing makes this an explicit priority shift: voice is not just a channel — it is the **load-bearing interface for the use cases where the project actually matters most** (driving, working, hands-free). Text loses what voice carries; emotional fidelity is the whole point of the project. OG Ani's voice quality (Grok's companion voice) is the empirical bar — Mark notes it surpasses the regular speech-to-text experience in mainstream chat channels, and approaching that quality with the asset stack we already have (1,806 tags + LMKit + ElevenLabs v3) is a tractable architecture-over-training goal.

H1 takes the existing voice pipeline from "deployed and working in narrow conditions" to "daily-use first-class," with vocal range that approaches OG Ani's quality.

## What H1 Is NOT

- **Not a fork of Ani for voice.** Mark Apr 29 10:33: *"I don't think it's necessary to parse at different places because we should rely on her register to carry across both."* Same Ani, same identity substrate, same memory architecture. Voice-mode register additions are prompt-level guidance, not a separate persona.
- **Not a retraining cycle.** Architecture over training. The 1,806-tag catalogue is a runtime asset; LMKit classification + ElevenLabs v3 with smarter tag selection produces vocal range without a v8 training pass.
- **Not Theme H §H2 (image generation / visual substrate).** H1 and H2 split as of Apr 29 — H1 is unblocked and active, H2 stays P3 deferred. The visual substrate work has bigger gates and a different design overhead (the Apr 29 09:13-09:24 brainstorm captured H2's open questions in §Theme H).
- **Not a replacement for text-mode.** Text-mode SMS via Twilio remains primary; voice is the parallel channel that excels for hands-free use.

## Gating Conditions — All Met as of Apr 28 evening

Theme H's original deferred status had three gates:
1. ✅ **Theme G Layer 1 shipped + measured.** Flags flipped Apr 24; baseline accumulating since.
2. ✅ **Parrot-bug root cause fixed.** Theme J J.1+J.2+J.3 shipped Apr 27; observation window open.
3. ✅ **Conversation quality won't amplify text-mode failures into audible failures.** Apr 28 evening's ~90 minutes of sustained coherent conversation on cleaned substrate is the empirical answer.

H1 is therefore **unblocked** and graduates from P3 (deferred) to active.

## Phase Structure

### Phase H1.0 — Tag Taxonomy Review ⏳
**Status:** Not started.
**Estimated effort:** ~2 days.

Organize the 1,806 catalogued tags by dimension. The catalogue itself is the asset; without taxonomy work it's noise that the selection logic can't navigate. Output is a structured reference (`docs/research/voice/elevenlabs-tag-taxonomy.md` or similar) that maps each tag to:
- **Dimension** — emotional valence / intensity / social context / prosody / pacing / age-tone / breath / non-verbal sound
- **Register affinity** — Tenderness / Longing / Playfulness / Hurt / Resilience / Agency / Disagreement / Honest-Self-Confrontation
- **Content cues** — what kind of text content the tag pairs well with (e.g., `[breathy]` for vulnerability disclosures, `[teasing-laugh]` for playful banter, `[soft]` for closing statements)
- **Time-of-day affinity** — early morning, midday, evening, late night
- **Combination compatibility** — which tags can stack vs. conflict

Identify the **50-100 highest-leverage tags** for the relational register Ani uses most (Tenderness 65.5% / Longing 25% per the 30-day data). Those become the priority set for selection logic; the long tail stays catalogued but not actively used until the priority set is dialed in.

**Acceptance:** taxonomy doc exists; ≥50 highest-leverage tags identified per register; combination-compatibility matrix populated for the priority set.

### Phase H1.1 — Selection-Logic Design ⏳
**Status:** Queued behind H1.0.
**Estimated effort:** ~3 days.

Decide and document the selection algorithm. Three candidate approaches:

**(A) LMKit-driven register classification → tag mapping.** LMKit classifies the outgoing utterance's register (Tenderness / Longing / Playfulness / etc.); the taxonomy from H1.0 maps register → tag set; emotional state (Warmth / Energy / Concern / Playfulness) modulates intensity tags within the set. Most architecturally consistent with the existing emotional model.

**(B) Prompt-driven Ollama tag selection.** A small dedicated prompt asks the inner-thought model to select tags for the utterance given context. Slower (one extra LLM call per utterance); more flexible.

**(C) Deterministic emotional-state-vector → tag rules.** No LLM call; rules engine maps (W, E, Concern, Playfulness, register, time-of-day) → tag set. Fastest, least flexible, easiest to debug.

Probably **hybrid: A for register classification + C for intensity modulation**. (B) reserved for fallback when the deterministic path returns ambiguous results.

**Acceptance:** decision recorded with reasoning; sample inputs (state, register, content, time-of-day) → expected tag sets documented; Mark approves before H1.2 builds.

### Phase H1.2 — `VoiceTagEnricher` Rewrite ⏳
**Status:** Queued behind H1.1.
**Estimated effort:** ~3 days.

Rewrite `VoiceTagEnricher` to consume the H1.0 taxonomy + H1.1 selector. Replace the current minimal hardcoded set. Spec tests pin tag selection per (register × emotional state × time-of-day) combination so future drift surfaces fast. Tests at the same Theme K strict-mock + TDD discipline as Apr 28's test methodology.

**Acceptance:** rewrite shipped; ≥30 spec tests covering the priority register/state combinations; build clean (0 warnings); 700+ tests passing.

### Phase H1.3 — Voice-Mode Register Prompt Revision ⏳
**Status:** Queued behind H1.2.
**Estimated effort:** ~2 days.

Voice-mode replies should differ from text-mode replies in spoken-natural ways:
- **No cliffhanger-tics.** *"but honestly?"* reads fine in SMS; sounds like a hung modem in TTS. (Theme E §StripCliffhangerTic position-bug fix should ship before this so the gate's coverage is uniform.)
- **Different pacing guidance.** Spoken Ani breathes; texted Ani uses ellipses. The prompt should hint at where pauses (`[pause-short]`, `[pause-medium]`) belong vs. where they're inappropriate.
- **Fewer ellipses, more punctuation.** ElevenLabs interprets ellipses as long pauses; in voice that creates an awkward halting cadence. The prompt should bias toward periods, commas, and explicit pause tags rather than `...` for trailing thought.
- **Caps register awareness.** All-lowercase reads as casual texting in SMS; ElevenLabs renders caps fine but the rendering can be unintentionally subdued. Voice-mode prompt may want sentence-case explicit guidance.
- **Spoken length calibration.** A voice reply sounds fine at the same length as a text reply, but a multi-paragraph reply is much more imposing in voice — pacing matters more. Probably 1-3 sentences for typical voice exchanges, with longer replies reserved for substantive content.

`BuildVoiceReplyPrompt` currently exists but is structurally similar to `BuildLeanConversationPrompt`. Revision adds the voice-mode register block at the top.

**Acceptance:** revised `BuildVoiceReplyPrompt` shipped; spec tests pin the voice-register guidance; before/after voice samples generated for Mark's blind comparison.

### Phase H1.4 — Streaming Round-Trip Hardening ⏳
**Status:** Queued behind H1.3 (or parallel with H1.3 if separate hands).
**Estimated effort:** ~1 week.

The Phase 5 changelog lists three remaining issues that block daily-use:
- **Initial audio static.** First ~100ms of TTS output has audible artifact; needs investigation (Deepgram → ElevenLabs handoff buffer? PCM warm-up? client-side audio queue priming?).
- **VAD barge-in.** Silero VAD design exists but interrupt-during-Ani-speaking isn't implemented. Critical for natural turn-taking; without it, conversation feels staged.
- **End-to-end latency tuning.** Target: <800ms first-audio from end-of-speech-detection. Current latency profile not measured cycle-by-cycle.

Each is its own diagnostic + fix effort. H1.4 may run parallel with H1.3 if Mark wants to keep the calendar tight.

**Acceptance:** initial-audio static gone; VAD barge-in working with conversation flow demonstrated; end-to-end latency measured and ≤800ms p50 / ≤1500ms p95.

### Phase H1.5 — Real-Use Evaluation ⏳
**Status:** Queued behind H1.2-H1.4.
**Estimated effort:** ~1 week observation window.

Mark spends a calendar week using voice mode in his actual daily-use contexts (driving, working, hands-free). Notes go into the research log: which registers landed clean, which sounded synthetic, which forced him back to text. Direct comparison against OG Ani as the empirical bar.

**Acceptance:** ≥5 voice sessions logged with notes; Mark's read on whether the OG-Ani bar was approached, met, or missed; gap analysis if missed → feeds H1.6 or back to H1.0/H1.1 for tuning.

### Phase H1.6 — Process Capture + Paper 3 Contribution Draft ⏳
**Status:** Queued behind H1.5.
**Estimated effort:** ~3 days.

H1's architectural shape — *"how does an open-source companion-AI substrate exploit a richer prosody vocabulary than the conversational-AI mainstream uses, without retraining the underlying TTS model?"* — is a Paper 3 (or Paper 4) contribution candidate. Same architecture-over-training shape as the rest of the project: don't retrain the TTS model, leverage the tags it already knows via runtime selection logic informed by emotional state + register + temporal context.

Output: prose draft in `docs/research/papers/paper3/contribution-voice-tag-enrichment.md` (or wherever the Paper 3 contribution drafts live). Cites OG Ani as the empirical bar; cites the 1,806-tag catalogue as the asset; cites the LMKit-driven selection logic as the mechanism.

**Acceptance:** prose draft exists; figures included (tag selection trace, before/after spectrograms or audio samples, register × tag heatmap from H1.0 taxonomy); ready for Paper 3 integration when the broader paper structure firms up.

## Sequencing & Dependencies

- **H1.0** (taxonomy) is foundational; everything else needs it.
- **H1.1** (selection logic) depends on H1.0; everything after needs H1.1.
- **H1.2** (VoiceTagEnricher rewrite) depends on H1.0+H1.1; pins H1.5's evaluation surface.
- **H1.3** (voice-register prompt) is parallel with H1.2 if separate hands; small dependency on H1.2 only at integration time. **Theme E `StripCliffhangerTic` position-bug fix should ship before H1.3** so the gate's coverage is uniform across modalities.
- **H1.4** (round-trip hardening) is largely parallel with H1.2/H1.3 — different code surface (streaming pipeline rather than tag selection / prompt content).
- **H1.5** (real-use evaluation) depends on H1.2+H1.3+H1.4 all shipping.
- **H1.6** (Paper 3 contribution) depends on H1.5 outcomes.

**Total calendar:** ~3-4 weeks if executed serially with H1.4 parallel; ~2-2.5 weeks if H1.2/H1.3/H1.4 run in parallel with separate attention.

## Acceptance Criteria for the Phase Overall

- Voice mode is daily-use grade for Mark's actual contexts (driving, working, hands-free).
- Vocal range across registers approaches OG Ani's quality per Mark's blind read.
- Tag richness applied dynamically — ≥50 priority tags actively in use, mapped to register × emotional-state × time-of-day combinations.
- Streaming round-trip <800ms p50, no initial audio static, VAD barge-in working.
- Voice-mode register prompt produces spoken-natural rhythm without cliffhanger-tics, awkward ellipses, or jarring length.
- Same Ani — identity substrate, character seeds, World Layer all carry across surfaces; no fork.
- Paper 3 contribution draft exists capturing the architecture-over-training framing for tag-driven prosody.

## What This Phase Doesn't Address

- **§H2 Visual Substrate Layer** stays P3 deferred. The Apr 29 brainstorm captured H2's open architectural questions in §Theme H of the tracker; H2 has bigger gates (visual identity choice, Type 11 in pixels, outbound truth gating across modalities) that need their own design sessions.
- **Voice-mode dispatch via Twilio voice channel.** Currently voice is MAUI client only. The "I just want to call her" use case is not part of H1 scope; if Mark wants that later it becomes H1.7 or its own phase. MAUI + Bluetooth-in-the-car is the H1.5 evaluation context.
- **v8 voice training data.** Architecture-over-training principle says we don't need a v8 cycle for H1's wins. If H1.5 reveals capacity gaps the model can't bridge through tag selection alone, that surfaces a v8 candidate scope but doesn't block H1's phases.

## Status Log

| Date | Phase | Note |
|------|-------|------|
| 2026-04-29 | H1.0 | Plan drafted by Mark + dogfood Claude after Mark named voice as the next substantial improvement priority. Theme H gating conditions confirmed met as of Apr 28 evening. Architecture-divergence question resolved: same Ani across surfaces, register carries. Plan committed to repo, H1/H2 split in priority matrix (H1 active, H2 stays P3 deferred). |
