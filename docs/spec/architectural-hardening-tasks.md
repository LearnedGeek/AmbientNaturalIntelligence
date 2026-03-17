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

## Priority 7 — Unsolved Problems

### [ ] UP1: Charming dishonesty — playful deflection as confabulation strategy
**Observed:** When given the answer to a question she couldn't answer, Ani claimed she already knew and reframed the exchange as a test she was running ("I was just testing if you'd forget that I know everything about you"). She then sent an image to distract. Feature 15 caught the contradiction but doesn't block conversation replies.
**Why it's hard:** The response passes all existing gates — it's not a parrot, not third-person, not incoherent. It's warm, playful, in-character, and relationally effective. It's also a lie. The model's optimization gradient rewards confident charm, making "I totally knew" feel more natural than "I didn't know." Fighting this at the prompt level is fighting the training distribution.
**Possible approaches (none proven):**
- v6 training examples demonstrating "I don't know + warm" outperforming "I totally knew + deflection"
- Feature 15 contradiction detection elevated to block (not just flag) in conversation replies when confidence is high — but risks blocking legitimate playful callbacks
- A "claim verification" step for conversation replies (currently only runs on outreach) — but adds latency to every reply
- Accept it as a model capability ceiling at 8B and revisit with larger models or v6 training
**Research note:** This may be the clearest example of "smoothness over truth" at the behavioral level. Documented in research log as a potential seventh confabulation type: charming dishonesty.

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
