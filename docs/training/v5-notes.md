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
