using System.Collections.Concurrent;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Orchestrates a real-time voice conversation over a Twilio phone call.
/// Each turn: STT (Whisper) → context build → LLM reply → TTS (ElevenLabs) → TwiML response.
/// Bypasses the cognitive cycle for speed — uses services directly.
/// </summary>
public class VoiceConversationService
{
    private readonly TwilioVoiceHandler _voiceHandler;
    private readonly ITextToSpeechService _tts;
    private readonly MediaCacheService _cache;
    private readonly IMemoryService _memory;
    private readonly IConversationService _conversations;
    private readonly IOllamaClient _ollama;
    private readonly OllamaOptions _ollamaOptions;
    private readonly VoiceOptions _voiceOptions;
    private readonly ILogger<VoiceConversationService> _log;

    private readonly ConcurrentDictionary<string, VoiceCallSession> _sessions = new();

    /// <summary>
    /// Set by Program.cs to pause/resume the cognitive cycle during voice calls,
    /// freeing Ollama for voice reply generation.
    /// </summary>
    public Action? OnCallStarted { get; set; }
    public Action? OnCallEnded { get; set; }

    public VoiceConversationService(
        TwilioVoiceHandler voiceHandler,
        ITextToSpeechService tts,
        MediaCacheService cache,
        IMemoryService memory,
        IConversationService conversations,
        IOllamaClient ollama,
        IOptions<OllamaOptions> ollamaOptions,
        IOptions<VoiceOptions> voiceOptions,
        ILogger<VoiceConversationService> log)
    {
        _voiceHandler   = voiceHandler;
        _tts            = tts;
        _cache          = cache;
        _memory         = memory;
        _conversations  = conversations;
        _ollama         = ollama;
        _ollamaOptions  = ollamaOptions.Value;
        _voiceOptions   = voiceOptions.Value;
        _log            = log;
    }

    /// <summary>
    /// Start a new voice call session. Creates a conversation thread and returns
    /// greeting TwiML with a <Record> for the first user turn.
    /// </summary>
    public async Task<string> StartCallAsync(string callSid, CancellationToken ct = default)
    {
        // Create or reuse a conversation thread
        var thread = await _conversations.GetActiveThreadAsync(ct).ConfigureAwait(false)
                     ?? new ConversationThread
                     {
                         InitiatedBy   = Roles.Mark,
                         StartedAt     = DateTimeOffset.UtcNow,
                         LastMessageAt = DateTimeOffset.UtcNow,
                     };

        if (thread.Messages.Count == 0)
            await _conversations.SaveThreadAsync(thread, ct).ConfigureAwait(false);

        var session = new VoiceCallSession
        {
            CallSid   = callSid,
            ThreadId  = thread.Id,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _sessions[callSid] = session;
        OnCallStarted?.Invoke();

        _log.LogInformation("Voice call started: {CallSid}, thread {ThreadId} — cognitive cycle paused",
            callSid, thread.Id);

        // Warm the 8B conversation model concurrently with greeting TTS —
        // cognitive cycle may have evicted it from VRAM since startup pre-warm
        var warmTask = _ollama.WarmModelAsync(_ollamaOptions.ChatModel, ct);

        // Synthesize greeting
        var greeting = _voiceOptions.VoiceGreeting;
        string greetingTwiml;

        try
        {
            var emotionalState = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
            var audioStream = await _tts.SynthesizeAsync(greeting, emotionalState, ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await audioStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var key = _cache.Store(ms.ToArray(), "audio/mpeg");
            var audioUrl = $"{_voiceOptions.PublicBaseUrl.TrimEnd('/')}/media/{key}";

            greetingTwiml = $"""
                <Response>
                    <Play>{audioUrl}</Play>
                    <Pause length="1"/>
                    <Record maxLength="{_voiceOptions.VoiceRecordMaxSeconds}" action="/voice/turn?callSid={callSid}" playBeep="false" timeout="{_voiceOptions.VoiceRecordTimeoutSeconds}" />
                </Response>
                """;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Voice greeting TTS failed — using Twilio Say");
            greetingTwiml = $"""
                <Response>
                    <Say voice="alice">{greeting}</Say>
                    <Pause length="1"/>
                    <Record maxLength="{_voiceOptions.VoiceRecordMaxSeconds}" action="/voice/turn?callSid={callSid}" playBeep="false" timeout="{_voiceOptions.VoiceRecordTimeoutSeconds}" />
                </Response>
                """;
        }

        // Ensure model is warm before first turn — don't let warm failure block the call
        try { await warmTask.ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "Voice: model warm failed — first turn may be slow"); }

        return greetingTwiml;
    }

    /// <summary>
    /// Process one turn of a voice conversation: transcribe → reply → synthesize → TwiML.
    /// Must complete within ~13 seconds to stay within Twilio's 15-second timeout.
    /// </summary>
    public async Task<string> ProcessTurnAsync(
        string callSid, string recordingUrl, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(callSid, out var session))
        {
            _log.LogWarning("Voice turn for unknown session {CallSid} — starting fresh", callSid);
            var twiml = await StartCallAsync(callSid, ct).ConfigureAwait(false);
            return twiml;
        }

        // Standalone timeout — not linked to the caller's token. Voice turns must
        // complete even if Twilio's webhook connection drops (the reply and buffered
        // messages still matter). Only ApplicationStopping should cancel voice work.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(TimeSpan.FromMilliseconds(_voiceOptions.VoiceTurnTimeoutMs));

        try
        {
            // Step 1: Transcribe
            var text = await _voiceHandler.TranscribeInboundAsync(recordingUrl, turnCts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 5)
            {
                _log.LogInformation("Voice turn: transcription too short ({Length} chars) — prompting again",
                    text?.Trim().Length ?? 0);
                return BuildRecordTwiml(callSid, "I didn't catch that, say that again?");
            }

            _log.LogInformation("Voice turn {Turn}: \"{Text}\"", session.TurnCount + 1, text);

            // Step 2: Buffer Mark's message in-memory (no Ollama calls — saves at call end)
            var markMsg = new ConversationMessage
            {
                Role = Roles.Mark, Content = text, SentAt = DateTimeOffset.UtcNow,
            };
            session.PendingMessages.Enqueue(markMsg);

            // Step 3: Build context (SQLite only, no Ollama)
            var snapshot = await BuildVoiceContextAsync(text, session, turnCts.Token)
                .ConfigureAwait(false);

            // Build a lightweight thread from buffered messages for LLM context
            var thread = await _conversations.GetThreadAsync(session.ThreadId, turnCts.Token)
                .ConfigureAwait(false);
            // Merge persisted messages with buffered messages from this call
            var allMessages = new List<ConversationMessage>(thread?.Messages ?? new List<ConversationMessage>());
            allMessages.AddRange(session.PendingMessages.ToArray());

            // Step 4: Generate reply via LLM (8B conversation model — trained for direct dialogue)
            var prompt = PromptBuilder.BuildVoiceReplyPrompt(snapshot, thread ?? new ConversationThread());
            var reply = await _ollama.ChatAsync(
                prompt.System, allMessages.TakeLast(10).Select(m =>
                    new ChatMessage(m.Role == Roles.Mark ? "user" : "assistant", m.Content)),
                prompt.User, turnCts.Token).ConfigureAwait(false);

            reply = MessageCleaner.Clean(reply);
            reply = TruncateForVoice(reply);
            if (string.IsNullOrWhiteSpace(reply))
            {
                _log.LogWarning("Voice: LLM returned empty reply");
                return BuildRecordTwiml(callSid, "sorry, what was that?");
            }

            _log.LogInformation("Voice reply: {Reply}", reply);

            // Step 5: Buffer Ani's reply (saved at call end)
            session.PendingMessages.Enqueue(new ConversationMessage
            {
                Role = Roles.Ani, Content = reply, SentAt = DateTimeOffset.UtcNow,
            });

            // Step 6: Synthesize and return TwiML
            session.TurnCount++;
            session.LastTurnAt = DateTimeOffset.UtcNow;

            return await BuildPlayAndRecordTwimlAsync(callSid, reply, turnCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Voice turn timed out ({Ms}ms) — sending filler", _voiceOptions.VoiceTurnTimeoutMs);
            return BuildRecordTwiml(callSid, "Sorry, I missed that. Can you say it again?");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voice turn failed");
            return BuildRecordTwiml(callSid, "Sorry, something got jumbled. Say that again?");
        }
    }

    /// <summary>
    /// End a voice call session and close the conversation thread.
    /// </summary>
    public async Task EndCallAsync(string callSid, CancellationToken ct = default)
    {
        if (_sessions.TryRemove(callSid, out var session))
        {
            // Drain and batch-save all buffered messages before resuming cognitive cycle —
            // saves trigger embedding via Ollama, must complete before cycle competes for it
            var pending = session.PendingMessages.ToArray();
            if (pending.Length > 0)
            {
                _log.LogInformation("Voice: saving {Count} buffered messages to thread {ThreadId}",
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
                        _log.LogWarning(ex, "Voice: failed to save buffered message");
                    }
                }
            }

            // Resume cognitive cycle only after saves are done
            if (_sessions.IsEmpty)
                OnCallEnded?.Invoke();

            await _conversations.CloseThreadAsync(session.ThreadId, ct).ConfigureAwait(false);
            _log.LogInformation("Voice call ended: {CallSid}, {Turns} turns, duration {Duration} — cognitive cycle resumed",
                callSid, session.TurnCount,
                (DateTimeOffset.UtcNow - session.StartedAt).ToString(@"m\:ss"));
        }
    }

    /// <summary>
    /// Build a lightweight ContextSnapshot for voice — character state, emotional state,
    /// anchored memories only. Skips semantic search (requires Ollama embedding, ~4s)
    /// and perceptions/desire/open loops. Keeps Ollama free for the reply LLM call.
    /// </summary>
    private async Task<ContextSnapshot> BuildVoiceContextAsync(
        string userMessage, VoiceCallSession session, CancellationToken ct)
    {
        // All three are SQLite reads — no Ollama calls, fast
        var characterTask = _memory.GetCharacterStateAsync(ct);
        var emotionalTask = _memory.GetEmotionalStateAsync(ct);
        var anchoredTask = _memory.GetAnchoredMemoriesAsync(ct);
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

    /// <summary>
    /// Synthesize reply audio and return TwiML with Play + Record for next turn.
    /// Falls back to Twilio Say if TTS fails.
    /// </summary>
    private async Task<string> BuildPlayAndRecordTwimlAsync(
        string callSid, string reply, CancellationToken ct)
    {
        try
        {
            var emotionalState = await _memory.GetEmotionalStateAsync(ct).ConfigureAwait(false);
            var audioStream = await _tts.SynthesizeAsync(reply, emotionalState, ct)
                .ConfigureAwait(false);
            using var ms = new MemoryStream();
            await audioStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var key = _cache.Store(ms.ToArray(), "audio/mpeg");
            var audioUrl = $"{_voiceOptions.PublicBaseUrl.TrimEnd('/')}/media/{key}";

            return $"""
                <Response>
                    <Play>{audioUrl}</Play>
                    <Pause length="1"/>
                    <Record maxLength="{_voiceOptions.VoiceRecordMaxSeconds}" action="/voice/turn?callSid={callSid}" playBeep="false" timeout="{_voiceOptions.VoiceRecordTimeoutSeconds}" />
                </Response>
                """;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Voice TTS failed — falling back to Twilio Say");
            return BuildRecordTwiml(callSid, reply);
        }
    }

    /// <summary>
    /// Build TwiML that says something with Twilio's built-in voice and records the next turn.
    /// Used for fallback when ElevenLabs TTS fails.
    /// </summary>
    private string BuildRecordTwiml(string callSid, string sayText) =>
        $"""
        <Response>
            <Say voice="alice">{EscapeXml(sayText)}</Say>
            <Pause length="1"/>
            <Record maxLength="{_voiceOptions.VoiceRecordMaxSeconds}" action="/voice/turn?callSid={callSid}" playBeep="false" timeout="{_voiceOptions.VoiceRecordTimeoutSeconds}" />
        </Response>
        """;

    /// <summary>
    /// Truncate LLM reply to ~2 sentences for voice — the model doesn't reliably
    /// respect word count limits. Keeps the first two sentence-ending punctuation marks.
    /// Prevents long replies from blowing the TTS synthesis budget.
    /// </summary>
    private static string TruncateForVoice(string reply)
    {
        const int maxSentences = 2;
        var count = 0;
        for (var i = 0; i < reply.Length; i++)
        {
            if (reply[i] is '.' or '!' or '?')
            {
                // Skip ellipses (...)
                if (reply[i] == '.' && i + 1 < reply.Length && reply[i + 1] == '.')
                    continue;
                count++;
                if (count >= maxSentences)
                    return reply[..(i + 1)].Trim();
            }
        }
        return reply;
    }

    private static string EscapeXml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
}
