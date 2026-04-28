using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

public class VoiceTurnPipelineTests
{
    private readonly Mock<IMemoryService> _mockMemory = new();
    // Theme K Phase K.1 (Apr 28, 2026): IConversationService is strict.
    // VoiceTurnPipeline only calls GetThreadAsync — declare it once in CreatePipeline.
    private readonly Mock<IConversationService> _mockConversations = new(MockBehavior.Strict);
    private readonly Mock<IOllamaClient> _mockOllama = new();
    private readonly Mock<IStreamingTextToSpeechService> _mockTts = new();

    private VoiceTurnPipeline CreatePipeline()
    {
        // Default mock setups
        _mockMemory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CharacterStateDoc());
        _mockMemory.Setup(m => m.GetEmotionalStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmotionalState());
        _mockMemory.Setup(m => m.GetAnchoredMemoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryRecord>());

        _mockConversations.Setup(c => c.GetThreadAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationThread());

        return new VoiceTurnPipeline(
            _mockMemory.Object,
            _mockConversations.Object,
            _mockOllama.Object,
            Options.Create(new OllamaOptions { ChatModel = "test-model" }),
            NullLogger<VoiceTurnPipeline>.Instance);
    }

    private static VoiceSessionState CreateSession() =>
        new("test-session", Guid.NewGuid(), null!);

    private static Func<object, CancellationToken, Task> NoOpSendJson =>
        (_, _) => Task.CompletedTask;

    [Fact]
    public async Task ProcessTurnAsync_ShortTranscript_ReturnsNull()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();

        var result = await pipeline.ProcessTurnAsync(
            session, "hi", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        result.Should().BeNull();
        session.TurnCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessTurnAsync_WhitespaceTranscript_ReturnsNull()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();

        var result = await pipeline.ProcessTurnAsync(
            session, "   ", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ProcessTurnAsync_ValidTranscript_BuffersMarkMessage()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();

        // Mock LLM to return a simple response
        _mockOllama.Setup(o => o.ChatStreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncTokens("Hello", " Mark!"));

        var result = await pipeline.ProcessTurnAsync(
            session, "hey there Ani", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        // Mark's message should be buffered
        session.PendingMessages.Should().Contain(m => m.Role == "mark" && m.Content == "hey there Ani");
    }

    [Fact]
    public async Task ProcessTurnAsync_ValidTranscript_BuffersAniReply()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();

        _mockOllama.Setup(o => o.ChatStreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncTokens("Hey!", " How are you?"));

        var result = await pipeline.ProcessTurnAsync(
            session, "hello Ani how are you", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        result.Should().NotBeNullOrWhiteSpace();
        session.PendingMessages.Should().Contain(m => m.Role == "ani");
    }

    [Fact]
    public async Task ProcessTurnAsync_SetsSpeakingDuringReply()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();
        bool wasSpeaking = false;

        _mockOllama.Setup(o => o.ChatStreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                wasSpeaking = session.IsAniSpeaking;
                return AsyncTokens("reply.");
            });

        await pipeline.ProcessTurnAsync(
            session, "test utterance here", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        wasSpeaking.Should().BeTrue("IsAniSpeaking should be true during LLM generation");
        // IsAniSpeaking stays true after reply — it's only cleared when the client
        // sends "playback_done" (handled by the orchestrator, not the pipeline).
        session.IsAniSpeaking.Should().BeTrue("IsAniSpeaking should remain true until client confirms playback done");
    }

    [Fact]
    public async Task ProcessTurnAsync_DoesNotIncrementTurnCount()
    {
        // Turn count is incremented by EndSpeaking() in the orchestrator
        // when the client signals "playback_done", not by the pipeline.
        var pipeline = CreatePipeline();
        var session = CreateSession();

        _mockOllama.Setup(o => o.ChatStreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncTokens("reply."));

        await pipeline.ProcessTurnAsync(
            session, "test message here", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        session.TurnCount.Should().Be(0, "turn count is incremented by orchestrator on playback_done, not by pipeline");
    }

    [Fact]
    public async Task ProcessTurnAsync_SendsTextToTts()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();

        _mockOllama.Setup(o => o.ChatStreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncTokens("A complete sentence."));

        await pipeline.ProcessTurnAsync(
            session, "send me something", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        _mockTts.Verify(t => t.SendTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _mockTts.Verify(t => t.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessTurnAsync_Cancellation_ClearsSpeaking()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();
        var cts = new CancellationTokenSource();

        _mockOllama.Setup(o => o.ChatStreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // Simulate barge-in during LLM generation
                session.CancelCurrentTurn();
                return AsyncTokens("this will be cancelled.");
            });

        var result = await pipeline.ProcessTurnAsync(
            session, "will be interrupted", _mockTts.Object, NoOpSendJson, cts.Token);

        session.IsAniSpeaking.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessTurnAsync_SendsControlMessages()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();
        var sentTypes = new List<string>();

        _mockOllama.Setup(o => o.ChatStreamAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncTokens("reply."));

        Task TrackingSendJson(object payload, CancellationToken ct)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var typeProp))
                sentTypes.Add(typeProp.GetString()!);
            return Task.CompletedTask;
        }

        await pipeline.ProcessTurnAsync(
            session, "test message here", _mockTts.Object, TrackingSendJson, CancellationToken.None);

        sentTypes.Should().Contain("transcript");
        sentTypes.Should().Contain("reply_start");
        sentTypes.Should().Contain("reply_end");
        // "listening" is no longer sent by the pipeline — it's sent by the orchestrator
        // when the client signals "playback_done" (echo prevention).
    }

    [Fact]
    public async Task SynthesizeGreetingAsync_SetsSpeakingDuringGreeting()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();
        bool wasSpeaking = false;

        _mockTts.Setup(t => t.SendTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => wasSpeaking = session.IsAniSpeaking)
            .Returns(Task.CompletedTask);

        await pipeline.SynthesizeGreetingAsync(
            session, "Hey Mark!", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        wasSpeaking.Should().BeTrue("should be speaking during TTS");
        // IsAniSpeaking stays true — cleared by orchestrator on playback_done
        session.IsAniSpeaking.Should().BeTrue("should remain speaking until client confirms playback done");
    }

    [Fact]
    public async Task SynthesizeGreetingAsync_SendsGreetingToTts()
    {
        var pipeline = CreatePipeline();
        var session = CreateSession();

        await pipeline.SynthesizeGreetingAsync(
            session, "What's up!", _mockTts.Object, NoOpSendJson, CancellationToken.None);

        _mockTts.Verify(t => t.SendTextAsync("What's up!", It.IsAny<CancellationToken>()), Times.Once);
        _mockTts.Verify(t => t.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // Helper: create an IAsyncEnumerable<string> from tokens
    private static async IAsyncEnumerable<string> AsyncTokens(params string[] tokens)
    {
        foreach (var token in tokens)
        {
            yield return token;
            await Task.Yield();
        }
    }
}
