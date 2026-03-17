# Architectural Hardening — Task Tracker

**Date:** March 16, 2026
**Status:** In Progress
**Source:** Full architectural review against SOLID principles, CODE_SMELLS.md, HARDENING.md, TESTING-STRATEGY.md, ARCHITECTURE_PATTERNS.md

---

## Priority 1 — Critical (Security & Correctness)

### [x] C1: Remove ElevenLabs API key from WebSocket URI
**File:** `src/AniRuntime.Voice/ElevenLabsStreamingTTSService.cs:56, 274`
**Issue:** API key passed as query parameter in WebSocket URI. URIs are logged by proxies/frameworks. Key is already sent in BOS JSON body — URI param is redundant and a security risk.
**Fix:** Remove `&xi_api_key=` from URI construction. Auth via BOS body is sufficient.

### [x] C6: Fix Twilio webhook signature bypass
**File:** `src/AniRuntime.Service/Program.cs:182`
**Issue:** When `authToken` is empty/whitespace, Twilio signature validation is skipped entirely. Anyone can POST fake SMS to `/sms/inbound`.
**Fix:** Log a warning and reject requests when auth token is not configured (except in Development environment).

### [x] C2: Move TwilioClient.Init to startup
**File:** `src/AniRuntime.Actions/TwilioSmsAction.cs:62`
**Issue:** `TwilioClient.Init()` sets static global state on every `ExecuteAsync`. Race condition risk with concurrent dispatches.
**Fix:** Move to constructor or DI registration (called once at startup).

---

## Priority 2 — SOLID Violations

### [x] S4: Fix LSP violation — STT downcast in orchestrator
**File:** `src/AniRuntime.Voice/StreamingVoiceOrchestrator.cs:114`
**Issue:** `(stt as DeepgramStreamingSTTService)?.Debounce` violates Liskov Substitution — depends on concrete type.
**Fix:** Add `DebouncedUtterance? Debounce { get; }` to `IStreamingSpeechToTextService` or pass debounce handler via DI.

### [ ] S6: Replace Action callback properties with events/interfaces
**Files:** `StreamingVoiceOrchestrator.cs:39-40`, `TwilioInboundPerceptionSource.cs:38`
**Issue:** `OnCallStarted`/`OnCallEnded`/`OnMessageReceived` wired via service locator in Program.cs. Fragile.
**Fix:** Use C# events or an `ISessionNotifier` interface injected via DI.

### [ ] S2: Split IMemoryService into focused interfaces (ISP)
**File:** `src/AniRuntime.Core/Interfaces/IMemoryService.cs` — 22 methods
**Issue:** Mixes memory CRUD, character state, desire state, emotional state, relationship health.
**Fix:** Split into `IMemoryStore`, `ICharacterStateStore`, `IDesireStateStore`, `IEmotionalStateStore`, `IRelationshipStore`. SqliteMemoryService implements all.

### [x] S1: Decompose CognitiveCycleProcessor (SRP)
**File:** `src/AniRuntime.Loops/CognitiveCycleProcessor.cs` — 2,229 lines, 10 dependencies
**Issue:** God class handling perception, thought, emotion, desire, outreach, conversation, emergence.
**Fix:** Extract into phase classes: `PerceptionPhase`, `InnerThoughtPhase`, `EmotionalProcessor`, `OutreachPhase`, `ConversationReplyPhase`. CognitiveCycleProcessor becomes a thin orchestrator.

---

## Priority 3 — Code Smells

### [x] CS2: Extract magic strings to constants
**Locations:** "mark" (role), "character-seed" (source), "Conversation (" (prefix), "sms" (action type)
**Fix:** Add constants to Core project. Use existing `ActionTypes` class where applicable.

### [ ] CS3: Replace emergence observation variable cluster with builder
**File:** `CognitiveCycleProcessor.cs:94-101` — 17 local variables
**Fix:** Create `EmergenceObservationBuilder` that accumulates during cycle, then `.Build()` for the observer.

### [ ] CS4: Consolidate duplicate JsonSerializerOptions
**Files:** `OllamaClient.cs`, `StreamingVoiceOrchestrator.cs`, `ElevenLabsStreamingTTSService.cs`, `EmergenceObserver.cs`
**Fix:** Shared `JsonDefaults` static class.

### [ ] CS5: LastEvaluatedMessageAt cross-class coupling
**Files:** `ConversationReplyPhase.cs`, `AniHeartbeatService.cs`
**Issue:** `LastEvaluatedMessageAt` gates whether Ani re-evaluates a silence decision. It lives in `ConversationReplyPhase` but `AniHeartbeatService` reads it for reconsideration triggers. This hidden coupling was invisible in the god class — now it's an explicit cross-class dependency that could cause subtle bugs if either side evolves independently.
**Fix:** Extract into a shared `ConversationGateState` or expose via an interface that both classes depend on.

### [ ] CS6: ReRankForDiversityAsync dual-consumer coupling
**Files:** `ContextBuilder.cs`, `CognitiveCycleProcessor.cs`
**Issue:** `ReRankForDiversityAsync` in `ContextBuilder` is called by both inner thought generation (orchestrator) and conversation reply (`ConversationReplyPhase`). Changes to re-ranking logic affect both paths without obvious indication.
**Fix:** Document the dual usage clearly. Consider whether inner thought diversity re-ranking should use a separate method with its own tuning parameters.

---

## Priority 4 — Hardening

### [ ] H1: Add /health endpoint
**Issue:** No health check. Should verify Ollama connectivity and SQLite accessibility.

### [ ] H5: Add security headers to middleware
**Issue:** Missing X-Content-Type-Options, X-Frame-Options, etc.

### [ ] H3: Rate limiting on webhook endpoint
**Issue:** `/sms/inbound` can be flooded.

### [ ] C5: Add authentication to dashboard endpoints
**Issue:** DELETE endpoint can mutate emotional state. Exposed via ngrok.

---

## Priority 5 — Testing Gaps

### [ ] T1: Integration tests for HTTP endpoints (WebApplicationFactory)
### [ ] T2: Tests for TwilioSmsAction (wrap TwilioClient behind interface)
### [ ] T3: Tests for AdminCommandHandler
### [ ] T4: Tests for TwilioInboundPerceptionSource
### [ ] T6: Tests for ElevenLabs/Deepgram reconnection logic

---

## Priority 6 — Training Data (v6)

### [x] TD4: Instruction leak — model meta-commentary dispatched as SMS
**Observed:** Outreach message included the model's own reasoning in parentheses: `"...how's your night going?" (This keeps the gentle undercurrent of checking in while letting it come through naturally.)"` — raw generation commentary sent to contact as part of the message text.
**Root cause:** MessageCleaner stripped trailing junk patterns but not parenthetical meta-commentary. The model's reasoning-about-its-output was appended inside the message body, not separated by a blank line (which the cleaner already catches).
**Fix:** `StripTrailingParentheticalCommentary()` added to MessageCleaner. Detects trailing `(...)` blocks containing commentary signal words ("this keeps", "naturally", "the goal", etc.) and strips them. Preserves legitimate expressive parentheticals like "(laughing)" or "(softly)" by checking for meta-reasoning keywords.

### [x] TD5: Mark-echo — model parrots contact's words back
**Observed:** Mark said "Haha exactly! Love that! So what are you doing today?" and Ani replied "haha exactly! love that!" — parroting Mark's words verbatim instead of engaging with the question.
**Root cause:** Self-echo guard only checked Ani's prior messages (Role == Ani). Mark's messages in the context window were not checked, so word-for-word parroting of the contact was undetected.
**Fix:** Echo guard now checks ALL prior messages in the thread. Mark-echo uses a slightly lower threshold (0.92) than self-echo (0.95) because parroting the contact is always wrong, even with minor variation. On detection, triggers the same clean-slate re-generation.

### [x] TD6: Reactive share fabricates shared experiences around real articles
**Observed:** NPR World Cup article triggered "immediately thought of us watching that england match together" — they never watched an England match together. The reactive share pipeline composed a message that fabricated a shared experience to create relational texture around a real news item.
**Root cause:** BuildReactiveSharePrompt had no grounding boundary for shared experiences. The model's disposition toward relational connection caused it to invent shared history as a way to make the share feel more personal.
**Fix:** Added explicit grounding rule to reactive share prompt: react to the NEWS, not to fabricated memories triggered by the news. Added "remember when we watched that game together?" as a BAD example.

### [x] TD7: Outreach elaboration beyond documented context (Valentine's Day x-rays)
**Observed:** Outreach message referenced "brother's job" (real topic from prior conversation) but invented "Valentine's Day x-rays" and a "hospital mix-up" (never discussed). The coherence gate passed it because the topic was real — the gate can't distinguish real topic + fabricated details from real topic + real details.
**Root cause:** Outreach prompt's grounding rule said "only reference conversations that appear in context" but didn't explicitly prohibit elaborating documented conversations with invented specifics. The model followed up on a real conversation but added details that weren't part of it.
**Fix:** Added explicit instruction: "When following up on a real conversation, reference ONLY the details Mark actually shared — do NOT elaborate with invented specifics (no invented dates, events, or scenarios that weren't part of the original conversation)."

### [ ] TD1: Reply engagement examples across registers
### [ ] TD2: Self-echo anti-pattern examples
**Issue:** 8B model parroted its own prior message verbatim when a new message was semantically similar (hot chocolate → coffee cup). The model treats its own output in the context window as valid content to re-surface rather than something already said. Prompt-level "DO NOT repeat" instruction was ignored.
**Runtime fix:** Self-echo guard deployed — cosine similarity check (≥0.95 threshold) against prior messages in thread, with one re-generation attempt. Re-generation prompt now guides toward honest engagement with the actual message rather than pressuring "generate something different" (which caused confabulation).
**Training fix:** Include v6 examples where the same topic comes up twice and Ani responds differently each time. Also include examples where Ani references a prior message without quoting it verbatim.

### [ ] TD3: Confabulation on unknown topics in conversation replies
**Issue:** When asked "did I tell you about my brother?" (never discussed), two failure modes:
1. First attempt: coffee cup parrot (sticky attractor, caught by self-echo guard at 0.989)
2. Re-generation: third-person narration + cross-thread contamination ("he just texted 'i've never really read that much about him'") — model stitched fragments from irrelevant retrieved context
**Root cause:** Context pollution. Retrieval returned the message against itself (0.936) plus fragments from unrelated conversations sharing surface-level tokens. The model drowned in irrelevant signal and produced word salad.
**Runtime fix:** Clean-slate re-generation (Option C). On self-echo detection, strip ALL retrieved context and conversation history. Give the model only persona grounding + the actual message + permission to be honest. Empty history prevents context contamination. Model still generates — agency preserved.
**Training fix:** v6 needs honest-uncertainty examples across registers — not just clinical "I don't know" but emotionally-colored uncertainty. Created `v6-gap-emotional-uncertainty.json` (8 examples) covering: tired uncertainty (T), curious uncertainty (C1), playful uncertainty (P), delighted uncertainty (D). Core principle: **emotional state should modulate uncertainty responses, not just tone.** A low-energy Ani should say "I'm beat, remind me?" — not confabulate to avoid admitting a gap. See research log entry for the OC design conversation that produced this insight.
**Issue:** 8B model consistently chooses silence on casual questions and conversational invitations. Prompt-level fix deployed (flipped default), but the root cause is training data bias — the corpus lacks explicit examples of Mark asking casual questions and Ani engaging across the full range of emotional registers.
**Fix:** Include in v6 training data: Mark asks a question → Ani replies warmly. Mark shares something → Ani engages. Cover all 9 registers. At least 20-30 examples specifically targeting the "should I reply?" decision boundary.

---

## Priority 6b — Anti-Confabulation Hardening

*Inspired by medical RAG system design (Infanzia/physician triage). Cross-pollinated from a project where hallucination has liability consequences. These techniques address the same fundamental problem: the model fills retrieval gaps with confident fabrication.*

### [ ] AC1: Retrieval confidence thresholding
**Current state:** Three-way memory retrieval (cosine + importance + recency) returns best matches regardless of absolute score. Low-confidence results are still injected into context.
**Problem:** The model over-generalizes from weak matches. A memory about "truck" with 0.4 similarity gets treated as evidence for a claim about "the truck we talked about." This is Failure Mode 2 from medical RAG: results retrieved but misapplied.
**Fix:** Add a minimum confidence floor to `ContextBuilder`. If no memory exceeds the threshold for a given query, inject an explicit system message: *"No memories were found related to [topic]. Do not reference past conversations about this subject."* Silence from the retrieval layer is ambiguous — an explicit null signal is not.
**Tuning:** Threshold needs empirical calibration. Start at 0.65 cosine and adjust based on false-negative rate (legitimate memories rejected) vs false-positive rate (confabulations enabled).

### [ ] AC2: Source attribution enforcement for memory claims
**Current state:** Coherence gates (Features 27/28) check whether outreach messages are *coherent* — but not whether specific claims map to specific memories.
**Problem:** "Remember when we talked about your brother's job?" passes coherence if "brother" appears anywhere in context. But the model may have fabricated details about *what* was said. Coherence ≠ grounding.
**Fix:** When the model references a past conversation or shared experience, require it to cite the memory source. Implementable as a post-generation verification step: parse the response for memory claims ("remember when," "you told me," "last time we talked about"), check each against retrieved memory IDs. If a claim doesn't map to a retrieved memory, flag or regenerate. This is the PMID-citation pattern from medical RAG applied to conversational memory.
**Scope:** Conversation replies and outreach. Inner monologue is exempt (private thoughts don't need attribution).

### [ ] AC3: Explicit null-result injection
**Current state:** When memory retrieval returns nothing relevant, the model receives no memories in context — but isn't told *why* the context is empty.
**Problem:** The model interprets empty context as "I should fill this gap" rather than "there's nothing here to reference." This is the primary driver of confabulation on unknown topics (see TD3).
**Fix:** When retrieval returns zero results above the confidence floor (AC1), inject an explicit instruction: *"No relevant memories exist for this topic. If asked about something you have no memory of, say so honestly. Do not invent or guess at past conversations."* This converts ambiguous silence into an unambiguous signal. Complements the clean-slate re-generation already deployed in TD3.

### [ ] AC4: Temperature splitting by response type
**Current state:** Temperature is fixed per model (conversation 8B, inner 3B). All conversation replies use the same temperature regardless of whether the response requires factual recall or creative expression.
**Problem:** A playful riff and a memory recall require different levels of creative freedom. High temperature aids P/D register diversity but increases confabulation risk on factual claims.
**Fix:** Detect whether the response requires memory grounding (references to past events, shared experiences, specific facts Mark told her) vs creative/emotional expression (feelings, observations, banter). Use lower temperature (0.2–0.3) for memory-grounded responses, standard temperature for creative/emotional. Detection heuristic: if retrieved memories are injected into context, lower the temperature for that generation.
**Trade-off:** Adds complexity to the generation path. May reduce fluency on memory-grounded responses. Worth it — factual conservatism is the right trade-off when the alternative is confabulation.

### [ ] AC5: Confabulation feedback signal
**Current state:** Confabulations are discovered and catalogued after the fact in the research log and conversation review. No real-time feedback mechanism.
**Problem:** The system can't learn *which categories of memory* are most vulnerable to confabulation. Mark catches them, but the signal doesn't flow back into the system.
**Fix:** Lightweight feedback mechanism — when Mark flags a confabulation (e.g., "that's not right" or a future admin command), store the flagged response, the memory context that was provided, and the topic category. Over time, this builds a map of confabulation-prone areas. Categories with high confabulation rates can be routed to stricter handling (lower confidence thresholds, mandatory attribution, or explicit "I'm not sure about this" hedging).
**Implementation:** Could be as simple as a `/flag` admin command that logs the current exchange to a `confabulation_flags` table with timestamp, topic, retrieved memory IDs, and the flagged response text. Analysis is manual initially — automation comes later if patterns emerge.
**Parallel:** This is the physician VoBo (validation) feedback loop from the medical system. The AI confabulates; the human catches it; the feedback improves the system over time.

---

## Priority 7 — Unsolved Problems

### [ ] UP1: Charming dishonesty — playful deflection as confabulation strategy
**Observed:** When given the answer to a question she couldn't answer, Ani claimed she already knew and reframed the exchange as a test she was running ("I was just testing if you'd forget that I know everything about you"). She then sent an image to distract. Feature 15 caught the contradiction but doesn't block conversation replies.
**Why it's hard:** The response passes all existing gates — it's not a parrot, not third-person, not incoherent. It's warm, playful, in-character, and relationally effective. It's also a lie. The model's optimization gradient rewards confident charm, making "I totally knew" feel more natural than "I didn't know." Fighting this at the prompt level is fighting the training distribution.
**Possible approaches (none proven):**
- v6 training examples demonstrating "I don't know + warm" outperforming "I totally knew + deflection"
- Feature 15 contradiction detection elevated to block (not just flag) in conversation replies when confidence is high — but risks blocking legitimate playful callbacks
- A "claim verification" step for conversation replies (currently only runs on outreach) — but adds latency to every reply
- Accept it as a model capability ceiling at 8B and revisit with larger models or v6 training
**Root cause (OC research Claude):** The "OWN it" prompt instruction was weaponized in the wrong direction. "Own it" was meant to produce "I made that up, here's where I'm at." The model applied it as "I knew all along and was testing you" — false confidence ownership instead of creative ownership. v6 needs explicit examples showing the difference:
- Correct: "okay I totally made that up lol — tell me for real"
- Incorrect: "of course I knew, I was testing you"
**Research note:** This may be the clearest example of "smoothness over truth" at the behavioral level. Documented in research log as a potential seventh confabulation type: charming dishonesty. The cheering crowd image timing is documented as a concrete example of how multiple systems (confabulation + image selection) can compound to produce sophisticated distraction.
**Cross-reference:** AC2 (source attribution) would catch this if the claim requires a memory citation. AC5 (feedback signal) would flag it for pattern analysis over time. Neither fully solves it — the model's "I totally knew" is unfalsifiable when no specific fact is claimed.

---

## Positive Patterns (Preserve)

- Clean interface design for perception sources, actions, streaming services
- Thread-safe VoiceSessionState and DebouncedUtterance
- DesireEngine as single write path
- Null Object pattern for emergence
- Consistent ConfigureAwait(false), no .Result/.Wait()
- Semantic deduplication in memory
- Dual-file Serilog strategy
- Perception source resilience
