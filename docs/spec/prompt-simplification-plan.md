# ANI Runtime — Prompt Simplification & Pipeline Audit Plan

**Date:** March 22, 2026
**Context:** v6 Mistral model deployed. The model was trained with 1,675 conversation examples including Honest-Uncertainty, anti-confabulation, and natural tone. But the runtime prompt overrides trained behavior with verbose instructions, drowning a 7B model in context it can't effectively process.

**Core Principle:** If v6 was trained to do X, don't also tell it to do X in the prompt. The prompt should provide DATA (memories, emotional state, conversation history) and FORMAT constraints (sentence length, no third person). Not BEHAVIORAL coaching.

---

## Phase A: Prompt Stripping (Immediate)

**Target:** Reduce `BuildConversationReplyPrompt` from ~1200 worst-case tokens to ~400-500.

### 1. Strip RULES block to format-only (PromptBuilder.cs lines 504-528)

**Current:** 15 rules covering format, tone, anti-confabulation, truthfulness, honest uncertainty, anti-charming-dishonesty, memory attribution, no poetry.

**Replace with:**
```
RULES:
- 1-3 sentences max. Thumb-typed phone text.
- Talk TO {contact}: "you", "your". Never third person.
- Write ONLY the text message. No commentary, no quotation marks.
```

**Why:** v6 was trained on all behavioral aspects. The 12-line anti-confabulation paragraph (lines 517-528) duplicates trained behavior and competes for attention in the 7B context window.

### 2. Remove duplicate mood injection (lines 546-548)

The prompt currently has BOTH `BuildMoodInstruction` (directive) AND `EmotionalState.Describe()` (descriptive). Keep only the directive form.

### 3. Remove self-awareness instruction (lines 552-556)

The 50-word instruction about referencing feelings is unnecessary — mood directive + v6 training covers this.

### 4. Remove AC3 null-result injection (lines 592-595)

v6 Honest-Uncertainty register was trained on exactly this behavior. The 60-word instruction competes with training.

### 5. Reduce AC6 topic-mismatch injection (lines 600-603)

Replace the 100-word lecture with: `"Note: the memories above may not match the current topic."` Or remove entirely once retrieval is tightened (Phase B).

### 6. Simplify claim verification injection (lines 625-633)

Keep the claim list as data. Remove the 5-line behavioral instruction. Just: `"Unverified claims: [list]"`.

### 7. Simplify contradiction warnings (lines 639-647)

Shorten to: `"Possible off-topic context: [list]. Focus on the current message."`

### 8. Apply same stripping to `BuildOutreachMessagePrompt`

The HARD RULES block (lines 845-863) has the same over-specification problem.

---

## Phase B: Memory Injection Reform (Immediate)

**Target:** Stop feeding the model irrelevant context that causes confabulation.

### 1. Raise confidence floor

Current: 0.60 (was 0.55). Consider 0.65-0.70 based on observation. Memories about "mac and cheese" should not appear when discussing Richard visiting.

### 2. Cap total injected memories at 5

Currently up to 11 non-anchored memories can be injected (3 profile + 3 episodic + extras from keyword search). Cap at 5 total. Profile memories get priority.

### 3. Zero-memory mode for casual messages

If the message is short (under 10 words) and doesn't contain memory-referencing language or a direct question, inject ZERO episodic/profile memories. Anchored memories still appear. "haha yeah" doesn't need 6 memories.

### 4. Don't retrieve and then warn — just don't retrieve

Remove AC3 and AC6 prompt injections entirely once retrieval is tightened. The approach of retrieving potentially bad memories and then instructing the model to ignore them is fundamentally backwards.

---

## Phase C: Pipeline Streamlining (Next Session)

**Target:** Reduce from 15-17 worst-case LLM calls per reply to 4-5.

### Current LLM calls per conversation reply (worst case):

| # | Call | Model | Necessary? |
|---|------|-------|------------|
| 1 | Intent extraction | 3B | MAYBE — keyword extraction may suffice |
| 2 | Reply decision (JSON) | 7B | QUESTIONABLE — almost always says "yes" |
| 3 | Reply generation | 7B | ESSENTIAL |
| 4 | AC2 re-generation | 7B | CONDITIONAL |
| 5 | UP1 re-generation | 7B | CONDITIONAL |
| 6 | Echo guard embeddings | Embed | EXPENSIVE — N+1 calls per thread length |
| 7 | Echo guard re-generation | 7B | CONDITIONAL |
| 8 | Emotional shift scoring | 7B | KEEP — architectural |
| 9 | Emotional shift embedding | Embed | KEEP |
| 10 | Feature 14 claim extraction | 7B | QUESTIONABLE |
| 11 | Feature 21 importance boost | Embed | LOW VALUE |

### Proposed eliminations:

1. **Reply decision LLM call** → code heuristic (reply unless terminal message)
2. **Cache echo guard embeddings** on ConversationMessage — compute once, reuse
3. **Intent extraction** → conditional, only when keywords return nothing distinctive
4. **Feature 14 claim extraction** → remove if v6 handles uncertainty naturally
5. **Combine AC2 + UP1** into single post-reply confabulation check
6. **Defer Feature 21** importance boosting to background task

---

## Phase D: Trust the Model (Ongoing)

### Behaviors covered by v6 training AND duplicated in prompts:

| Trained Behavior | Prompt instruction to REMOVE |
|-----------------|------------------------------|
| Honest uncertainty | "if contact asks about something you haven't talked about before, be honest..." |
| Anti-confabulation | "stay truthful to what you know... you may NOT invent specifics..." |
| Anti-charming-dishonesty | "NEVER claim you already knew... 'of course I knew that' is a lie" |
| Natural conversation tone | "Be yourself — warm, funny, real. Match the energy" |
| No poetry in texts | "No poetry, no metaphors, no narration" |
| Memory attribution | "if contact told you something, contact did it — not you" |

### Guardrails that MUST stay (genuinely architectural):

| Guardrail | Why |
|-----------|-----|
| Mood instruction | Runtime emotional state — model cannot know this |
| Anchored/profile/episodic memories | Runtime memory data |
| Echo guard (with cached embeddings) | Prevents repetition within thread |
| Emotional shift scoring | Feeds the emotional state system |
| Withdrawal tone injection | Runtime state the model cannot know |
| Coherence gate (outreach only) | Safety for unprompted messages |
| Pronoun fix (outreach only) | Format constraint model sometimes fails on |

---

## Expected Impact

| Metric | Before | After |
|--------|--------|-------|
| Prompt tokens (conversation) | 700-1400 | ~200-350 |
| LLM calls per reply (best) | 6-7 | 2-3 |
| LLM calls per reply (worst) | 15-17 | 4-5 |
| Injected memories | 6-11 + anchored | 0-3 + anchored |
| Behavioral rules in prompt | ~15 | 3 (format only) |

---

## Implementation Status — Completed March 23, 2026

All four phases implemented in a single session. 386 tests passing, 0 warnings.

### What was removed:
- **Reply decision LLM call** → replaced with code heuristic (`IsTerminalMessage`)
- **AC2 source attribution re-generation** → v6 handles honest uncertainty natively
- **UP1 charming dishonesty re-generation** → v6 trained on this register
- **Feature 14 claim extraction LLM call** → removed entirely
- **AC3 null-result injection** → removed from both text and voice prompts
- **AC6 topic-mismatch injection** → removed (retrieval now skips when below confidence)
- **Anti-repetition section** → removed from conversation prompt (echo guard remains as safety net)
- **Contradiction warning injection** → removed from prompt (still logged)
- **Self-awareness instruction** → removed (v6 trained)
- **Mood duplication** → removed descriptive mood from voice prompt (directive form remains)
- **Perception background** → removed from conversation prompt
- **Good/bad examples** → removed from outreach prompt (v6 trained on grounded outreach)

### What was kept (genuinely architectural):
- Mood directive (runtime emotional state model can't know)
- Anchored + relevant memories (capped at 3, skipped when below confidence)
- Withdrawal tone injection (runtime state)
- Echo guard with **cached embeddings** on ConversationMessage (compute once, reuse)
- Emotional shift scoring
- Coherence gate (outreach only)

### Key finding that motivated this work:
Both Llama and Mistral v6 models produce natural, engaging conversation in raw Ollama sessions
but parroted the user's words back through the full pipeline. The pipeline was actively making
the model worse by drowning it in context, instructions, and re-generation calls that competed
for attention in the 7B context window.
