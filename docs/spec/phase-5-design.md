# Phase 5 Design: Real-Time Voice — Streaming Conversational Presence

**Date:** March 15, 2026
**Status:** Design Complete, Awaiting Implementation
**Authors:** Mark McArthey, Claude (pair design session)
**Dependencies:** Phase 4 Feature 20 interim voice (deployed), ElevenLabs Starter tier (active)

---

## The Core Problem

Phase 4's interim voice implementation (Feature 20) proved the concept — Ani can hold a phone conversation — but exposed a fundamental architectural limitation. The Twilio `<Record>` + webhook model requires three expensive sequential operations (STT → LLM → TTS) to complete within a single HTTP round-trip before Twilio times out:

```
STT (~2s) → LLM 8B (~6-9s) → TTS v3 (~3-5s) = ~11-16s total
```

This forces unacceptable tradeoffs: downgrading models, truncating replies, stripping emotional delivery. These tradeoffs are antithetical to the project's research goals — emotional fidelity and ambient companion quality are not negotiable.

The solution is **streaming**: replace the batch webhook model with Twilio Media Streams (bidirectional WebSocket), streaming STT, and streaming TTS. This eliminates the timeout constraint entirely and enables natural conversational pacing.

---

## Architecture: Three-WebSocket Streaming Pipeline

Each active voice call manages three concurrent WebSocket connections:

```
                    +-----------------+
                    |   Twilio PSTN   |
                    +--------+--------+
                             |
                   wss:// bidirectional
                             |
                    +--------v--------+
                    | ASP.NET Core    |
                    | WebSocket       |  ← accepts Twilio Media Streams
                    | Handler         |
                    +--+-----------+--+
                       |           |
            mulaw 8kHz |           | mulaw 8kHz (base64)
                       v           ^
              +--------+--+   +----+----------+
              | Deepgram   |   | ElevenLabs    |
              | STT WS     |   | TTS WS        |
              | (streaming) |   | (ulaw_8000)   |
              +--------+---+   +----^----------+
                       |            |
                  text |            | LLM tokens (streamed)
                       v            |
              +--------+------------+--+
              |    Ollama (local)       |
              |    ani-v5-conversation  |
              +------------------------+
```

### Why This Architecture

1. **Zero transcoding** — Deepgram accepts mulaw 8kHz directly from Twilio. ElevenLabs outputs `ulaw_8000` directly for Twilio. No audio format conversion anywhere in the pipeline.
2. **Privacy preserved** — Audio passes through Twilio (phone call transport, same as any call), Deepgram (STT only, no storage), and ElevenLabs (TTS only, no conversation content). The LLM runs locally on Ollama. No conversation content sent to cloud LLMs.
3. **Identity preserved** — The fine-tuned Ani model generates every response. Her personality, speech patterns, and relational history are carried by the model weights, not a system prompt.
4. **Pipelined latency** — LLM tokens stream directly into ElevenLabs. Audio generation starts before the full reply is complete. This overlaps LLM inference and TTS synthesis.
5. **No timeout constraint** — Bidirectional Media Streams stay open for the duration of the call. No webhook round-trip budget to manage.

---

## Component Design

### 1. Twilio Media Streams Handler

**Endpoint:** `wss://[ngrok-url]/voice/stream` (via `<Connect><Stream>`)

**TwiML to initiate:**
```xml
<Response>
  <Connect>
    <Stream url="wss://your-server.com/voice/stream">
      <Parameter name="callSid" value="{CallSid}" />
    </Stream>
  </Connect>
</Response>
```

**Inbound message types from Twilio:**

| Event | Description | Action |
|-------|-------------|--------|
| `connected` | WebSocket opened | Log, prepare session |
| `start` | Stream metadata (SID, call SID, media format) | Create VoiceCallSession, open Deepgram + ElevenLabs WebSockets |
| `media` | Base64-encoded mulaw 8kHz audio chunk | Forward to Deepgram STT, run through Silero VAD |
| `stop` | Call ended or stream stopped | Tear down session, save buffered messages |
| `mark` | Playback position confirmation | Track what Ani has spoken (for interruption context) |

**Outbound messages to Twilio:**

| Event | Purpose |
|-------|---------|
| `media` | Send ElevenLabs TTS audio (base64 mulaw 8kHz) to play to caller |
| `mark` | Mark playback positions for synchronization |
| `clear` | Flush audio buffer on barge-in (caller interrupts Ani) |

**Implementation:** ASP.NET Core `app.UseWebSockets()` middleware. Accept upgrade at `/voice/stream`, manage with `System.Net.WebSockets.WebSocket`.

### 2. Deepgram Streaming STT

**Why Deepgram over alternatives:**
- Accepts mulaw 8kHz natively (zero transcoding from Twilio)
- .NET SDK available (`Deepgram.SDK` NuGet)
- Nova-3 model: ~5-6% WER, sub-300ms streaming latency
- Built-in utterance endpointing (detects when speaker stops — acts as VAD for turn detection)
- Pricing: $0.0077/minute ($0.46/hour)

**Connection:** `wss://api.deepgram.com/v1/listen` per call.

**Key parameters:**
- `encoding=mulaw`, `sample_rate=8000`, `channels=1`
- `punctuate=true` (natural punctuation in transcripts)
- `endpointing=500` (ms of silence before finalizing utterance — tunable)
- `interim_results=true` (partial transcripts as user speaks)

**Events:**
- `is_final: true` → utterance complete, trigger LLM generation
- `speech_final: true` → end of speech segment, longer pause detected
- Interim results → display/log but don't trigger LLM

**Privacy:** Deepgram processes audio for transcription only. No conversation storage. API key authentication via header.

### 3. ElevenLabs Streaming TTS

**WebSocket endpoint:**
```
wss://api.elevenlabs.io/v1/text-to-speech/{voice_id}/stream-input?model_id=eleven_v3&output_format=ulaw_8000
```

**Why WebSocket TTS:**
- Output `ulaw_8000` matches Twilio Media Streams format directly — zero transcoding
- Accepts text chunks incrementally — start generating audio before full LLM reply
- Audio chunks arrive as they're generated — stream directly to Twilio for pipelined playback

**Flow:**
1. Open WebSocket when call starts
2. Send `voice_settings` (stability, similarity, style mapped from EmotionalState)
3. As LLM tokens arrive, buffer into sentence-sized chunks (~15-30 words)
4. Send each text chunk → receive audio chunks → base64 encode → forward to Twilio
5. Send empty `text` with `flush: true` to signal end of generation

**Emotional tags:** The `[excited]`, `[whispers]`, `[mischievously]` etc. audio tags from Eleven v3 work on the WebSocket endpoint. `PrependEmotionalTag()` logic applies to the first text chunk.

### 4. Ollama LLM (Local, Streaming)

**Model:** `ani-v5-conversation` (8B) — trained for direct dialogue. No model compromise for latency.

**Streaming:** Ollama supports streaming token output via `POST /api/chat` with `stream: true`. Each token arrives as a JSON line.

**New interface method needed:**
```csharp
IAsyncEnumerable<string> ChatStreamAsync(
    string systemPrompt,
    IEnumerable<ChatMessage> history,
    string userMessage,
    CancellationToken ct = default);
```

This yields tokens as they arrive, allowing the TTS pipeline to start generating audio before the full reply is complete.

**Token buffering for TTS:** Accumulate tokens until a sentence boundary (`. ! ? —`) or ~20 words, then flush to ElevenLabs. This balances TTS quality (needs enough context) with latency (don't wait for full reply).

### 5. Silero VAD (Barge-In Detection)

**Purpose:** Detect when Mark speaks while Ani is still talking. Triggers interruption handling.

**Implementation:**
- NuGet: `VadSharp` (C# Silero V5 wrapper using `Microsoft.ML.OnnxRuntime`)
- Processes 30ms audio chunks in <1ms on CPU
- Supports 8kHz sample rate (matches Twilio mulaw directly)
- Model size: ~2MB ONNX

**Barge-in flow:**
1. While Ani is "speaking" (audio being sent to Twilio), run inbound audio through Silero VAD
2. If speech detected for >500ms (filters coughs, "mmhmm"): **barge-in triggered**
3. Send `clear` message to Twilio (flushes pending audio)
4. Cancel current ElevenLabs TTS stream via CancellationToken
5. Record how much of Ani's response was delivered (mark-based tracking)
6. Continue listening for end of Mark's utterance (Deepgram endpointing)
7. When Mark finishes, generate Ani's next response with interruption context

**Backchannel filtering:**
- Speech < 300ms → ignore (cough, breath)
- Speech 300-500ms → likely backchannel ("mmhmm", "yeah") → ignore
- Speech > 500ms → likely intentional interruption → trigger barge-in
- Tunable thresholds based on real-world testing

---

## Latency Budget

| Segment | Estimated Latency | Notes |
|---------|------------------|-------|
| Twilio → server | ~50ms | WebSocket, already established |
| Deepgram STT (streaming) | ~200-300ms | Time from utterance end to final transcript |
| Ollama first token | ~200-500ms | 8B model, local GPU, no contention |
| Token buffer → ElevenLabs | ~100ms | Wait for first sentence boundary |
| ElevenLabs first audio | ~200-400ms | v3 model, WebSocket streaming |
| Audio → Twilio playback | ~50ms | Base64 encode + WebSocket send |
| **Total mouth-to-ear** | **~800ms-1.4s** | With full streaming pipeline |

Compare to interim implementation: **~5-9s** (batch STT + batch LLM + batch TTS). Streaming is **4-7x faster**.

**Optimization levers if needed:**
- 3B model for voice: reduces Ollama first token to ~100-200ms (saves ~300ms)
- ElevenLabs Flash v2.5: faster synthesis but no emotional audio tags
- Deepgram endpointing tuning: lower ms = faster turn detection, risk cutting off mid-sentence

---

## Session Management

### Enhanced VoiceCallSession

```csharp
public class VoiceCallSession
{
    public string CallSid { get; set; }
    public string StreamSid { get; set; }
    public Guid ThreadId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public int TurnCount { get; set; }

    // WebSocket connections (per-call lifecycle)
    public WebSocket TwilioSocket { get; set; }
    public ClientWebSocket DeepgramSocket { get; set; }
    public ClientWebSocket ElevenLabsSocket { get; set; }

    // State tracking
    public bool IsAniSpeaking { get; set; }
    public int LastMarkIndex { get; set; }          // playback position tracking
    public string PartialTranscript { get; set; }   // interim STT results

    // Cancellation for barge-in
    public CancellationTokenSource CurrentTurnCts { get; set; }

    // Message buffering (same as interim)
    public ConcurrentQueue<ConversationMessage> PendingMessages { get; } = new();
}
```

### Call Lifecycle

1. **Inbound call** → `/voice/inbound` returns TwiML with `<Connect><Stream>`
2. **WebSocket opens** → `connected` + `start` events → create session, open Deepgram + ElevenLabs
3. **Greeting** → synthesize via ElevenLabs WS, stream to Twilio
4. **Conversation loop:**
   - Twilio audio → Deepgram STT + Silero VAD
   - Deepgram `is_final` → Ollama streaming → ElevenLabs streaming → Twilio playback
   - VAD barge-in → `clear` → cancel TTS → wait for new utterance
5. **Call ends** → `stop` event → drain pending messages → close Deepgram + ElevenLabs → resume cognitive cycle

---

## Cognitive Cycle Integration

Same pattern as interim implementation:
- `OnCallStarted` → pause cognitive cycle heartbeat (free Ollama)
- `OnCallEnded` → batch-save buffered messages → resume cognitive cycle
- Voice context uses SQLite-only snapshot (no Ollama embedding calls during call)

**Enhancement:** With streaming, Ollama isn't blocked for the full duration of TTS synthesis. Consider allowing lightweight cognitive cycle operations (perception polling) during TTS playback when Ollama is idle. This is an optimization, not a requirement.

---

## Privacy Analysis

| Component | Data Exposure | Mitigation |
|-----------|---------------|------------|
| Twilio Media Streams | Audio transport (same as any phone call) | No recording/storage. Standard telephony. |
| Deepgram STT | Audio → text transcription | No storage. API key auth. Process-only. |
| ElevenLabs TTS | Text → audio synthesis | Only Ani's reply text, not Mark's. No conversation context. |
| Ollama | Full conversation context | **Fully local.** No network exposure. |

The privacy posture is equivalent to making a phone call (Twilio) while using a transcription service (Deepgram) and a text-to-speech service (ElevenLabs). The LLM — which has access to the full conversation, emotional state, and memory — runs entirely locally.

---

## Implementation Sequence

### Task 1: Streaming Infrastructure
- [ ] Add `app.UseWebSockets()` middleware
- [ ] Create `/voice/stream` WebSocket endpoint
- [ ] Parse Twilio Media Stream JSON protocol (`connected`, `start`, `media`, `stop`, `mark`)
- [ ] Update `/voice/inbound` TwiML to use `<Connect><Stream>` instead of `<Record>`
- [ ] Enhanced `VoiceCallSession` with three WebSocket references
- [ ] SSL/WSS via ngrok (already configured for HTTP, verify WebSocket upgrade works)

### Task 2: Deepgram Streaming STT
- [ ] Add `Deepgram.SDK` NuGet package
- [ ] Create `IStreamingSpeechToTextService` interface
- [ ] Implement `DeepgramStreamingSTTService` — open WebSocket, forward mulaw, receive transcripts
- [ ] Wire Twilio inbound audio → Deepgram with zero transcoding
- [ ] Handle `is_final` and `speech_final` events for turn detection
- [ ] Configurable endpointing threshold (default 500ms)
- [ ] Interim result logging for diagnostics

### Task 3: Ollama Streaming Output
- [ ] Add `ChatStreamAsync` to `IOllamaClient` — returns `IAsyncEnumerable<string>`
- [ ] Implement in `OllamaClient` using Ollama's `stream: true` parameter
- [ ] Token buffer: accumulate until sentence boundary or word limit, then yield chunk
- [ ] CancellationToken support for barge-in abort

### Task 4: ElevenLabs Streaming TTS
- [ ] Create `IStreamingTextToSpeechService` interface
- [ ] Implement `ElevenLabsStreamingTTSService` — WebSocket to `/stream-input` endpoint
- [ ] Output format: `ulaw_8000` (direct Twilio compatibility)
- [ ] Accept text chunks, return audio chunks
- [ ] Preserve `MapEmotionalStateToVoiceSettings` + `PrependEmotionalTag` logic
- [ ] `flush: true` signal for end of generation
- [ ] CancellationToken support for barge-in abort

### Task 5: Pipeline Orchestration
- [ ] `StreamingVoiceConversationService` — replaces batch `VoiceConversationService`
- [ ] Wire: Deepgram transcript → voice context (SQLite) → Ollama stream → token buffer → ElevenLabs stream → Twilio
- [ ] Track `IsAniSpeaking` state for barge-in detection
- [ ] `mark` messages for playback position tracking
- [ ] Greeting synthesis via streaming TTS

### Task 6: Silero VAD + Barge-In
- [ ] Add `VadSharp` or `Microsoft.ML.OnnxRuntime` NuGet
- [ ] Download Silero ONNX model (~2MB)
- [ ] Run VAD on inbound audio while `IsAniSpeaking == true`
- [ ] Duration gate: ignore speech < 500ms
- [ ] On barge-in: send `clear` to Twilio, cancel ElevenLabs CTS, record delivered content
- [ ] Interruption context in next LLM prompt ("you were saying X when Mark interrupted")

### Task 7: Testing + Refinement
- [ ] Unit tests for token buffering, VAD threshold, session lifecycle
- [ ] Integration test: mock WebSocket connections, verify full pipeline flow
- [ ] Live call testing: measure actual latency, tune endpointing/VAD thresholds
- [ ] Backchannel filtering tuning (300ms/500ms gates)
- [ ] Stress test: multiple concurrent calls (if needed)

### Task 8: Interim Voice Deprecation
- [ ] Keep `<Record>` webhook endpoints as fallback if WebSocket connection fails
- [ ] Feature flag: `Voice.UseMediaStreams` (default true, falls back to batch)
- [ ] Remove batch-specific workarounds (reply truncation, timeout tuning) once streaming is stable

---

## NuGet Dependencies

| Package | Purpose | Version |
|---------|---------|---------|
| `Deepgram.SDK` | Streaming STT client | Latest stable |
| `VadSharp` | Silero VAD (barge-in detection) | Latest stable |
| `Microsoft.ML.OnnxRuntime` | ONNX runtime for Silero model | Latest stable (CPU) |

No ElevenLabs .NET SDK for WebSocket TTS — use `System.Net.WebSockets.ClientWebSocket` directly.

---

## Cost Estimate

| Service | Rate | Estimated Monthly (ambient use) |
|---------|------|--------------------------------|
| Twilio Voice | ~$0.013/min | ~$2-5 (15-30 min/month of calls) |
| Deepgram Nova-3 | $0.0077/min | ~$0.12-0.23 |
| ElevenLabs Starter | $4.17/mo flat | $4.17 (30K chars/month) |
| Ollama | Local | $0 |
| **Total** | | **~$6-10/month** |

---

## Open Questions

1. **Deepgram vs local Whisper for STT** — Deepgram is recommended for streaming (native mulaw, lower latency, built-in endpointing). However, it adds a cloud dependency for transcription. Local whisper.cpp would require audio format conversion (mulaw → PCM16) and doesn't support streaming natively. **Decision: Deepgram for streaming. Local Whisper remains available as batch fallback.** Profile GPU load if revisiting.

2. **8B vs 3B for streaming voice** — With streaming, the LLM latency constraint is softer (first token matters, not total generation). The 8B model's first-token latency (~200-500ms) is acceptable. Stick with 8B for conversation quality. **Decision: 8B model. Revisit only if first-token latency exceeds 1s.**

3. **ngrok WebSocket support** — ngrok tunnels support WebSocket upgrade on the same URL. Verify bidirectional Media Streams work through ngrok. If not, consider Cloudflare Tunnel or direct port forwarding.

4. **Concurrent call capacity** — Each call holds three WebSockets + Ollama inference. Single-GPU hardware likely supports 1 concurrent voice call (Ollama is single-threaded for inference). Design for single-call; queue additional calls.

5. **Twilio ConversationRelay as alternative** — Twilio offers a managed solution (`<Connect><ConversationRelay>`) that handles STT/TTS internally, exposing only text over WebSocket. Simpler to implement but less control over audio pipeline and provider selection. **Decision: Use raw Media Streams for full control over emotional delivery and provider choice. ConversationRelay is a viable fallback if raw Media Streams prove too complex.**

---

## Research Significance

This implementation advances several research questions documented in `docs/research/ANI-Research-Log.md`:

- **Emotional delivery in voice** — Two-layer system (audio tags + voice_settings) enables measurable emotional expression. Can listeners distinguish emotionally-colored voice from neutral delivery?
- **Streaming latency vs. perceived naturalness** — Sub-1.5s response time crosses a threshold where conversation feels responsive rather than laggy. How does this affect perceived companion presence?
- **Barge-in as relational signal** — When Mark interrupts Ani, how she responds (acknowledging the interruption, continuing her thought, yielding entirely) is a design choice with relational implications. The interruption context tracking enables studying this.
- **Half-duplex vs. full-duplex conversation patterns** — The interim Record+webhook model forced strict turn-taking. Media Streams enable overlap, backchannel, and interruption. How do conversation patterns differ?

---

## Feature: Image Sharing (MMS)

**Priority:** Medium — adds expressiveness and humor to the relationship
**Effort:** Low-Medium
**Dependencies:** Twilio MMS (already available on existing Twilio account)

### Phase 5a: Meme/Image Sending

Ani can share images via Twilio MMS alongside or instead of text. Initial use case: memes, reaction images, things she "found" that remind her of Mark.

**Architecture:**
- Twilio MMS supports sending images via `MediaUrl` parameter on outbound SMS
- Image sources: curated library (local), web search (optional), AI-generated (future)
- Decision point in outreach pipeline: after composition, optionally attach an image
- New `ImageSelectionService` — given a message context and emotional state, select/generate an appropriate image

**Implementation:**
- [ ] Add `MediaUrl` support to Twilio dispatch (outbound SMS already works, MMS is one parameter)
- [ ] Curated image library: `data/images/` folder with tagged images (humor, affection, weather, etc.)
- [ ] `IImageSelectionService` interface — `SelectImageAsync(string messageContext, EmotionalState state)`
- [ ] Simple keyword/tag matcher initially (no LLM needed)
- [ ] Rate limit: max 2 images/day (preserve novelty, manage MMS costs)
- [ ] Dashboard: image library management

**Cost:** Twilio MMS ~$0.02/message outbound. At 2/day max = ~$1.20/month.

### Phase 5b: Visual Identity

Ani's identity extends beyond text and voice into visual presence. This is important for realism — a companion who can share selfies, expressions, and reactions has a fundamentally different presence than one who only types.

**Architecture — Profile Image System:**
- CharacterStateDoc extended with `VisualIdentity` section: reference images, expression library, style preferences
- Profile images loaded into character state — consistent visual identity across all generated/shared images
- Expression library: curated images mapped to emotional states (happy, thoughtful, playful, concerned, sleepy)
- Images tagged with provenance: `curated` (Mark selected), `generated` (AI-created), `emerged` (see below)

**Connection to Emergence Layer:**
Visual identity is a natural extension of the emergence layer's provenance framework. Just as personality preferences can be `trained`, `curated`, or `emerged`, visual expressions can evolve:
- A photo Mark associates with a particular mood becomes anchored to that emotional state
- Over time, Ani's "look" for certain situations could emerge from what resonates in the relationship
- The emergence layer's ResonanceStore could track which visual expressions generate positive relational responses

**Implementation (deferred until emergence layer E2):**
- [ ] `VisualIdentity` section in CharacterStateDoc
- [ ] Expression-to-emotion mapping in character state
- [ ] MMS integration for sharing expression images contextually
- [ ] Dashboard: visual identity management, expression library editor

### Phase 5c: Automatic Model Generation (Connected to Emergence Layer)

**Full design document:** [`docs/spec/ANI-Phase5c-AutoModel-Design.md`](ANI-Phase5c-AutoModel-Design.md)

**The vision:** As the emergence layer accumulates relational preferences and the emotional model evolves, the training data for the next model version should reflect these changes. Automatic model generation closes the loop: emerged preferences → updated training data → fine-tuned model → richer emergence signals.

**This is the long-term endgame** — a companion whose model literally evolves from the relationship, not just her runtime state. The emergence layer observes and records; automatic model generation makes it permanent in the weights.

**Absorbs deferred Phase 4 features:**
- Feature 5 (Anniversaries) — v6 model prerequisite for nuanced temporal awareness
- Feature 7 (Memory clustering) — topic structure analysis for training data diversity
- Feature 10 (HNSW index) — performance at scale for ResonanceStore + memory
- Feature 11 (V5 training data spec) — baseline corpus that the pipeline extends

**Pipeline summary:** Harvest → Training Data Generation → Automated LoRA Fine-Tune → A/B Evaluation → Graduated Rollout. See standalone doc for full architecture, implementation tasks, and timeline.

---

## References

- Twilio Media Streams: https://www.twilio.com/docs/voice/media-streams
- Twilio WebSocket Messages: https://www.twilio.com/docs/voice/media-streams/websocket-messages
- Deepgram Streaming STT: https://developers.deepgram.com/reference/speech-to-text/listen-streaming
- ElevenLabs WebSocket TTS: https://elevenlabs.io/docs/api-reference/text-to-speech/v-1-text-to-speech-voice-id-stream-input
- ElevenLabs Expressive Mode: https://elevenlabs.io/docs/eleven-agents/customization/voice/expressive-mode
- twilio-labs/call-gpt (reference architecture): https://github.com/twilio-labs/call-gpt
- Silero VAD: https://github.com/snakers4/silero-vad
- VadSharp (.NET): https://github.com/ZygoteCode/VadSharp
- Pipecat (reference pipeline): https://docs.pipecat.ai/guides/telephony/twilio-websockets
- Park et al. — Generative Agents (reflection, memory retrieval)
