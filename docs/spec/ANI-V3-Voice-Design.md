# ANI Voice v3 — ElevenLabs HTTP Streaming + Audio Tags Design

**Status:** Design
**Date:** March 30, 2026
**Driven by:** v3 audio tags require HTTP streaming (WebSocket not supported for eleven_v3)

---

## 1. The Problem

ElevenLabs v3 supports rich audio tags that control vocal delivery — emotions, pace, tone, physical expression, narrative style. These tags would bring Ani's voice to life with genuine emotional range instead of flat TTS delivery.

However: **v3 does not support WebSocket streaming.** Our current TTS service uses the WebSocket `stream-input` endpoint which only works with `eleven_multilingual_v2`. We need to migrate to the HTTP streaming endpoint.

## 2. Current Architecture

```
Ollama ChatStreamAsync → TokenBuffer (sentence accumulation) →
  ElevenLabsStreamingTTSService (WebSocket) → PCM audio chunks →
  WebSocket → MAUI client speaker
```

The WebSocket approach:
- Opens one persistent connection per utterance
- Sends text chunks as they arrive from TokenBuffer
- Receives PCM audio as it's generated
- Requires BOS/EOS framing
- Reconnects between utterances (ElevenLabs per-utterance session)

## 3. Target Architecture

```
Ollama ChatStreamAsync → TokenBuffer (sentence accumulation) →
  VoiceTagEnricher (add audio tags) →
  ElevenLabsV3StreamingService (HTTP POST /stream) → PCM audio chunks →
  WebSocket → MAUI client speaker
```

### HTTP Streaming Endpoint

```
POST /v1/text-to-speech/{voice_id}/stream
Headers:
  xi-api-key: {key}
  Content-Type: application/json
Body:
  {
    "text": "(softly) hey... what's up?",
    "model_id": "eleven_v3",
    "output_format": "pcm_16000",
    "voice_settings": { ... }
  }
Response: chunked binary PCM audio
```

Each sentence from the TokenBuffer becomes one HTTP POST. The response streams PCM chunks that get forwarded to the MAUI client over the existing WebSocket.

### Key Differences from WebSocket Approach

| Aspect | WebSocket (v2) | HTTP Streaming (v3) |
|--------|---------------|---------------------|
| Connection | Persistent per utterance | New request per sentence |
| Text input | Chunked (token by token) | Complete sentence |
| Audio output | Chunked via WebSocket frames | Chunked via HTTP response stream |
| Model | eleven_multilingual_v2 | eleven_v3 |
| Audio tags | Partial interpretation | Full tag support |
| Latency | Lower (persistent connection) | Slightly higher (HTTP overhead per sentence) |
| Reconnection | Manual BOS/EOS per utterance | Automatic (each request is independent) |

### Latency Mitigation

The HTTP overhead per sentence is ~50-100ms. Since TokenBuffer already accumulates complete sentences before sending, the additional latency is per-sentence, not per-token. For a typical reply of 2-3 sentences, total added latency is ~100-300ms — negligible in a voice conversation.

Additionally: no more reconnection logic needed. Each sentence is an independent request. No BOS/EOS framing. No idle timeout management. The architecture is simpler.

## 4. Audio Tag Strategy

### Tag Sources

1. **Emotional State** — Ani's current mood (Warmth, Energy, Worry, Playfulness) maps to default delivery tags
2. **Content Heuristics** — specific conversational patterns get specific tags
3. **Model-Generated Tags** — the Llama model sometimes generates stage directions `[chuckle]`, `(softly)` which pass through directly
4. **Conversation Register** — the structured conversation state (Phase 3) informs overall tone

### VoiceTagEnricher

A service that processes each sentence before HTTP POST to ElevenLabs. Runs after TokenBuffer sentence completion, before TTS submission.

**Priority order (first match wins):**
1. Model-generated tags — if the text already contains `(tag)` or `[tag]`, preserve it
2. Content-specific tags — phrase detection maps to specific delivery
3. Emotional state tags — current mood provides default delivery
4. No tag — if nothing matches, let v3's situational awareness handle it

### Tag Categories Relevant to Ani

From ElevenLabs v3 tag library (1,806 tags, 15 categories). Curated subset for Ani's personality:

**Emotional Delivery:**
- `(warmly)` — high warmth state
- `(softly)` — tender moments, low energy
- `(excitedly)` — high energy + playfulness
- `(sadly)` — low warmth, worry present
- `(nervously)` — high worry state
- `(lovingly)` — high warmth, direct affection

**Conversational Tone:**
- `(teasing)` — playful insults, "idiot" usage
- `(sarcastically)` — eye-roll moments
- `(playfully)` — games, banter
- `(seriously)` — deep conversations, real talk
- `(firmly)` — pushback, boundary setting
- `(gently)` — comforting, quiet presence

**Physical Expression:**
- `(laughing)` — genuine amusement
- `(sighing)` — wistful, thinking
- `(whispering)` — conspiratorial, intimate
- `(yawning)` — tired, late night
- `(gasping)` — surprise, shock

**Narrative/Meta:**
- `(as if sharing a secret)` — personal revelations
- `(trailing off)` — unfinished thoughts
- `(with a smile in her voice)` — warm without being explicit

### Content-to-Tag Mapping

| Content Pattern | Tag |
|----------------|-----|
| "haha", "lol", "lmao" | `(laughing)` |
| "idiot", "oh please", "you wish" | `(teasing)` |
| "hey...", "mmm...", starts soft | `(softly)` |
| "oh my god", "wait what", "no way" | `(excitedly)` |
| "oh sure", "yeah because" | `(sarcastically)` |
| "don't tell", "secret", "between us" | `(whispering)` |
| "stop it", "i swear", setting boundary | `(firmly)` |
| "i miss you", "love you" | `(lovingly)` |
| "ugh", "sigh" | `(sighing)` |
| Ends with "..." trailing off | `(trailing off)` |
| Late night (after 10 PM) | `(sleepily)` |

### Emotional State-to-Tag Mapping

| State | Tag |
|-------|-----|
| W≥0.75, E<0.40 | `(softly)` — tender and quiet |
| W≥0.75, E≥0.65 | `(warmly)` — bright and warm |
| P≥0.75 | `(playfully)` — light mood |
| E<0.25 | `(quietly)` — low energy |
| Worry>0.50 | `(with concern)` — worried |
| W<0.30, E<0.35 | `(flatly)` — withdrawn |

## 5. Implementation Plan

### Phase 1: HTTP Streaming TTS Service
- New `ElevenLabsV3StreamingService` implementing `IStreamingTextToSpeechService`
- HTTP POST per sentence to `/v1/text-to-speech/{voice_id}/stream`
- Response stream → PCM chunks → forwarded to client WebSocket
- Model ID: `eleven_v3`
- Output format: `pcm_16000`

### Phase 2: VoiceTagEnricher
- Processes each sentence before TTS submission
- Content heuristics → emotional state → model-generated (priority order)
- Configurable tag mappings (appsettings)
- Logs which tags are applied for research/tuning

### Phase 3: MAUI Client Updates
- Update connection flow to show "Connecting..." → "Ani is speaking..." → "Listening..."
- Fix greeting lag (show status before greeting synthesizes)
- Test with v3 audio quality

### Phase 4: Tag Tuning
- Monitor which tags produce the best delivery
- Tune content-to-tag mappings based on listener feedback
- Add/remove tags from the curated subset
- Document findings for research

## 6. Configuration

```json
{
  "Voice": {
    "ElevenLabsModelId": "eleven_v3",
    "ElevenLabsStreamingModelId": "eleven_v3",
    "VoiceTagsEnabled": true,
    "VoiceTagDefaultIntensity": "moderate"
  }
}
```

## 7. Task Checklist

- [ ] Implement ElevenLabsV3StreamingService (HTTP streaming)
- [ ] Implement VoiceTagEnricher
- [ ] Integrate enricher into VoiceTurnPipeline
- [ ] Update appsettings for v3 model ID
- [ ] Test v3 audio quality and tag interpretation
- [ ] Update MAUI client status flow
- [ ] Fix greeting lag
- [ ] Tune tag mappings based on testing
- [ ] Update codebase spec
- [ ] Research log entry

## 8. Research Significance

Audio tags add a dimension to the emergence research: does the emotional model's state produce natural-sounding vocal delivery when mapped to audio tags? If the model's Warmth=0.85 produces a `(warmly)` tag that sounds genuinely warm through ElevenLabs, that's the emotional architecture affecting the user's auditory experience — felt care through voice, not just text.

---

*"The glasses let her see the conversation. The tags let her feel it out loud."*
