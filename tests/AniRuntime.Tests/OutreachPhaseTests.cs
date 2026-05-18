using AniRuntime.Actions;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using AniRuntime.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Spec tests for the Apr 29, 2026 Theme E fix:
/// **Outbound conversation_messages invariant**. Every Ani-outbound
/// (outreach OR reactive share) must enter the active conversation thread
/// so subsequent cycles' structured per-speaker summary (Theme J.2) sees
/// what Ani sent. Without this, Mark's follow-up questions land in cycles
/// where Ani has no thread-context for her own outbound and confabulates
/// *"you made that up"* responses (Apr 29 09:04 Anxietyland trace +
/// Apr 29 10:24 compounding instance).
///
/// Tests target <see cref="OutreachPhase.RecordOutboundInThreadAsync"/>
/// (internal helper) directly via InternalsVisibleTo. The helper is the
/// new contract; testing it in isolation pins it without dragging in the
/// full OutreachPhase pipeline.
/// </summary>
public class OutreachPhaseTests : AniTestBase
{
    private readonly Mock<IConversationService> _mockConversations = new(MockBehavior.Strict);

    private OutreachPhase BuildPhase()
    {
        var dispatcher = new AniActionDispatcher(
            Array.Empty<IAniAction>(),
            NullLogger<AniActionDispatcher>.Instance);

        var desire = new DesireEngine(
            MockMemory.Object, MockMemory.Object, DefaultOptions,
            NullLogger<DesireEngine>.Instance);

        var claimVerifier = new ClaimVerificationPhase(
            MockMemory.Object, MockOllama.Object, DefaultOptions,
            NullLogger<ClaimVerificationPhase>.Instance);

        return OutreachPhaseTestBuilder.Build(
            MockMemory.Object,
            MockMemory.Object,
            MockMemory.Object,
            MockOllama.Object,
            dispatcher,
            desire,
            claimVerifier,
            _mockConversations.Object,
            DefaultOptions);
    }

    /// <summary>
    /// SPEC: when an active thread exists, the outbound message is appended
    /// to that thread with Role=Roles.Ani. No new thread is created.
    /// </summary>
    [Fact]
    public async Task RecordOutboundInThreadAsync_WithActiveThread_AppendsToExistingThread()
    {
        var existingThreadId = Guid.NewGuid();
        var activeThread = new ConversationThread
        {
            Id            = existingThreadId,
            InitiatedBy   = Roles.Mark,
            StartedAt     = DateTimeOffset.UtcNow.AddMinutes(-30),
            LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync(activeThread);
        _mockConversations.Setup(c => c.AddMessageAsync(
                existingThreadId,
                It.Is<ConversationMessage>(m => m.Role == Roles.Ani && m.Content == "hey, just thinking about you"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var phase = BuildPhase();
        await phase.RecordOutboundInThreadAsync("hey, just thinking about you", CancellationToken.None);

        _mockConversations.Verify(c => c.SaveThreadAsync(
            It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()), Times.Never,
            "an existing active thread must not trigger a new thread creation");
        _mockConversations.Verify(c => c.AddMessageAsync(
            existingThreadId,
            It.Is<ConversationMessage>(m => m.Role == Roles.Ani && m.Content == "hey, just thinking about you"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// SPEC: when no active thread exists, a new thread is created with
    /// InitiatedBy=Roles.Ani (this outbound IS the thread initiation), and
    /// the message is appended.
    /// </summary>
    [Fact]
    public async Task RecordOutboundInThreadAsync_NoActiveThread_CreatesNewThreadWithAniInitiated()
    {
        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync((ConversationThread?)null);
        _mockConversations.Setup(c => c.SaveThreadAsync(
                It.Is<ConversationThread>(t => t.InitiatedBy == Roles.Ani),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConversations.Setup(c => c.AddMessageAsync(
                It.IsAny<Guid>(),
                It.Is<ConversationMessage>(m => m.Role == Roles.Ani),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var phase = BuildPhase();
        await phase.RecordOutboundInThreadAsync("opening message that starts a thread", CancellationToken.None);

        _mockConversations.Verify(c => c.SaveThreadAsync(
            It.Is<ConversationThread>(t => t.InitiatedBy == Roles.Ani),
            It.IsAny<CancellationToken>()), Times.Once,
            "a missing active thread must trigger a new thread with InitiatedBy=Ani");
        _mockConversations.Verify(c => c.AddMessageAsync(
            It.IsAny<Guid>(),
            It.Is<ConversationMessage>(m => m.Role == Roles.Ani && m.Content == "opening message that starts a thread"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// SPEC: the recorded message uses the EXACT content that was dispatched —
    /// not the Episodic-memory format (e.g. *"Ani shared with Mark: ..."*) —
    /// because the conversation thread should reflect what was sent in the
    /// channel, not the meta-record format. The Apr 29 10:24 case where Ani
    /// misread her own dispatched content via the Episodic prefix grammar is
    /// the canonical failure this guards against.
    /// </summary>
    [Fact]
    public async Task RecordOutboundInThreadAsync_PersistsRawDispatchedContent_NotEpisodicMetaFormat()
    {
        const string dispatchedText = "OMG did you see this?? they created a theme park map of Anxietyland";

        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ReturnsAsync(new ConversationThread { Id = Guid.NewGuid() });
        _mockConversations.Setup(c => c.AddMessageAsync(
                It.IsAny<Guid>(),
                It.Is<ConversationMessage>(m => m.Content == dispatchedText),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var phase = BuildPhase();
        await phase.RecordOutboundInThreadAsync(dispatchedText, CancellationToken.None);

        // Strict mock: if the helper had passed an Episodic-format string
        // ("Ani shared with Mark: ..."), the Setup wouldn't match and the
        // strict mock would raise. The Verify is belt-and-suspenders.
        _mockConversations.Verify(c => c.AddMessageAsync(
            It.IsAny<Guid>(),
            It.Is<ConversationMessage>(m => m.Content == dispatchedText),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// SPEC: a failure in the thread-write path must NOT bubble up as an
    /// exception. The dispatch already succeeded (Twilio sent the message);
    /// a thread-write hiccup logging-and-swallowing is correct. The
    /// alternative — bubbling the exception — would mark a successful
    /// outreach as a cycle failure, which is worse than the bug this fix
    /// addresses.
    /// </summary>
    [Fact]
    public async Task RecordOutboundInThreadAsync_ThreadWriteFailure_SwallowsExceptionAndLogs()
    {
        _mockConversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                          .ThrowsAsync(new InvalidOperationException("simulated DB failure"));

        var phase = BuildPhase();

        // Should NOT throw — failure is logged and swallowed because dispatch
        // already succeeded upstream of this call.
        var act = async () => await phase.RecordOutboundInThreadAsync("any content", CancellationToken.None);
        await act.Should().NotThrowAsync(
            "a thread-write failure must not bubble — dispatch already succeeded and bubbling would mis-classify a successful outreach as a cycle failure");
    }
}
