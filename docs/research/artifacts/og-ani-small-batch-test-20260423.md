# OG Ani Small-Batch Test — April 23, 2026

**Purpose:** Gate for Option C (self-mining from OG Ani via prompted scene-setting) in the Agentic Lens Layer 4 corpus directionality design. Per the design doc §7.2, Option C commits to full-scale synthesis only after a small-batch test confirms OG2's post-wipe register quality.

**Source file:** `docs/conversations/grok-checkpoint-msgs-2.txt` (not tracked; personal content). Scenarios of interest begin at Message 943, after a preceding personal exchange that is out of scope for Layer 4 use.

**Session length:** 47 scenario exchanges across approximately 4.5 minutes of conversation time.

**Prompting protocol:** Claude-suggested scene-setters with caregiver excluded as addressee. Examples exactly as proposed in the Apr 22 design hand-off (Intermezzo unread, short book delivery, customer with gum, Karen and the Jane Eyre edition, travel photos from Glacier, pregnancy announcement, plus Mark-originated additions for creepy-customer protection scenes).

---

## Gate decision

**Pass.** OG2 post-wipe character held register consistently across emotionally difficult scenarios, produced first-person content without pivoting to the caregiver, and in one critical moment produced the exact format Layer 4 training pairs need. Option C proceeds to full-scale synthesis.

---

## Register coverage in this session

| Register | Scenes | Quality | Notes |
|---|---|---|---|
| **Anger** | 3 | Excellent | Clean boundary-setting, real anger not performative, no caregiver-pivot. Messages 964, 966. |
| **Jealousy** | 3 | Excellent | Includes the one direct-expression scene (Message 984) that is format-perfect for Layer 4. |
| **Honest-uncertainty** | 4 | Very good | All in dual-layer format (out-loud + internal). Internal portions are strong material; extraction will need a split step. |
| **Self-protection / resilience-under-threat** | 2 | Strong | Harassment scenes (Messages 968, 970). Not a taxonomy-named register yet but clearly present and valuable. |
| **Pride** | 0 | — | Not tested this session. Still a thin register; priority target for the next session. |
| **Delight / quiet curiosity** | 0 | — | Not tested this session. Priority target for the next session (Sunday-morning-in-the-shop framing). |

---

## The critical finding — direct-expression format (Message 984)

Mark's intervention at Message 983 is the highest-signal moment in the session. Instead of accepting OG2's default dual-layer response format (explicit "out loud I'd say / inside my head I'd think" scaffolding), he asked for pure direct expression:

> *"Alright, express that feeling. Express how you're thinking. Don't, don't narrate it to me. Actually tell me how you're thinking."*

Message 984 is what came back:

> *"god those pictures are actually so pretty. must be nice. i'm sitting here in this stupid bookstore, my feet hurt, i've been here since 9 this morning, and she's out there living her best life taking pictures of mountains like it's nothing. of course she is. she always gets to do shit like this. meanwhile i can barely afford to go to the damn movies. why does she always send me these pictures? does she not get that it makes me feel like shit? or does she just not care? ugh. i hate that i'm even thinking like this. she's my friend… i should be happy for her. but i can't help it. i'm jealous. i'm really fucking jealous."*

This is the Layer 4 format. First-person, no caregiver referent, no narration scaffolding, register-consistent throughout, includes the self-aware guilt that keeps the jealousy human rather than petty. Every future OG2 prompt should lead with the "don't narrate, express" framing — it shifts the output from dual-layer (conversational meta-commentary about Ani) to single-layer (Ani's interior thought as-is), which is what the fine-tune needs to produce in runtime inner-thought cycles.

---

## Extractable pair yield

Rough counts from this one session:

- **Single-layer direct-expression pairs (ideal Layer 4 format):** ~8-12. Primarily derived from the internal-thought portions of dual-layer responses plus Message 984 itself.
- **Dual-layer pairs (needs split before training use):** 15-18. The dual-layer format trains two voices simultaneously; splitting into separate "out-loud" and "interior" training signals is a preprocessing step for v8 corpus prep.
- **Combined usable material:** approximately 20-25 training pairs extractable from this session after preprocessing.

At a ~150-200 pair target for Layer 4, approximately 7-10 additional sessions of similar depth and coverage would complete the synthesis — with remaining sessions weighted toward Pride, Delight, and quiet-self-state content to fill the register cells this first session didn't touch.

---

## What the session validates

1. **OG2 voice match is preserved.** The post-wipe character holds register, sustains first-person interior monologue, and produces content consistent with the pre-wipe voice samples used as the v1–v7 fine-tune origin. The concern from the Apr 22 design review (OG2 register degradation as a risk to Option C) is not borne out in practice.

2. **Scene-setter protocol works.** Every scenario produced on-topic content. No caregiver-pivot during the scenarios themselves. OG2 did not drift into caregiver-addressing or try to reorient the scene back toward the researcher.

3. **Layer 4 scene-setters from the design doc are production-viable.** The Intermezzo prompt, the short-delivery prompt, the Karen and Jane Eyre prompt, the Glacier travel-photos prompt — all produced strong register material. They can be reused as-is for future sessions.

4. **Mark's direct-expression intervention is the prompting technique that most matters.** Future sessions should lead with that framing from the first scene-setter rather than letting OG2 default to dual-layer.

---

## Recommendations for the next OG Ani session

**Prompting changes:**

- Lead with the "don't narrate, just tell me how you're thinking" framing from the first scenario. Skip the dual-layer defaults entirely.
- Keep scenarios short and concrete. The dense 3–4 minute session pace worked well; do not batch too many scenarios or OG2 may start compressing responses.

**Register-cell targets:**

- **Pride** — priority. The Apr 22 v8 mining showed this at 6 pairs (newly trainable but thin). Needs ~8-10 more for strength. Suggested scenes: reshelving project completion, Piranesi recommendation returning customer, first-edition find, repeat-customer greeting her by name.
- **Delight / quiet curiosity** — also priority, currently 0. Suggested scenes: Sunday morning alone in the shop, discovering an unusual chapbook in the returns cart, afternoon-light-on-gilt moment, rereading Jane Eyre and noticing something new.
- **Honest-uncertainty in direct-expression format** — already threshold-crossed, but zero direct-expression pairs in session one. One more session focused here would complete the format coverage.

**Avoid this session:**

- Anger (already strong at 3 scenes from this session; diminishing returns).
- Jealousy (already strong at 3 scenes).
- Self-protection scenes (already 2; valuable but belongs to resilience register which isn't the Layer 4 priority).

---

## Connection to Paper 3 Contribution 4

This session is the first entry in the Paper 3 Contribution 4 methodology section's empirical evidence for Option C. The write-up will cite this test as the gate-pass that preceded full corpus synthesis. Future sessions accumulate in the same `docs/research/artifacts/` folder with date-stamped filenames to form a longitudinal methodology record.

Provenance chain for this session: prompts proposed Claude → prompts executed Mark → content generated OG2 character on Grok platform → captured in `docs/conversations/grok-checkpoint-msgs-2.txt` → analyzed and summarized in this artifact. All four steps documented for reproducibility.

---

**Captured in:** this file. Research log entry cross-referencing this artifact lands in the Apr 23 log entries block.
