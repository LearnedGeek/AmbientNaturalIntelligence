using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Orchestrates streaming voice conversations over a direct WebSocket from the MAUI app.
/// Pipeline: MAUI audio → Deepgram STT → Ollama streaming → TokenBuffer → ElevenLabs TTS → MAUI playback.
/// Replaces the batch Twilio Record+webhook model for real-time voice with sub-2-second latency.
/// </summary>
public class StreamingVoiceOrchestrator
{
    private readonly IServiceProvider _services;
    private readonly IMemoryService _memory;
    private readonly IConversationService _conversations;
    private readonly IOllamaClient _ollama;
    private readonly OllamaOptions _ollamaOptions;
    private readonly VoiceOptions _voiceOptions;
    private readonly ILogger<StreamingVoiceOrchestrator> _log;

    private readonly ConcurrentDictionary<string, VoiceCallSession> _sessions = new();

    public Action? OnCallStarted { get; set; }
    public Action? OnCallEnded { get; set; }

    public StreamingVoiceOrchestrator(
        IServiceProvider services,
        IMemoryService memory,
        IConversationService conversations,
        IOllamaClient ollama,
        IOptions<OllamaOptions> ollamaOptions,
        IOptions<VoiceOptions> voiceOptions,
        ILogger<StreamingVoiceOrchestrator> log)
    {
        _services      = services;
        _memory        = memory;
        _conversations = conversations;
        _ollama        = ollama;
        _ollamaOptions = ollamaOptions.Value;
        _voiceOptions  = voiceOptions.Value;
        _log           = log;
    }

    /// <summary>
    /// Handle a WebSocket connection from the MAUI voice client.
    /// Manages the full lifecycle: greeting → conversation loop → cleanup.
    /// </summary>
    public async Task HandleConnectionAsync(WebSocket ws, CancellationToken appCt)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        _log.LogInformation("Streaming voice: WebSocket connected, session {SessionId}", sessionId);

        IStreamingSpeechToTextService? stt = null;
        IStreamingTextToSpeechService? tts = null;

        try
        {
            // Wait for start message
            var startMsg = await ReceiveJsonAsync(ws, appCt).ConfigureAwait(false);
            if (startMsg?.Type != "start")
            {
                await SendJsonAsync(ws, new { type = "error", message = "Expected start message" }, appCt)
                    .ConfigureAwait(false);
                return;
            }

            // Create session
            var thread = await _conversations.GetActiveThreadAsync(appCt).ConfigureAwait(false)
                         ?? new ConversationThread
                         {
                             InitiatedBy   = "mark",
                             StartedAt     = DateTimeOffset.UtcNow,
                             LastMessageAt = DateTimeOffset.UtcNow,
                         };
            if (thread.Messages.Count == 0)
                await _conversations.SaveThreadAsync(thread, appCt).ConfigureAwait(false);

            var session = new VoiceCallSession
            {
                CallSid      = sessionId,
                ThreadId     = thread.Id,
                StartedAt    = DateTimeOffset.UtcNow,
                ClientSocket = ws,
            };
            _sessions[sessionId] = session;
            OnCallStarted?.Invoke();

            _log.LogInformation("Streaming voice session started: {SessionId}, thread {ThreadId}",
                sessionId, thread.Id);

            // Warm the conversation model
            var warmTask = _ollama.WarmModelAsync(_ollamaOptions.ChatModel, appCt);

            // Create per-session streaming services
            stt = _services.GetRequiredService<IStreamingSpeechToTextService>();
            tts = _services.GetRequiredService<IStreamingTextToSpeechService>();

            // Wire STT transcript → LLM → TTS → client
            stt.TranscriptReceived += transcript =>
            {
                // Fire-and-forget the async pipeline — errors are logged inside
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessTranscriptAsync(session, transcript, stt, tts, appCt)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Streaming voice: pipeline error for transcript");
                    }
                });
            };

            stt.PartialTranscriptReceived += partial =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendJsonAsync(ws, new { type = "transcript", text = partial, isFinal = false }, appCt)
                            .ConfigureAwait(false);
                    }
                    catch { /* best-effort partial transcript display */ }
                });
            };

            // Wire TTS audio → client WebSocket
            tts.AudioChunkReceived += audioChunk =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.SendAsync(audioChunk, WebSocketMessageType.Binary,
                                endOfMessage: true, appCt).ConfigureAwait(false);
                    }
                    catch { /* client may have disconnected */ }
                });
            };

            // Start STT and TTS connections
            var emotionalState = await _memory.GetEmotionalStateAsync(appCt).ConfigureAwait(false);
            await Task.WhenAll(
                stt.StartAsync(appCt),
                tts.StartAsync(emotionalState, appCt)
            ).ConfigureAwait(false);

            // Ensure model is warm
            try { await warmTask.ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Streaming voice: model warm failed"); }

            await SendJsonAsync(ws, new { type = "session_started", sessionId }, appCt)
                .ConfigureAwait(false);

            // Synthesize greeting
            await SynthesizeGreetingAsync(session, tts, appCt).ConfigureAwait(false);

            // Main receive loop — forward audio and handle control messages
            await ReceiveLoopAsync(ws, session, stt, appCt).ConfigureAwait(false);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _log.LogInformation("Streaming voice: client disconnected (session {SessionId})", sessionId);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Streaming voice: session cancelled (session {SessionId})", sessionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Streaming voice: unhandled error (session {SessionId}): {Error}", sessionId, ex.Message);
        }
        finally
        {
            // Cleanup
            if (stt is not null) await stt.DisposeAsync().ConfigureAwait(false);
            if (tts is not null) await tts.DisposeAsync().ConfigureAwait(false);

            if (_sessions.TryRemove(sessionId, out var endedSession))
            {
                await SavePendingMessagesAsync(endedSession, appCt).ConfigureAwait(false);
                await _conversations.CloseThreadAsync(endedSession.ThreadId, appCt).ConfigureAwait(false);

                if (_sessions.IsEmpty)
                    OnCallEnded?.Invoke();

                _log.LogInformation(
                    "Streaming voice session ended: {SessionId}, {Turns} turns, duration {Duration}",
                    sessionId, endedSession.TurnCount,
                    (DateTimeOffset.UtcNow - endedSession.StartedAt).ToString(@"m\:ss"));
            }
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket ws, VoiceCallSession session,
        IStreamingSpeechToTextService stt, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Audio from MAUI client → forward to Deepgram STT
                await stt.SendAudioAsync(new ReadOnlyMemory<byte>(buffer, 0, result.Count), ct)
                    .ConfigureAwait(false);
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var msg = JsonSerializer.Deserialize<ControlMessage>(json);

                switch (msg?.Type)
                {
                    case "end":
                        _log.LogInformation("Streaming voice: client sent end signal");
                        return;

                    case "audio_start":
                        // User started speaking while Ani is talking — barge-in
                        if (session.IsAniSpeaking)
                        {
                            _log.LogInformation("Streaming voice: barge-in detected");
                            session.CurrentTurnCts?.Cancel();
                            session.IsAniSpeaking = false;
                        }
                        break;

                    case "audio_stop":
                        // User stopped speaking — Deepgram endpointing handles this
                        break;
                }
            }
        }
    }

    private async Task ProcessTranscriptAsync(
        VoiceCallSession session, string transcript,
        IStreamingSpeechToTextService stt, IStreamingTextToSpeechService tts,
        CancellationToken appCt)
    {
        if (string.IsNullOrWhiteSpace(transcript) || transcript.Trim().Length < 3)
        {
            _log.LogDebug("Streaming voice: transcript too short, skipping");
            return;
        }

        // Send final transcript to client
        if (session.ClientSocket?.State == WebSocketState.Open)
        {
            await SendJsonAsync(session.ClientSocket,
                new { type = "transcript", text = transcript, isFinal = true }, appCt)
                .ConfigureAwait(false);
        }

        _log.LogInformation("Streaming voice turn {Turn}: \"{Text}\"", session.TurnCount + 1, transcript);

        // Buffer Mark's message
        session.PendingMessages.Enqueue(new ConversationMessage
        {
            Role = "mark", Content = transcript, SentAt = DateTimeOffset.UtcNow,
        });

        // Per-turn cancellation for barge-in support
        session.CurrentTurnCts?.Dispose();
        session.CurrentTurnCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var turnCt = session.CurrentTurnCts.Token;

        try
        {
            // Build context (SQLite only — no Ollama embedding during voice)
            var snapshot = await BuildVoiceContextAsync(turnCt).ConfigureAwait(false);

            var thread = await _conversations.GetThreadAsync(session.ThreadId, turnCt)
                .ConfigureAwait(false);
            var allMessages = new List<ConversationMessage>(thread?.Messages ?? new List<ConversationMessage>());
            allMessages.AddRange(session.PendingMessages.ToArray());

            // Generate streaming reply
            var prompt = PromptBuilder.BuildVoiceReplyPrompt(snapshot, thread ?? new ConversationThread());

            if (session.ClientSocket?.State == WebSocketState.Open)
                await SendJsonAsync(session.ClientSocket, new { type = "reply_start" }, turnCt)
                    .ConfigureAwait(false);

            session.IsAniSpeaking = true;
            var tokenBuffer = new TokenBuffer();
            var fullReply = new StringBuilder();

            await foreach (var token in _ollama.ChatStreamAsync(
                prompt.System,
                allMessages.TakeLast(10).Select(m =>
                    new ChatMessage(m.Role == "mark" ? "user" : "assistant", m.Content)),
                prompt.User, turnCt).ConfigureAwait(false))
            {
                fullReply.Append(token);

                var sentence = tokenBuffer.Add(token);
                if (sentence is not null)
                    await tts.SendTextAsync(sentence, turnCt).ConfigureAwait(false);
            }

            // Flush remaining tokens
            var remaining = tokenBuffer.Flush();
            if (remaining is not null)
                await tts.SendTextAsync(remaining, turnCt).ConfigureAwait(false);

            await tts.FlushAsync(turnCt).ConfigureAwait(false);

            // Clean and buffer Ani's reply
            var reply = MessageCleaner.Clean(fullReply.ToString());
            if (!string.IsNullOrWhiteSpace(reply))
            {
                session.PendingMessages.Enqueue(new ConversationMessage
                {
                    Role = "ani", Content = reply, SentAt = DateTimeOffset.UtcNow,
                });
            }

            session.TurnCount++;
            session.LastTurnAt = DateTimeOffset.UtcNow;
            session.IsAniSpeaking = false;

            _log.LogInformation("Streaming voice reply: \"{Reply}\"", reply);

            if (session.ClientSocket?.State == WebSocketState.Open)
            {
                await SendJsonAsync(session.ClientSocket, new { type = "reply_end" }, appCt)
                    .ConfigureAwait(false);
                await SendJsonAsync(session.ClientSocket, new { type = "listening" }, appCt)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            session.IsAniSpeaking = false;
            _log.LogInformation("Streaming voice: turn cancelled (barge-in or disconnect)");
        }
    }

    private async Task SynthesizeGreetingAsync(
        VoiceCallSession session, IStreamingTextToSpeechService tts, CancellationToken ct)
    {
        var greeting = _voiceOptions.VoiceGreeting;
        session.IsAniSpeaking = true;

        if (session.ClientSocket?.State == WebSocketState.Open)
            await SendJsonAsync(session.ClientSocket, new { type = "reply_start" }, ct)
                .ConfigureAwait(false);

        await tts.SendTextAsync(greeting, ct).ConfigureAwait(false);
        await tts.FlushAsync(ct).ConfigureAwait(false);

        // Brief pause for audio to finish streaming before signaling ready
        await Task.Delay(500, ct).ConfigureAwait(false);

        session.IsAniSpeaking = false;

        if (session.ClientSocket?.State == WebSocketState.Open)
        {
            await SendJsonAsync(session.ClientSocket, new { type = "reply_end" }, ct)
                .ConfigureAwait(false);
            await SendJsonAsync(session.ClientSocket, new { type = "listening" }, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<ContextSnapshot> BuildVoiceContextAsync(CancellationToken ct)
    {
        var characterTask = _memory.GetCharacterStateAsync(ct);
        var emotionalTask = _memory.GetEmotionalStateAsync(ct);
        var anchoredTask  = _memory.GetAnchoredMemoriesAsync(ct);
        await Task.WhenAll(characterTask, emotionalTask, anchoredTask).ConfigureAwait(false);

        return new ContextSnapshot
        {
            CharacterState   = characterTask.Result,
            EmotionalState   = emotionalTask.Result,
            RelevantMemory   = new List<MemoryRecord>(),
            AnchoredMemories = anchoredTask.Result.ToList(),
            RecentMemory     = new List<MemoryRecord>(),
            Perceptions      = new List<PerceptionEvent>(),
            OpenLoops        = new List<OpenLoop>(),
            RecentHistory    = new List<ChatMessage>(),
            BuiltAt          = DateTimeOffset.UtcNow,
        };
    }

    private async Task SavePendingMessagesAsync(VoiceCallSession session, CancellationToken ct)
    {
        var pending = session.PendingMessages.ToArray();
        if (pending.Length == 0) return;

        _log.LogInformation("Streaming voice: saving {Count} buffered messages to thread {ThreadId}",
            pending.Length, session.ThreadId);

        foreach (var msg in pending)
        {
            try
            {
                await _conversations.AddMessageAsync(session.ThreadId, msg, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Streaming voice: failed to save buffered message");
            }
        }
    }

    // ── WebSocket helpers ───────────────────────────────────────────────────

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    private static async Task<ControlMessage?> ReceiveJsonAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
        if (result.MessageType != WebSocketMessageType.Text) return null;
        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        return JsonSerializer.Deserialize<ControlMessage>(json, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private record ControlMessage
    {
        public string Type { get; init; } = string.Empty;
    }
}
