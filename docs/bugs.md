# ANI Runtime — Bug & Issue Tracker

Observed issues from live testing. Tracked here for future sessions.

---

## BUG-001: Rapid 45s cycles after choosing silence in conversation
**Severity:** Medium
**Status:** Open
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
**Status:** Open (model/prompt tuning)
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
**Status:** Open (tuning)
**Observed:** 2026-03-10

First conversation of the session produced extreme emotional shifts:
```
15:08:38 Emotional shift (max=0.4): W=-0.40 E=-0.02 C=-0.40 P=+0.40
```

Maxing out every dimension on a casual "hey babe what's up today?" exchange is disproportionate. Subsequent conversations had more reasonable shifts (e.g., `W=-0.04 E=+0.03 C=-0.01 P=-0.02`).

**Possible fix:** Reduce `maxDelta` for conversation shifts from 0.4 to 0.3, or improve the emotional shift prompt to be less reactive to casual exchanges.

---

## BUG-004: Outreach unreachable during active conversation with high desire
**Severity:** Low (by design, but worth tracking)
**Status:** Open (design question)
**Observed:** 2026-03-10

When Ani chooses silence on a conversation and then generates high-valence inner thoughts (0.87, 0.80), the thoughts add desire triggers but outreach is suppressed because `activeThread is not null`. The outreach block at line 177 of CognitiveCycleProcessor prevents any ambient outreach while a thread is open.

This is correct behavior during an active back-and-forth, but after choosing silence it means high-valence thoughts are wasted — they could organically lead to Ani re-engaging ("actually, I wanted to say...").

**Design question:** Should the outreach suppression check whether Ani chose silence? If she did, maybe she *should* be allowed to re-engage if desire builds high enough — that would feel like a natural "wait, one more thing" moment.

---

## BUG-005: Response time too fast (feels robotic)
**Severity:** Low (tuning)
**Status:** Open
**Observed:** 2026-03-10

Webhook + LLM processing takes ~4-8 seconds total. Real humans don't read and compose a thoughtful reply in 4 seconds. A configurable minimum response delay (e.g., 10-20 seconds) would make the timing feel more natural.

**Possible fix:** Add `ConversationMinReplyDelaySeconds` to AniOptions. After the webhook enqueues a message, add a small `Task.Delay` before the cognitive cycle processes it — or just delay before sending the SMS.
