using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Thin WebSocket handler for streaming voice sessions. Responsibilities:
/// 1. WebSocket lifecycle (connect, receive loop, cleanup)
/// 2. Audio routing (client ↔ STT/TTS)
/// 3. Session tracking
///
/// Business logic is delegated to:
/// - <see cref="VoiceTurnPipeline"/> — transcript → LLM → TTS flow
/// - <see cref="VoiceSessionState"/> — thread-safe session state
/// - <see cref="DebouncedUtterance"/> — turn detection (via DeepgramStreamingSTTService)
/// </summary>
public class StreamingVoiceOrchestrator
{
    private readonly IServiceProvider _services;
    private readonly IConversationService _conversations;
    private readonly IOllamaClient _ollama;
    private readonly OllamaOptions _ollamaOptions;
    private readonly IMemoryService _memory;
    private readonly VoiceOptions _voiceOptions;
    private readonly VoiceTurnPipeline _pipeline;
    private readonly ILogger<StreamingVoiceOrchestrator> _log;

    private readonly ConcurrentDictionary<string, VoiceSessionState> _sessions = new();

    public Action? OnCallStarted { get; set; }
    public Action? OnCallEnded { get; set; }

    public StreamingVoiceOrchestrator(
        IServiceProvider services,
        IConversationService conversations,
        IOllamaClient ollama,
        IOptions<OllamaOptions> ollamaOptions,
        IMemoryService memory,
        IOptions<VoiceOptions> voiceOptions,
        VoiceTurnPipeline pipeline,
        ILogger<StreamingVoiceOrchestrator> log)
    {
        _services      = services;
        _conversations = conversations;
        _ollama        = ollama;
        _ollamaOptions = ollamaOptions.Value;
        _memory        = memory;
        _voiceOptions  = voiceOptions.Value;
        _pipeline      = pipeline;
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
        VoiceSessionState? session = null;
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);

        try
        {
            var startMsg = await ReceiveJsonAsync(ws, sessionCts.Token).ConfigureAwait(false);
            if (startMsg?.Type != "start")
            {
                await SendJsonAsync(ws, new { type = "error", message = "Expected start message" }, appCt)
                    .ConfigureAwait(false);
                return;
            }

            // Create or get conversation thread
            var thread = await _conversations.GetActiveThreadAsync(appCt).ConfigureAwait(false)
                         ?? new ConversationThread
                         {
                             InitiatedBy   = Roles.Mark,
                             StartedAt     = DateTimeOffset.UtcNow,
                             LastMessageAt = DateTimeOffset.UtcNow,
                         };
            if (thread.Messages.Count == 0)
                await _conversations.SaveThreadAsync(thread, appCt).ConfigureAwait(false);

            session = new VoiceSessionState(sessionId, thread.Id, ws);
            _sessions[sessionId] = session;
            OnCallStarted?.Invoke();

            _log.LogInformation("Streaming voice session started: {SessionId}, thread {ThreadId}",
                sessionId, thread.Id);

            var ct = sessionCts.Token;

            // Warm the conversation model
            var warmTask = _ollama.WarmModelAsync(_ollamaOptions.ChatModel, ct);

            // Create per-session streaming services
            stt = _services.GetRequiredService<IStreamingSpeechToTextService>();
            tts = _services.GetRequiredService<IStreamingTextToSpeechService>();

            // Wire TTS audio → client WebSocket (serialized to prevent concurrent SendAsync)
            var wsSendLock = new SemaphoreSlim(1, 1);
            tts.AudioChunkReceived += audioChunk =>
            {
                // Fire-and-forget is acceptable here — audio delivery is best-effort
                // and the lock prevents concurrent WebSocket sends.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await wsSendLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            if (ws.State == WebSocketState.Open)
                                await ws.SendAsync(audioChunk, WebSocketMessageType.Binary,
                                    endOfMessage: true, ct).ConfigureAwait(false);
                        }
                        finally { wsSendLock.Release(); }
                    }
                    catch (OperationCanceledException) { }
                    catch (WebSocketException) { /* client disconnected */ }
                });
            };

            // Wire STT transcript → pipeline (drop if busy, no queuing)
            var replyLock = new SemaphoreSlim(1, 1);

            // Delegates for the pipeline to send data to the client
            async Task SendJsonToClient(object payload, CancellationToken token)
            {
                await wsSendLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await SendJsonAsync(ws, payload, token).ConfigureAwait(false);
                }
                finally { wsSendLock.Release(); }
            }

            // Resumes listening after client confirms audio playback is done.
            // This prevents echo: mic stays muted until speaker finishes draining.
            // Guard: only acts when IsAniSpeaking is true, so naturally idempotent.
            async Task ResumeListeningAsync(CancellationToken token)
            {
                if (!session.IsAniSpeaking) return;
                session.EndSpeaking();
                stt.ClearPendingSegments();
                await SendJsonToClient(new { type = "listening" }, token).ConfigureAwait(false);
                _log.LogDebug("Streaming voice: playback done, resumed listening");
            }

            stt.TranscriptReceived += transcript =>
            {
                _ = Task.Run(async () =>
                {
                    // Drop transcripts that arrive while a reply is in progress.
                    // This prevents Ani from generating multiple sequential replies
                    // to fragments of the same utterance.
                    if (!await replyLock.WaitAsync(0, ct).ConfigureAwait(false))
                    {
                        _log.LogDebug("Streaming voice: dropping transcript (reply in progress): \"{Text}\"",
                            transcript.Length > 60 ? transcript[..60] + "..." : transcript);
                        return;
                    }
                    try
                    {
                        await _pipeline.ProcessTurnAsync(
                            session, transcript, tts,
                            SendJsonToClient, ct)
                            .ConfigureAwait(false);

                        // Safety fallback: if client never sends "playback_done"
                        // (disconnect, bug, etc.), resume listening after 5 seconds.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(5000, ct).ConfigureAwait(false);
                                if (session.IsAniSpeaking)
                                {
                                    _log.LogWarning("Streaming voice: playback_done timeout, force-resuming");
                                    await ResumeListeningAsync(ct).ConfigureAwait(false);
                                }
                            }
                            catch (OperationCanceledException) { }
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Streaming voice: pipeline error for transcript");
                    }
                    finally
                    {
                        replyLock.Release();
                    }
                });
            };

            stt.PartialTranscriptReceived += partial =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendJsonToClient(
                            new { type = "transcript", text = partial, isFinal = false }, ct)
                            .ConfigureAwait(false);
                    }
                    catch { /* best-effort partial transcript display */ }
                });
            };

            // Start STT and TTS connections
            var emotionalState = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(
                stt.StartAsync(ct),
                tts.StartAsync(emotionalState, ct)
            ).ConfigureAwait(false);

            // Ensure model is warm
            try { await warmTask.ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Streaming voice: model warm failed"); }

            await SendJsonToClient(new { type = "session_started", sessionId }, ct)
                .ConfigureAwait(false);

            // Synthesize greeting — client will send "playback_done" when audio finishes
            await _pipeline.SynthesizeGreetingAsync(
                session, _voiceOptions.VoiceGreeting, tts, SendJsonToClient, ct)
                .ConfigureAwait(false);

            // Safety fallback for greeting playback_done
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    if (session.IsAniSpeaking)
                    {
                        _log.LogWarning("Streaming voice: greeting playback_done timeout, force-resuming");
                        await ResumeListeningAsync(ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            });

            // Main receive loop — forward audio and handle control messages
            await ReceiveLoopAsync(ws, session, stt, ResumeListeningAsync, ct).ConfigureAwait(false);
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
            sessionCts.Cancel();

            if (stt is not null) await stt.DisposeAsync().ConfigureAwait(false);
            if (tts is not null) await tts.DisposeAsync().ConfigureAwait(false);

            if (_sessions.TryRemove(sessionId, out _) && session is not null)
            {
                await SavePendingMessagesAsync(session, appCt).ConfigureAwait(false);
                await _conversations.CloseThreadAsync(session.ThreadId, appCt).ConfigureAwait(false);

                if (_sessions.IsEmpty)
                    OnCallEnded?.Invoke();

                _log.LogInformation(
                    "Streaming voice session ended: {SessionId}, {Turns} turns, duration {Duration}",
                    sessionId, session.TurnCount,
                    (DateTimeOffset.UtcNow - session.StartedAt).ToString(@"m\:ss"));
            }

            session?.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket ws, VoiceSessionState session,
        IStreamingSpeechToTextService stt,
        Func<CancellationToken, Task> resumeListening,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        long totalAudioBytes = 0;
        int audioFrameCount = 0;

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Skip forwarding audio while Ani is speaking to prevent echo
                if (session.IsAniSpeaking)
                    continue;

                totalAudioBytes += result.Count;
                audioFrameCount++;
                if (audioFrameCount == 1 || audioFrameCount % 500 == 0)
                    _log.LogDebug("Streaming voice: audio forwarded to Deepgram ({Frames} frames, {KB:F1} KB total)",
                        audioFrameCount, totalAudioBytes / 1024.0);

                await stt.SendAudioAsync(new ReadOnlyMemory<byte>(buffer, 0, result.Count), ct)
                    .ConfigureAwait(false);
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var msg = JsonSerializer.Deserialize<ControlMessage>(json, JsonOpts);

                switch (msg?.Type)
                {
                    case "end":
                        _log.LogInformation("Streaming voice: client sent end signal");
                        return;

                    case "playback_done":
                        // Client's AudioTrack has finished draining — safe to resume listening.
                        // This is the ONLY path that unmutes the mic after Ani speaks.
                        await resumeListening(ct).ConfigureAwait(false);
                        break;

                    case "audio_start":
                        if (session.IsAniSpeaking)
                        {
                            _log.LogInformation("Streaming voice: barge-in detected");
                            session.CancelCurrentTurn();
                        }
                        break;

                    case "audio_stop":
                        break;
                }
            }
        }
    }

    private async Task SavePendingMessagesAsync(VoiceSessionState session, CancellationToken ct)
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
