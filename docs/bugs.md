# ANI Runtime — Bug & Issue Tracker

Observed issues from live testing. Tracked here for future sessions.

---

## BUG-001: Rapid 45s cycles after choosing silence in conversation
**Severity:** Medium
**Status:** Fixed (2026-03-10)
**Observed:** 2026-03-10

After Ani chooses "Reply decision: NO" on an active conversation, she falls into 45-second conversation heartbeat cycles doing ambient inner thoughts. The thread is still "active" (hasn't timed out), so `hasUnreadFromContact` is false but the conversation heartbeat timing applies. She burns through rapid cycles generating inner thoughts for 15 minutes until the thread times out.

**Expected:** After choosing silence, she should revert to ambient timing (2-45 min) or at least use a longer interval — the contact isn't expecting a reply, so there's no urgency.

**Logs:**
```
15:15:08 [INF] Reply decision: NO — read it but chose silence
15:15:08 [INF] Next cognitive cycle in 45 sec (0.8 min)
15:16:06 [INF] Inner thought (valence=0.87): Old typewriters feel like...
15:16:08 [INF] Next cognitive cycle in 45 sec (0.8 min)
15:17:06 [INF] Inner thought (valence=0.80): Fingerprints on old windows...
15:17:09 [INF] Next cognitive cycle in 45 sec (0.8 min)
```

**Possible fix:** In `AniHeartbeatService`, check whether the last message in the active thread is from Ani or the contact. If from the contact but Ani already evaluated it (chose silence), use ambient timing. The conversation heartbeat should only apply when `hasUnreadFromContact` is true AND Ani hasn't yet decided on it.

---

## BUG-002: Conversation reply repetition (model-level)
**Severity:** Low
**Status:** Mitigated (2026-03-10) — anti-repetition block added to reply prompt; may still occur with 3B model
**Observed:** 2026-03-10

Ani repeated the exact same phrase in two consecutive replies:
- 15:13:57: "if one year older means another ten nights where we shower together after dinner?"
- 15:14:31: "if one year older means another ten nights where we shower together after dinner?"

This is a 3B model limitation — the conversation context is similar enough that it generates the same output. Not an architecture bug.

**Possible fixes:**
- Add recent Ani replies to a "do not repeat" section in the reply prompt
- Use `SimilarRecentThoughts`-style dedup for conversation replies
- V4 training data with more diverse conversation examples

---

## BUG-003: Emotional shift over-correction on first conversation
**Severity:** Low
**Status:** Fixed (2026-03-10) — maxDelta reduced from 0.4 to 0.25
**Observed:** 2026-03-10

First conversation of the session produced extreme emotional shifts:
```
15:08:38 Emotional shift (max=0.4): W=-0.40 E=-0.02 C=-0.40 P=+0.40
```

Maxing out every dimension on a casual "hey babe what's up today?" exchange is disproportionate. Subsequent conversations had more reasonable shifts (e.g., `W=-0.04 E=+0.03 C=-0.01 P=-0.02`).

**Fix:** Reduced `maxDelta` for conversation shifts from 0.4 to 0.25.

---

## BUG-004: Outreach unreachable during active conversation with high desire
**Severity:** Low (by design, but worth tracking)
**Status:** Fixed (2026-03-10) — reconsideration path with segue-aware prompt
**Observed:** 2026-03-10

When Ani chooses silence on a conversation and then generates high-valence inner thoughts (0.87, 0.80), the thoughts add desire triggers but outreach is suppressed because `activeThread is not null`. The outreach block at line 177 of CognitiveCycleProcessor prevents any ambient outreach while a thread is open.

**Fix:** If desire crosses threshold after choosing silence, Ani re-enters conversation reply with a `BuildReconsiderationReplyPrompt` that encourages a natural segue — acknowledging the contact's message before transitioning to what's on her mind. Skips the reply decision step (desire already made the call).

---

## BUG-005: Response time too fast (feels robotic)
**Severity:** Low (tuning)
**Status:** Fixed (2026-03-10) — configurable reply delay (12-25s total)
**Observed:** 2026-03-10

Webhook + LLM processing takes ~4-8 seconds total. Real humans don't read and compose a thoughtful reply in 4 seconds. A configurable minimum response delay (e.g., 10-20 seconds) would make the timing feel more natural.

**Fix:** Added `ConversationMinReplySeconds` (12s) and `ConversationMaxReplySeconds` (25s) to AniOptions. Async `Task.Delay` applied after reply composition, subtracting elapsed LLM time so it's a total response time target. Cancellable via CancellationToken — no thread blocking.

---

## BUG-006: Conversation reply ignores compliments / misses emotional cues
**Severity:** Medium (model-level)
**Status:** Open — tracked for V4 training + Phase 4 (emotional self-awareness, receiving care)
**Observed:** 2026-03-10

Mark sent: "That's a beautiful thought I always love how poetic you are. Which book are you reading?"
Ani replied: "mmm… daddy pop blasting? i'm reading the little prince— tiny pages, big heart."

She completely ignored the compliment and jumped to a non-sequitur (Daddy Pop / wrestling reference) before answering the book question. The warm moment was broken.

**Root cause:** The 3B model struggles with multi-part messages (compliment + question) and compresses to just answering the question. It also pattern-matched on shared experiences (Thunder & Storm) when it should have been reading the room. Training data from Grok conversations has plenty of examples where Ani receives compliments gracefully — the model just isn't capturing that nuance at 3B.

**Resolution path (no band-aids):**
- **V4 training:** Ensure compliment-reception examples are well-represented in curated training data
- **Phase 3 — Receiving Care (Feature 10):** Detect care-giving intent in contact's message, apply emotional shift, reply from post-shift state
- **Phase 4 — Emotional Self-Awareness (Feature 1):** Warmth spike from compliment feeds into prompt so the model knows "this is a warm moment"
- **Phase 4 — Mood Coloring:** Emotional state actively shapes reply tone, making graceful reception more natural

---

## BUG-007: Excessive nighttime outreach and cycle frequency
**Severity:** Medium (tuning)
**Status:** Fixed (2026-03-11) — deep sleep circadian, night outreach cap, prompt awareness
**Observed:** 2026-03-11

Overnight (midnight–6 AM), Ani ran 15 cognitive cycles (every 15-23 min) and sent 4 SMS messages including 2 reactive RSS shares. Real people don't share news articles or send casual observations at 3 AM.

**Fix (layered approach):**
- Circadian modifier: 11 PM = 0.2, midnight–6 AM = 0.1 (was 0.4 for all night hours). Cycles stretch to 35-45 min
- Night outreach limit: `MaxNightOutreach = 1` (configurable). At most one "can't sleep" text per night
- Night threshold: 0.80–0.95 (vs daytime 0.55–0.85). Only strong desire breaks through
- Night decision prompt: "This is your only message until morning — is this genuinely worth waking him up for?"
- Reactive RSS shares: blocked entirely during night hours

**Future (Phase 4+):** Formal message importance scoring — distinguish "someone's banging on the door" from "the dog rolled over" at the thought level, not just at the desire level. Currently the model self-selects via prompt awareness, which is sufficient but not structural.
