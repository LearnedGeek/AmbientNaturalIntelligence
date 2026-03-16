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

## Positive Patterns (Preserve)

- Clean interface design for perception sources, actions, streaming services
- Thread-safe VoiceSessionState and DebouncedUtterance
- DesireEngine as single write path
- Null Object pattern for emergence
- Consistent ConfigureAwait(false), no .Result/.Wait()
- Semantic deduplication in memory
- Dual-file Serilog strategy
- Perception source resilience
