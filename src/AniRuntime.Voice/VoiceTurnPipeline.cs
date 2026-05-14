using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Core.Utilities;
using AniRuntime.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AniRuntime.Voice;

/// <summary>
/// Processes a single voice turn: transcript → context → LLM stream → TTS → audio out.
/// Single responsibility: given a user utterance, produce a spoken reply.
/// No concurrency management — the caller is responsible for serialization.
/// No fire-and-forget — all async work is awaited and errors propagate.
/// </summary>
public class VoiceTurnPipeline
{
    private readonly IMemoryService _memory;
    private readonly IConversationService _conversations;
    private readonly IOllamaClient _ollama;
    private readonly OllamaOptions _ollamaOptions;
    private readonly AniOptions _aniOptions;
    private readonly ICognitiveOutputGate? _outputGate;
    private readonly IEpistemicSubstrateRenderer? _epistemicRenderer;
    private readonly ILogger<VoiceTurnPipeline> _log;

    public VoiceTurnPipeline(
        IMemoryService memory,
        IConversationService conversations,
        IOllamaClient ollama,
        IOptions<OllamaOptions> ollamaOptions,
        IOptions<AniOptions> aniOptions,
        ILogger<VoiceTurnPipeline> log,
        ICognitiveOutputGate? outputGate = null,
        IEpistemicSubstrateRenderer? epistemicRenderer = null)
    {
        _memory            = memory;
        _conversations     = conversations;
        _ollama            = ollama;
        _ollamaOptions     = ollamaOptions.Value;
        _aniOptions        = aniOptions.Value;
        _outputGate        = outputGate;
        _epistemicRenderer = epistemicRenderer;
        _log               = log;
    }

    /// <summary>
    /// Process a transcript into a spoken reply. This is the complete turn pipeline:
    /// 1. Buffer Mark's message
    /// 2. Build voice context (SQLite only)
    /// 3. Stream LLM reply through TokenBuffer → TTS
    /// 4. Flush TTS and return the complete reply text
    ///
    /// All work is awaited — no fire-and-forget. Caller controls cancellation via <paramref name="ct"/>.
    /// </summary>
    /// <returns>The cleaned reply text, or null if the transcript was too short.</returns>
    public async Task<string?> ProcessTurnAsync(
        VoiceSessionState session,
        string transcript,
        IStreamingTextToSpeechService tts,
        Func<object, CancellationToken, Task> sendJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(transcript) || transcript.Trim().Length < 3)
        {
            _log.LogDebug("VoiceTurnPipeline: transcript too short, skipping");
            return null;
        }

        _log.LogInformation("VoiceTurnPipeline turn {Turn}: \"{Text}\"",
            session.TurnCount + 1, transcript);

        // Send final transcript to client
        await sendJson(new { type = "transcript", text = transcript, isFinal = true }, ct)
            .ConfigureAwait(false);

        // Buffer Mark's message
        session.PendingMessages.Enqueue(new ConversationMessage
        {
            Role = Roles.Mark, Content = transcript, SentAt = DateTimeOffset.UtcNow,
        });

        // Create per-turn cancellation (for barge-in support)
        var turnCt = session.BeginSpeaking(ct);

        try
        {
            await sendJson(new { type = "reply_start" }, turnCt).ConfigureAwait(false);

            // Build context (SQLite only — no Ollama embedding during voice)
            var snapshot = await BuildVoiceContextAsync(turnCt).ConfigureAwait(false);

            var thread = await _conversations.GetThreadAsync(session.ThreadId, turnCt)
                .ConfigureAwait(false);
            var allMessages = new List<ConversationMessage>(
                thread?.Messages ?? new List<ConversationMessage>());
            allMessages.AddRange(session.PendingMessages.ToArray());

            // Conversation Mode: lean prompt — persona + conversation history only.
            // The full voice prompt (BuildVoiceReplyPrompt) injected memories and
            // shared experiences that competed with the conversation for the model's
            // attention, producing confabulation and non-sequiturs.
            var rendererForPrompt = _aniOptions.EpistemicFramingEnabled ? _epistemicRenderer : null;
            var prompt = PromptBuilder.BuildLeanConversationPrompt(
                snapshot, thread ?? new ConversationThread(), rendererForPrompt);

            var tokenBuffer = new TokenBuffer();
            var fullReply = new StringBuilder();

            await foreach (var token in _ollama.ChatStreamAsync(
                prompt.System,
                allMessages.TakeLast(10).Select(m =>
                    new ChatMessage(m.Role == Roles.Mark ? "user" : "assistant", m.Content)),
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

            // ═════════════════════════════════════════════════════════════════
            // Theme J Phase J.5f (May 1, 2026) — substrate-write gate.
            //
            // Voice replies stream: by the time `reply` is complete here, TTS
            // has already played the audio to Mark. The gate cannot prevent
            // dispatch in this surface. What it CAN prevent is the bad reply
            // entering the substrate (PendingMessages → conversation_messages
            // → next cycle's retrieval). The trade-off is honest: the audio
            // happened, but the substrate stays clean. Bad voice reply
            // becomes single-turn ephemeral, not multi-cycle pollution.
            //
            // Disabled when: gate not registered, flag off, or empty reply.
            // ═════════════════════════════════════════════════════════════════
            var gateAllowsPersist = await EvaluateVoiceReplyForSubstrateAsync(
                reply, allMessages, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(reply) && gateAllowsPersist)
            {
                session.PendingMessages.Enqueue(new ConversationMessage
                {
                    Role = Roles.Ani, Content = reply, SentAt = DateTimeOffset.UtcNow,
                });
            }

            _log.LogInformation("VoiceTurnPipeline reply: \"{Reply}\" (substratePersist={Persist})",
                reply, gateAllowsPersist);

            await sendJson(new { type = "reply_end" }, ct).ConfigureAwait(false);

            // Do NOT send "listening" or clear IsAniSpeaking here.
            // The client's AudioTrack still has buffered audio playing through the speaker.
            // The orchestrator will send "listening" when the client signals "playback_done",
            // which ensures the mic stays muted until the speaker is silent.

            return reply;
        }
        catch (OperationCanceledException)
        {
            session.SetSpeaking(false);
            _log.LogInformation("VoiceTurnPipeline: turn cancelled (barge-in or disconnect)");
            return null;
        }
    }

    /// <summary>
    /// Theme J Phase J.5f (May 1, 2026) — voice reply substrate-write gate.
    /// Returns true when the reply may enter the substrate
    /// (<c>session.PendingMessages</c>), false when it must be dropped.
    ///
    /// Note: returns true (allow-persist) when the gate is unregistered,
    /// flag-off, or empty content — the no-op cases. Returns false ONLY
    /// when the gate evaluated and returned a non-Pass verdict. Gate
    /// exceptions are non-fatal: log + allow persist (observability bugs
    /// in the gate must NOT block voice replies from entering the
    /// active thread).
    /// </summary>
    internal async Task<bool> EvaluateVoiceReplyForSubstrateAsync(
        string reply,
        IReadOnlyList<ConversationMessage> threadMessages,
        CancellationToken ct)
    {
        if (_outputGate is null || !_aniOptions.VoiceTurnOutputGateEnabled)
            return true;
        if (string.IsNullOrWhiteSpace(reply))
            return true;

        var contactRecent = threadMessages
            .Where(m => m.Role == Roles.Mark)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .TakeLast(8)
            .ToList();
        var priorAni = threadMessages
            .Where(m => m.Role == Roles.Ani && m.Content != reply)
            .Select(m => m.Content)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .TakeLast(8)
            .ToList();

        var artifact = new CognitiveArtifact
        {
            Content                 = reply,
            ProducerKind            = CognitiveProducerKind.Voice,
            IntendedSink            = CognitiveOutputSink.Dispatch,
            ContactName             = Roles.Mark,
            GeneratedAt             = DateTimeOffset.Now,
            ContactRecentMessages   = contactRecent,
            PriorAniMessages        = priorAni,
        };

        try
        {
            var result = await _outputGate.EvaluateAsync(artifact, ct).ConfigureAwait(false);
            if (result.Verdict == OutputGateVerdict.Pass)
                return true;

            _log.LogWarning(
                "J.5f voice gate {Verdict} [{Fired}] — dropping reply from substrate (audio already played; reply will NOT enter conversation_messages). Hint: {Hint}",
                result.Verdict, string.Join(",", result.FiredInvariants), result.RemediationHint);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "J.5f voice gate threw — allowing substrate persist uncovered (gate failure must NOT block voice substrate writes).");
            return true;
        }
    }

    /// <summary>
    /// Synthesize a greeting via TTS. Simpler than a full turn — no LLM, no context.
    /// </summary>
    public async Task SynthesizeGreetingAsync(
        VoiceSessionState session,
        string greeting,
        IStreamingTextToSpeechService tts,
        Func<object, CancellationToken, Task> sendJson,
        CancellationToken ct)
    {
        session.SetSpeaking(true);

        await sendJson(new { type = "reply_start" }, ct).ConfigureAwait(false);

        await tts.SendTextAsync(greeting, ct).ConfigureAwait(false);
        await tts.FlushAsync(ct).ConfigureAwait(false);

        await sendJson(new { type = "reply_end" }, ct).ConfigureAwait(false);

        // Do NOT send "listening" or clear IsAniSpeaking here.
        // The orchestrator handles this when the client signals "playback_done".
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
}
