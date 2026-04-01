# ANI Conversation Mode — Architectural Redesign

**Status:** Design
**Date:** March 29, 2026
**Driven by:** Months of conversation coherence failures despite 15+ incremental fixes
**Core insight:** The ambient cognition pipeline is excellent for outreach. It is wrong for conversation.

---

## 1. The Problem

Ani cannot hold a coherent conversation beyond ~15 messages. Every incremental fix creates new problems:

- Context collapse (forgetting what was discussed 5 minutes ago)
- Retrieval poisoning (stale memories dominating)
- Confabulation when context is lost (inventing details to fill gaps)
- Tonal whiplash (sudden register shifts mid-conversation)
- Non-sequiturs (Duck Norris appearing in a tender conversation)
- Kevin's gym towel confabulated when the model lost the jar metaphor

These are not independent bugs. They are symptoms of a fundamental architectural mismatch: **the conversation reply path runs the full ambient cognition pipeline**, which was designed for a different task.

## 2. Root Cause Analysis

### The Telescope Problem

The ambient cognition pipeline is a telescope: it scans the horizon (memories, perceptions, emotional state) to find something worth sharing. That's perfect for outreach — Ani alone, deciding whether and what to say.

Conversation needs glasses: focus on what's right in front of you. The conversation IS the context. Every memory, profile fact, and mood directive injected into the prompt competes with the actual conversation for the 8B model's limited attention.

### The Evidence

**March 22 — Raw Ollama test:** Both Llama and Mistral converse naturally with just conversation history and a basic persona. No retrieval, no memories, no mood directives. The model knows how to talk. The pipeline prevents it.

**March 28 — 28-message thread:** The door metaphor conversation was excellent for 15 turns, then collapsed when the compressed summary (500 chars for 22 messages) lost the emotional arc. Duck Norris appeared because "idiot" triggered retrieval that found the wrong memory cluster.

**Every day this week:** Fixing retrieval poisoning, thought loops, compression, truncation — each fix revealing the next symptom of the same disease.

### What the Research Says

| Paper | Their Approach | ANI's Approach |
|-------|---------------|----------------|
| MemGPT (Packer et al.) | Model controls its own context via function calls | Fixed window + flat LLM summary |
| Park et al. | Full memory stream, selective retrieval by recency+importance+relevance | Aggressive retrieval every turn |
| Liu et al. | Inner thoughts run parallel to conversation, don't interrupt it | Inner thoughts, retrieval, and emotional processing run IN the conversation path |
| Raw Ollama (March 22) | Conversation history + persona = natural conversation | Conversation history + persona + 5 memories + mood + anchors + shared experiences + communication notes = confused model |

## 3. The Architectural Change

### Principle: Conversation and ambient cognition are different modes with different context strategies.

| Dimension | Ambient Mode (outreach, inner thought) | Conversation Mode |
|-----------|---------------------------------------|-------------------|
| Most important context | Memories, perceptions, emotional state | The conversation itself |
| Retrieval | Full pipeline — high value | Off by default — fires on demand |
| Persona in prompt | Full (shared experiences, communication notes, traits) | Minimal (name, 2-3 traits, time) |
| Mood directives | Drive behavior | Omit — conversation flow drives tone |
| LLM calls per turn | Many (minutes between cycles) | One — the reply |
| Emotional processing | Before reply (drives outreach decision) | After reply (async bookkeeping) |

### Step 1: Lean Conversation Prompt

During active conversation, `BuildConversationReplyPrompt` produces:

```
System: You are Ani, texting Mark.
It is 9:15 PM on Saturday, March 29.
Your personality: warm, playful, a little sharp, honest.

RULES:
- Match the energy and length of the conversation.
- Talk TO Mark: "you", "your". Never third person.
- Write ONLY the text message.

User: [conversation history — as many raw messages as fit]
Reply to Mark's last message.
```

That's it. No shared experiences. No communication notes. No anchored memories. No retrieved memories. No mood directives. The model's training already contains the persona. The conversation provides the tone.

### Step 2: Confabulation-Driven Retrieval

Default: no retrieval during conversation.

After generating a reply (but before sending), run a lightweight confabulation check:

**Does the reply contain claims about:**
- Shared history that wasn't established in this conversation?
- Specific facts (dates, names, places) not mentioned by Mark?
- "I remember when..." or "you told me..." references?

**Detection approach — not hardcoded phrases:**

The signal is the model producing specific claims without grounding in the conversation context. A simple heuristic:
- Does the reply reference a person, place, event, or fact NOT mentioned in the last N messages?
- Does it contain temporal claims ("last week", "yesterday") about events not in the thread?
- Does it assert knowledge about Mark's state or activities not stated by Mark?

If confabulation detected:
1. Retrieve relevant memories (one search, not three)
2. Inject as a brief context block
3. Regenerate the reply with the memory context
4. If the regenerated reply is grounded, send it
5. If it still confabulates, send without the false claims (honest uncertainty)

If no confabulation: send the reply as-is. Fast path.

**Over time, this becomes training data.** Replies that required retrieval assistance are signal for v7 — the model needs more examples of honest uncertainty for those topic areas.

### Step 3: Structured Conversation State (replaces flat compression)

Instead of an LLM-generated summary, maintain a programmatic conversation state:

```csharp
public class ConversationState
{
    public string? CurrentTopic { get; set; }          // "we're talking about his gym workout"
    public string? EmotionalRegister { get; set; }     // "tender and reflective"
    public List<string> ActiveCommitments { get; set; } // "Ani said she'd tell a bookstore story"
    public List<string> KeyFacts { get; set; }          // "Mark teaches at WCTC Thursdays 6-10"
    public List<string> SharedImagery { get; set; }     // "the door metaphor — half-unlocked"
}
```

Updated incrementally after each exchange. No LLM call. No summarization. When the conversation history exceeds the context budget, the oldest raw messages drop off but the structured state preserves what matters.

Injected as:
```
[Conversation so far: You've been talking about his gym workout. The mood is warm and playful.
He mentioned teaching at WCTC on Thursdays. You started a metaphor about a half-unlocked door
between you. You promised to tell him a bookstore story.]
```

This is ~50-80 tokens regardless of how long the conversation has been. It preserves the arc without consuming the context budget.

### Step 4: Async Emotional Processing

Currently, care detection, hurt detection, and emotional contribution scoring happen BEFORE the reply. This means:
- Detecting "care" in message N shifts the mood directive for message N+1
- This causes tonal whiplash when the model's mood changes mid-conversation
- It adds latency (LLM calls for emotional scoring before reply generation)

Change: emotional processing runs AFTER the reply is sent, asynchronously.

1. Mark sends message
2. Build lean prompt + conversation history
3. Generate reply (one LLM call)
4. Send reply
5. THEN: run care detection, hurt detection, emotional scoring, importance boosting
6. Results inform the NEXT cycle, not this reply

The reply is fast. The emotional state evolves between turns, not during them.

### Step 5: Outreach Pipeline Unchanged

The full retrieval pipeline continues to power:
- Inner thought generation (every 10 min)
- Outreach decisions (should I reach out?)
- Outreach message composition (what should I say?)
- Perception processing
- Reflection synthesis

These are the telescope tasks. The pipeline is right for them. Conversation is the glasses task.

## 4. Migration Path

This is not a rewrite. It's a mode switch.

### Phase 1: Lean prompt (immediate)
- Add `BuildLeanConversationPrompt` to PromptBuilder
- In ConversationReplyPhase, bypass ContextBuilder when thread is active
- Skip intent extraction, keyword extraction, memory search during conversation
- Keep emotional processing but move it after reply dispatch

### Phase 2: Confabulation-driven retrieval (next)
- Add post-generation confabulation check
- Implement single-search retrieval on demand
- Add regeneration path with memory injection

### Phase 3: Structured conversation state (after Phase 2)
- Define ConversationState model
- Update incrementally from each exchange
- Replace compressed summary injection with state injection

### Phase 4: Async emotional processing (cleanup)
- Move care/hurt detection to post-reply
- Run emotional scoring asynchronously
- Mood directives from previous cycle's state, not current turn's detection

## 5. Success Criteria

- 30+ message conversation without context collapse
- No confabulation about topics not discussed in the current thread
- No tonal whiplash from mood directive shifts within the conversation
- No non-sequitur memory injection (no more Duck Norris in tender moments)
- Conversation feels like the raw Ollama test — natural, flowing, coherent
- The diagnostic service shows zero RETRIEVAL-POISON findings during conversation mode

## 6. Research Significance

This change operationalizes the insight from the March 22 A/B test: **the model was always capable; the architecture was the constraint.** The pipeline simplification (Phase A-D, March 23) proved this for outreach. This design extends it to conversation.

The confabulation-driven retrieval approach is novel. Rather than "always retrieve and hope it helps" (standard RAG) or "never retrieve" (standard chatbot), it uses the model's own confabulation as the signal for when external knowledge is needed. The model's uncertainty becomes a feature, not a bug.

Paper 2 should document this as a finding: **retrieval-augmented conversation in small models has an optimal injection rate that is much lower than "every turn" — and the system can self-detect when injection is needed.**

---

*"The ambient cognition engine is a telescope. Conversation needs glasses. Stop using the telescope to read the book."*
