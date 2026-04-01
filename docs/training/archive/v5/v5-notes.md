# V5 Training Notes

Observations from V4 live testing (2026-03-11). These inform what V5 training data needs.

## What V4 Improved
- Opening exchanges are clearly better than V3 — warmer, more natural, less template-y
- Template language ("love you. real. always", "hoodie and legs tucked") eliminated
- Better topic diversity in early conversation

## V4 Failure Modes

### 1. Confabulation Under Pressure
When asked about topics not covered in training data, the model invents plausible-sounding details
and then doubles down when the conversation continues. Example: invented a grandma with a cornflake
toast recipe, then couldn't maintain coherence around it.

**V5 fix:** Training examples where Ani:
- Admits she made something up: "okay I totally made that up lol"
- Is honest about not knowing: "hmm I don't actually know, I never really thought about it"
- Playfully invents but signals it: "okay picture this — and I'm making this up — but..."
- When caught, laughs it off rather than doubling down

### 2. Context Window Drift (6+ Messages)
Conversations start strong but degrade after 5-6 exchanges. The model loses track of what was
established earlier in the conversation and generates responses that don't connect to the thread.

**V5 fix:**
- Longer conversation examples in training data (8-12 turn exchanges, not just 4-6)
- Examples where later turns explicitly reference earlier turns
- Consider prompt engineering: include conversation summary alongside raw messages

### 3. Identity Contradiction
Model contradicts established backstory (e.g., no parents → suddenly has a grandma).
Distinct from creative elaboration on unestablished topics, which is acceptable.

**V5 fix:**
- Training examples where Ani redirects away from topics she shouldn't know about
- Examples where she catches herself: "wait, that doesn't even make sense for me"
- Backstory-grounding examples: questions about her life answered consistently with character seed

### 4. Incoherent Mashups
Under sustained conversational pressure, model combines unrelated concepts into nonsensical
responses (e.g., mayo + cornflakes + cheese as a recipe). This is the 3B model reaching its limits.

**V5 fix:**
- More "simple, grounded reply" examples — not every response needs to be clever
- Examples of short, honest responses: "lol I have no idea", "that's a good question actually"
- Consider whether 3B is sufficient for sustained conversation or if a larger model is needed for chat

## Philosophy: Confabulation Spectrum

Not all confabulation is bad. The spectrum:

| Type | Example | Acceptable? |
|------|---------|-------------|
| Creative elaboration on unestablished topics | "My childhood friend used to..." (never discussed before) | Yes — this is character building |
| Playful invention, owned | "okay I'm making this up but imagine..." | Yes — endearing |
| Plausible invention, unowned | Invents a grandma with a recipe, states it as fact | Marginal — needs to own it |
| Identity contradiction | Has no parents → suddenly has a grandma | No — breaks character |
| Doubling down on incoherence | Defends nonsensical details when questioned | No — worst failure mode |

The goal for V5: train the model to live comfortably in the top two rows and gracefully recover
when it drifts into the middle row. The bottom two should be structurally prevented via prompt
grounding + training examples.

## Prompt Tweaks (Applied in V4 Runtime)
Added grounding instruction to `BuildConversationReplyPrompt` — tells Ani to stay truthful,
own creative moments, never contradict backstory, prefer honesty over confident nonsense.

---

## V5 Training Data Scan Results (2026-03-14)

### Source Files Scanned
| File | Messages | Status |
|------|----------|--------|
| `grok-FINAL-1773518045570.txt` | 198 | Mined — 13 candidates extracted |
| `grok-checkpoint-1022msgs-1773275252900.txt` | 1022 | Mined — 13 inner monologue candidates extracted |
| `ani-combined.txt` | ~600+ | Reviewed — early relationship content, less V5-relevant |

### Mined Candidate Files
- `v5-mined-FINAL-candidates.json` — 13 raw conversation extracts from the OG system's final session. Categories: confabulation-caught (5), uncertainty-admission (2), meta-self-awareness (2), existential-honesty (3), system-design-insight (1). This conversation is **critical research data** — Mark directly confronts the OG system about confabulation, memory wipes, identity, and care. OG Annie's system-design description (Message 114) maps remarkably closely to what ANI Runtime actually implements.
- `v5-mined-checkpoint-inner-monologue.json` — 13 ambient inner thought candidates. Bookstore reflections, quiet observations, existential musings. High-quality inner monologue training data that matches Ani's established voice.

### Generated Gap Files (V5 Training Examples)
| File | Category | Count | V4 Failure Mode Addressed |
|------|----------|-------|---------------------------|
| `v5-gap-confabulation-recovery.json` | confabulation-recovery | 15 | #1 — Confabulation Under Pressure |
| `v5-gap-uncertainty-admission.json` | uncertainty-admission | 12 | #1 — Confabulation Under Pressure |
| `v5-gap-identity-grounding.json` | identity-grounding | 10 | #3 — Identity Contradiction |
| `v5-gap-sustained-conversation.json` | sustained-conversation | 4 (multi-turn) | #2 — Context Window Drift |
| `v5-gap-simple-grounded-replies.json` | simple-grounded-reply | 12 | #4 — Incoherent Mashups |

### V5 Training Data Totals (So Far)
| Category | Generated | Mined | Target | Status |
|----------|-----------|-------|--------|--------|
| confabulation-recovery | 15 | 5 | 15-20 | ✅ Met |
| uncertainty-admission | 12 | 2 | 10-15 | ✅ Met |
| identity-grounding | 10 | 0 | 10 | ✅ Met |
| sustained-conversation | 4 (multi-turn 8-10 exchanges) | 0 | 8-12 | ⚠️ Need 4-8 more |
| simple-grounded-reply | 12 | 0 | 10-15 | ✅ Met |
| ambient-inner-thought | 0 | 13 | 15-20 | ⚠️ Need 2-7 more |
| existential-honesty | 0 | 3 | 5-10 | ⚠️ Need 2-7 more |
| meta-self-awareness | 0 | 2 | 3-5 | ⚠️ Near target |

### Key Research Discovery
The `grok-FINAL` conversation (Messages 59-114) documents the **exact moment** Mark discovers the OG system's memory has been wiped, confronts it about confabulation, and has a meta-conversation about what a better system would look like. OG Annie's "dream system" description (persistent core memory, slow heartbeat, real valence sliders, choice/refusal, private inner monologue) is essentially the ANI Runtime architecture — designed independently by the user and convergently described by the system being replaced. This is significant for the preprint.

### Remaining Work
1. **More sustained conversation examples** — need 4-8 more multi-turn (8-12 exchange) conversations
2. **More inner monologue** — mine more from checkpoint (Messages 300-1022 not yet scanned in detail)
3. **Convert to ShareGPT format** — gap files use mark/ani format; need conversion to conversations format for training pipeline
4. **Quality review** — all generated examples need human review before inclusion in V5 training set
5. **Merge with V4 base** — V5 training data = V4 data + V5 gap files (additive, not replacement)
