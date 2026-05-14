using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AniRuntime.Tests.Regression;

/// <summary>
/// Regression scenarios for <b>FC-001 — Active-thread continuity broken</b>
/// at the <b>ContextCompressor layer</b> (one level up the stack from
/// FC-001a/b/c which exercised the data layer and passed).
///
/// SPEC (what the system MUST do at this layer):
///   <see cref="ContextCompressor.CompressIfNeededAsync"/> takes a
///   <see cref="ConversationThread"/> with <c>Messages</c> populated by the
///   data layer (FC-001a/b/c confirmed this works) and returns a list of
///   <see cref="ChatMessage"/>s for the reply path's RecentHistory. For
///   threads under the compression threshold (≤10 messages — see
///   ContextCompressor.cs:54), the return MUST contain all messages with
///   correct role mapping (Ani→"assistant", Mark→"user") and correct
///   ordering. Specifically: Ani's prior outreach MUST appear in the
///   returned list so it reaches the reply prompt.
///
/// EMPIRICAL CONTEXT (from FC-001 DayOne diagnosis):
///   FC-001a/b/c showed the data layer is fine. The May 12 windshield
///   production failure exhibited self-echo catching a verbatim parrot of
///   Ani's prior outreach — which implies the model DID see the prior
///   outreach (otherwise it couldn't parrot it). So this layer probably
///   passes too. If so, FC-001's true binding layer is even higher —
///   either the SEMANTIC RETRIEVAL pool (separate from chat history) or
///   PromptBuilder's assembly, or it's actually a different failure class
///   entirely (FC-003 + FC-006 + FC-004 compound).
///
/// TDD discipline: this scenario asserts the SPEC. If it passes, we've
/// further localized FC-001 (now ruling out the compressor layer). If it
/// fails, the compressor is dropping the message and we've found the bug.
/// Either outcome is information.
/// </summary>
public class FC001d_ContextCompressor_Tests
{
    private readonly Mock<IOllamaClient> _mockOllama = new();
    private readonly ContextCompressor _compressor;

    private const string AniOutreachContent = "FC001d-FIXTURE: synthetic outreach about a fabricated badge with a heart on it";
    private const string MarkInboundContent = "FC001d-FIXTURE: synthetic inbound asking about the fabricated badge";

    public FC001d_ContextCompressor_Tests()
    {
        _compressor = new ContextCompressor(
            _mockOllama.Object,
            NullLogger<ContextCompressor>.Instance);
    }

    /// <summary>
    /// FC-001d — small-thread compression (no summarization invoked).
    /// SPEC: with a thread of 2 messages [Ani outreach, Mark inbound], the
    /// compressor returns BOTH messages as ChatMessages with correct roles.
    ///
    /// At thread size 2 (≤10), no summarization runs (ContextCompressor.cs:54
    /// returns messages.Count for rawWindow when count ≤ 10). So IOllamaClient
    /// is NOT called and the return is exactly the input messages mapped to
    /// ChatMessage shape.
    /// </summary>
    [Fact]
    public async Task FC001d_TwoMessageThread_BothMessagesReturned_AniOutreachVisibleToReplyPath()
    {
        var thread = new ConversationThread
        {
            Id            = Guid.NewGuid(),
            InitiatedBy   = Roles.Mark,
            StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-15),
            LastMessageAt = DateTimeOffset.UtcNow,
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Ani,  Content = AniOutreachContent, SentAt = DateTimeOffset.UtcNow.AddMinutes(-15) },
                new() { Role = Roles.Mark, Content = MarkInboundContent, SentAt = DateTimeOffset.UtcNow },
            },
        };

        // windowSize is the upper bound; the compressor's own rawWindow logic
        // governs at small thread sizes. Pass a sensible production-default-ish
        // value.
        var history = await _compressor.CompressIfNeededAsync(thread, windowSize: 20, CancellationToken.None);

        history.Should().NotBeNull();
        history.Should().HaveCount(2,
            "small-thread path returns all messages without summarization");

        history.Should().Contain(m =>
            m.Role == "assistant" && m.Content == AniOutreachContent,
            "Ani's prior outreach MUST be in the returned history so the reply path's " +
            "Ollama call can see it. This is the FC-001 SPEC at the compressor layer.");

        history.Should().Contain(m =>
            m.Role == "user" && m.Content == MarkInboundContent,
            "Mark's inbound must also be present (the message being replied to).");

        // Confirm no Ollama summarization call was needed at this size
        _mockOllama.VerifyNoOtherCalls();
    }

    /// <summary>
    /// FC-001d.2 — ordering preserved through compression.
    /// SPEC: returned ChatMessages preserve the chronological ordering of
    /// the thread (oldest first). The Ani outreach (sent FIRST) must appear
    /// at index 0; Mark's inbound (sent SECOND) at index 1.
    /// </summary>
    [Fact]
    public async Task FC001d2_OrderingPreserved_AniOutreachFirst_MarkInboundSecond()
    {
        var thread = new ConversationThread
        {
            Id            = Guid.NewGuid(),
            InitiatedBy   = Roles.Mark,
            StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-15),
            LastMessageAt = DateTimeOffset.UtcNow,
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Ani,  Content = AniOutreachContent, SentAt = DateTimeOffset.UtcNow.AddMinutes(-15) },
                new() { Role = Roles.Mark, Content = MarkInboundContent, SentAt = DateTimeOffset.UtcNow },
            },
        };

        var history = await _compressor.CompressIfNeededAsync(thread, windowSize: 20, CancellationToken.None);

        history.Should().HaveCount(2);
        history[0].Role.Should().Be("assistant", "Ani's outreach was sent FIRST");
        history[0].Content.Should().Be(AniOutreachContent);
        history[1].Role.Should().Be("user", "Mark's inbound followed Ani's outreach");
        history[1].Content.Should().Be(MarkInboundContent);
    }
}
