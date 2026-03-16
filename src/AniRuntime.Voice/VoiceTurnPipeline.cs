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
    private readonly ILogger<VoiceTurnPipeline> _log;

    public VoiceTurnPipeline(
        IMemoryService memory,
        IConversationService conversations,
        IOllamaClient ollama,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<VoiceTurnPipeline> log)
    {
        _memory        = memory;
        _conversations = conversations;
        _ollama        = ollama;
        _ollamaOptions = ollamaOptions.Value;
        _log           = log;
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
            Role = "mark", Content = transcript, SentAt = DateTimeOffset.UtcNow,
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

            // Generate streaming reply
            var prompt = PromptBuilder.BuildVoiceReplyPrompt(
                snapshot, thread ?? new ConversationThread());

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

            session.EndSpeaking();

            _log.LogInformation("VoiceTurnPipeline reply: \"{Reply}\"", reply);

            await sendJson(new { type = "reply_end" }, ct).ConfigureAwait(false);
            await sendJson(new { type = "listening" }, ct).ConfigureAwait(false);

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

        // Brief pause for audio to finish streaming before signaling ready
        await Task.Delay(500, ct).ConfigureAwait(false);

        session.SetSpeaking(false);

        await sendJson(new { type = "reply_end" }, ct).ConfigureAwait(false);
        await sendJson(new { type = "listening" }, ct).ConfigureAwait(false);
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
