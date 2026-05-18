using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Voice.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Orchestrates a real-time voice conversation over a Twilio phone call.
/// Each turn: STT (Whisper) → context build → LLM reply → TTS (ElevenLabs)
/// → TwiML response. Bypasses the cognitive cycle for speed.
///
/// <para>
/// Orchestrator SOLID refactor §5.2 (2026-05-18) — previously held 13 ctor
/// deps and mixed five concerns inline (session map, context build, LLM
/// chat, TTS+caching, TwiML formatting). The five concerns now live in
/// <see cref="IVoiceSessionStore"/>, <see cref="IVoiceContextBuilder"/>,
/// <see cref="IVoiceReplyGenerator"/>, <see cref="IVoiceAudioSynthesizer"/>,
/// and <see cref="IVoiceTwimlBuilder"/>. This class is now an orchestrator
/// — it chains them through StartCall / ProcessTurn / EndCall.
/// </para>
/// </summary>
public class VoiceConversationService
{
    private readonly IVoiceSessionStore _sessions;
    private readonly IVoiceContextBuilder _contextBuilder;
    private readonly IVoiceReplyGenerator _replyGenerator;
    private readonly IVoiceAudioSynthesizer _audio;
    private readonly IVoiceTwimlBuilder _twiml;
    private readonly IConversationService _conversations;
    private readonly TwilioVoiceHandler _voiceHandler;
    private readonly VoiceOptions _voiceOptions;
    private readonly ILogger<VoiceConversationService> _log;

    public VoiceConversationService(
        IVoiceSessionStore sessions,
        IVoiceContextBuilder contextBuilder,
        IVoiceReplyGenerator replyGenerator,
        IVoiceAudioSynthesizer audio,
        IVoiceTwimlBuilder twiml,
        IConversationService conversations,
        TwilioVoiceHandler voiceHandler,
        IOptions<VoiceOptions> voiceOptions,
        ILogger<VoiceConversationService> log)
    {
        _sessions       = sessions       ?? throw new ArgumentNullException(nameof(sessions));
        _contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
        _replyGenerator = replyGenerator ?? throw new ArgumentNullException(nameof(replyGenerator));
        _audio          = audio          ?? throw new ArgumentNullException(nameof(audio));
        _twiml          = twiml          ?? throw new ArgumentNullException(nameof(twiml));
        _conversations  = conversations  ?? throw new ArgumentNullException(nameof(conversations));
        _voiceHandler   = voiceHandler   ?? throw new ArgumentNullException(nameof(voiceHandler));
        _voiceOptions   = voiceOptions.Value;
        _log            = log            ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Start a new voice call session. Creates a conversation thread and returns
    /// greeting TwiML with a <Record> for the first user turn.
    /// </summary>
    public async Task<string> StartCallAsync(string callSid, CancellationToken ct = default)
    {
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
        _sessions.Add(callSid, session);

        _log.LogInformation("Voice call started: {CallSid}, thread {ThreadId} — cognitive cycle paused",
            callSid, thread.Id);

        // Warm the conversation model concurrently with greeting TTS — the
        // cognitive cycle may have evicted it since startup pre-warm.
        var warmTask = _replyGenerator.WarmAsync(ct);

        string greetingTwiml;
        try
        {
            var audioUrl = await _audio.SynthesizeAsync(_voiceOptions.VoiceGreeting, ct).ConfigureAwait(false);
            greetingTwiml = _twiml.BuildPlayAndRecord(callSid, audioUrl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Voice greeting TTS failed — using Twilio Say");
            greetingTwiml = _twiml.BuildSayAndRecord(callSid, _voiceOptions.VoiceGreeting);
        }

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
        if (!_sessions.TryGet(callSid, out var session) || session is null)
        {
            _log.LogWarning("Voice turn for unknown session {CallSid} — starting fresh", callSid);
            return await StartCallAsync(callSid, ct).ConfigureAwait(false);
        }

        // Standalone timeout — not linked to the caller's token. Voice turns must
        // complete even if Twilio's webhook connection drops (the reply and buffered
        // messages still matter). Only ApplicationStopping should cancel voice work.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(TimeSpan.FromMilliseconds(_voiceOptions.VoiceTurnTimeoutMs));

        try
        {
            var text = await _voiceHandler.TranscribeInboundAsync(recordingUrl, turnCts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 5)
            {
                _log.LogInformation("Voice turn: transcription too short ({Length} chars) — prompting again",
                    text?.Trim().Length ?? 0);
                return _twiml.BuildSayAndRecord(callSid, "I didn't catch that, say that again?");
            }

            _log.LogInformation("Voice turn {Turn}: \"{Text}\"", session.TurnCount + 1, text);

            session.PendingMessages.Enqueue(new ConversationMessage
            {
                Role = Roles.Mark, Content = text, SentAt = DateTimeOffset.UtcNow,
            });

            var snapshot = await _contextBuilder.BuildAsync(text, session, turnCts.Token).ConfigureAwait(false);

            var thread = await _conversations.GetThreadAsync(session.ThreadId, turnCts.Token).ConfigureAwait(false)
                         ?? new ConversationThread();
            var allMessages = new List<ConversationMessage>(thread.Messages);
            allMessages.AddRange(session.PendingMessages.ToArray());

            var reply = await _replyGenerator.GenerateAsync(snapshot, thread, allMessages, turnCts.Token)
                .ConfigureAwait(false);

            if (reply is null)
                return _twiml.BuildSayAndRecord(callSid, "sorry, what was that?");

            _log.LogInformation("Voice reply: {Reply}", reply);

            session.PendingMessages.Enqueue(new ConversationMessage
            {
                Role = Roles.Ani, Content = reply, SentAt = DateTimeOffset.UtcNow,
            });
            session.TurnCount++;
            session.LastTurnAt = DateTimeOffset.UtcNow;

            try
            {
                var audioUrl = await _audio.SynthesizeAsync(reply, turnCts.Token).ConfigureAwait(false);
                return _twiml.BuildPlayAndRecord(callSid, audioUrl);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Voice TTS failed — falling back to Twilio Say");
                return _twiml.BuildSayAndRecord(callSid, reply);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Voice turn timed out ({Ms}ms) — sending filler", _voiceOptions.VoiceTurnTimeoutMs);
            return _twiml.BuildSayAndRecord(callSid, "Sorry, I missed that. Can you say it again?");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voice turn failed");
            return _twiml.BuildSayAndRecord(callSid, "Sorry, something got jumbled. Say that again?");
        }
    }

    /// <summary>
    /// End a voice call session and close the conversation thread.
    /// </summary>
    public async Task EndCallAsync(string callSid, CancellationToken ct = default)
    {
        var session = _sessions.Remove(callSid);
        if (session is null)
            return;

        // Drain and batch-save all buffered messages before resuming the cognitive cycle —
        // saves trigger embedding via Ollama, must complete before cycle competes for it.
        var pending = session.PendingMessages.ToArray();
        if (pending.Length > 0)
        {
            _log.LogInformation("Voice: saving {Count} buffered messages to thread {ThreadId}",
                pending.Length, session.ThreadId);
            foreach (var msg in pending)
            {
                try
                {
                    await _conversations.AddMessageAsync(session.ThreadId, msg, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Voice: failed to save buffered message");
                }
            }
        }

        await _conversations.CloseThreadAsync(session.ThreadId, ct).ConfigureAwait(false);
        _log.LogInformation("Voice call ended: {CallSid}, {Turns} turns, duration {Duration} — cognitive cycle resumed",
            callSid, session.TurnCount,
            (DateTimeOffset.UtcNow - session.StartedAt).ToString(@"m\:ss"));
    }
}
