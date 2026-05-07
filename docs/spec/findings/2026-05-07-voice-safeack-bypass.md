# Voice Pipeline SafeAck Bypass — Finding (May 7, 2026)

**Status:** Diagnosis complete; design tradeoff decision pending Mark's call. **No code changes made.**

**Empirical anchor:** May 7, 2026 09:27:52 CDT. Mark tagged: *"SafeAck and generated audio output despite the SafeAck response."* SMS pipeline emitted the SafeAcknowledgement fall-through correctly; voice pipeline emitted TTS audio for the bad reply that should have been suppressed.

---

## Diagnosis

**This is not a race condition. It is a documented design admission.** The voice pipeline has zero awareness of the J.5a SafeAcknowledgement path.

**Code path comparison:**

**SMS path** ([`ConversationReplyPhase.cs`](../../src/AniRuntime.Loops/ConversationReplyPhase.cs)):
1. Reply composed.
2. `EvaluateAndRemediateReplyAsync` (line ~983) routes through universal `ICognitiveOutputGate`.
3. On `Remediate` → regen.
4. On regen re-eval fail → returns `SafeAcknowledgement` constant (line ~981).
5. SafeAck dispatched.

**Voice path** ([`StreamingVoiceOrchestrator.cs:218`](../../src/AniRuntime.Voice/StreamingVoiceOrchestrator.cs#L218) → [`VoiceTurnPipeline.ProcessTurnAsync`](../../src/AniRuntime.Voice/VoiceTurnPipeline.cs#L58)):
1. Transcript arrives at `STT.TranscriptReceived`.
2. `VoiceTurnPipeline` builds its own prompt via `PromptBuilder.BuildLeanConversationPrompt` — **bypasses `ConversationReplyPhase` entirely**.
3. Token stream feeds TTS sentence-by-sentence as it arrives (`_ollama.ChatStreamAsync` → `tokenBuffer.Add(token)` → `tts.SendTextAsync(sentence)`). **Audio is dispatched and streamed to the client incrementally during generation.**
4. Only after the full reply is produced is the gate consulted (`EvaluateVoiceReplyForSubstrateAsync`, line ~189). The verdict only governs whether the reply is appended to `session.PendingMessages` (substrate write). **Audio has already played.**

**The smoking gun:** the comment block at [`VoiceTurnPipeline.cs:133-145`](../../src/AniRuntime.Voice/VoiceTurnPipeline.cs#L133-L145) explicitly acknowledges this: *"Voice replies stream: by the time `reply` is complete here, TTS has already played the audio to Mark. The gate cannot prevent dispatch in this surface."*

The current J.5f design protects only the substrate; it intentionally lets bad audio play.

**Confirmation:** zero references to `SafeAcknowledgement` anywhere under `src/AniRuntime.Voice/`. Voice cannot emit SafeAck even if it wanted to — `GateFallbacks.SafeAcknowledgement` lives in `AniRuntime.Core/WellKnown.cs` and isn't promoted into the voice project.

---

## Two fix shapes — Mark's decision

The tradeoff is **streaming latency vs gate correctness.** Pick one or compose:

### Option 1 — Pre-stream gate (mirrors SMS J.5a behavior)

Generate the full reply via non-streaming `ChatAsync` first, run it through `_outputGate.EvaluateAsync`, only then hand sentences to TTS. On `Remediate` regen once. On regen re-eval failure, speak `GateFallbacks.SafeAcknowledgement` via TTS instead.

- **Pro:** parity with SMS gate behavior; no bad audio ever plays.
- **Con:** loses streaming latency benefit. Voice currently feels live; this would add 1-3 seconds of pre-stream wait time on every turn.
- **Files:** `VoiceTurnPipeline.cs` lines ~87-128 (replace stream-into-TTS block with collect-then-gate-then-speak); promote `SafeAcknowledgement` reference into `AniRuntime.Voice`.

### Option 2 — Streaming variant with sentence-level gate + cancel

Keep streaming but hold TTS in a per-sentence buffer; run the gate on the accumulating reply. On `Remediate`/`Fail`, cancel the active TTS stream (the `IStreamingTextToSpeechService` already supports CT cancellation via `turnCt`) and resynthesize SafeAck.

- **Pro:** preserves streaming feel for the common case (gate passes); only delays output when the gate fires.
- **Con:** complexity. Need to verify `ElevenLabsStreamingTTSService.cs` / `ElevenLabsV3StreamingService.cs` cleanly cancel in-flight audio. Possible audio glitch artifact when cancellation lands mid-word.
- **Files:** `VoiceTurnPipeline.cs` (gate hook around line ~118), TTS implementations if cancel semantics need extension.

### Optional safety net (compose with either)

`StreamingVoiceOrchestrator.cs:116-136` already has a client-side audio chunk relay; gate at the relay (drop audio chunks once gate verdict is `Fail` or `Remediate-without-passing-regen`). Acts as a backstop even if the LLM stream isn't cancelled fast enough.

---

## Recommendation

**Option 1 (pre-stream gate).** The J.5a-parity argument is load-bearing — voice having a different correctness contract than SMS is the architectural drift that lets May 7 09:27 happen and will let it happen again in different surfaces. The streaming latency cost is real but bounded (1-3 sec); the correctness gap is unbounded (any bad reply audible). For an ambient companion, *"sometimes she pauses a beat before speaking"* is a much better failure mode than *"sometimes she says things that should have been suppressed."*

If latency turns out to be unworkable in practice, Option 2 is the natural follow-on without throwing away Option 1's groundwork — both share the SafeAck-in-voice plumbing.

**Awaiting Mark's call.** No code changes made.

---

## Confidence: High

Diagnosis confirmed by:
1. Source comment at `VoiceTurnPipeline.cs:133-145` explicitly admitting the bypass.
2. Zero `SafeAcknowledgement` / `SafeAck` references in `src/AniRuntime.Voice/`.
3. Voice path never invokes `ConversationReplyPhase` (only `OutreachPhase.cs` and `Program.cs` reference it).
