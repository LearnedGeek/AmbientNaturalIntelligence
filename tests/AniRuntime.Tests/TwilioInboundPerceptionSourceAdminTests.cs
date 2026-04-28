using System.Net;
using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Perception;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Spec tests for the Apr 28, 2026 architectural fix that routes admin commands
/// (messages starting with "///") DIRECTLY to <see cref="IAdminCommandHandler"/>
/// at the perception source — bypassing thread creation, message persistence,
/// and perception-event emission. These tests pin the architectural contract:
/// admin content must never reach the substrate via the conversation pipeline.
///
/// Tests use <see cref="MockBehavior.Strict"/> per the Apr 28 strict-mock
/// migration policy (~/.claude/TESTING-STRATEGY.md §20). Every interaction is
/// declared up front; an unexpected call to the conversation service or session
/// notifier indicates a regression of the architectural contract.
/// </summary>
public class TwilioInboundPerceptionSourceAdminTests
{
    // Strict mocks: every call MUST be set up, or the test fails. This is the
    // entire point of these tests — admin commands should not call most of these.
    private readonly Mock<IConversationService> _conversations = new(MockBehavior.Strict);
    private readonly Mock<IMemoryService>       _memory        = new(MockBehavior.Strict);
    private readonly Mock<IAdminCommandHandler> _adminHandler  = new(MockBehavior.Strict);
    private readonly Mock<IHttpClientFactory>   _httpFactory   = new(MockBehavior.Strict);
    private readonly Mock<ISessionNotifier>     _notifier      = new(MockBehavior.Strict);

    private readonly TwilioOptions _twilioOptions = new()
    {
        AccountSid     = "AC_test_sid",
        AuthToken      = "test_auth_token",
        FromNumber     = "+15555550000",
        ToNumber       = "+15555550001",
        InboundEnabled = true,
    };

    private readonly AniOptions _aniOptions = new()
    {
        ConversationTimeoutMinutes = 30,
    };

    public TwilioInboundPerceptionSourceAdminTests()
    {
        // Notifier fires on EnqueueInbound — common to every test, set up once.
        _notifier.Setup(n => n.OnMessageReceived());

        // FetchInboundMessagesAsync runs unconditionally and hits Twilio's REST
        // API. We return an empty messages array so the only message processed
        // is the one we enqueued via the webhook path (the path that mirrors
        // production for inbound SMS arriving at /sms/inbound).
        var fakeHandler = new FakeHttpMessageHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[]}"),
            });
        var httpClient = new HttpClient(fakeHandler);
        _httpFactory.Setup(f => f.CreateClient("twilio")).Returns(httpClient);

        // Character lookup happens before per-message processing — common.
        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { PrimaryContactName = "Mark" });

        // CheckConversationTimeoutAsync runs at end of every PollAsync.
        // No active thread is the "nothing to time out" case — no further calls.
        _conversations.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync((ConversationThread?)null);
    }

    private TwilioInboundPerceptionSource CreateSource() =>
        new(_conversations.Object,
            _memory.Object,
            _adminHandler.Object,
            Options.Create(_twilioOptions),
            Options.Create(_aniOptions),
            _httpFactory.Object,
            _notifier.Object,
            NullLogger<TwilioInboundPerceptionSource>.Instance);

    /// <summary>
    /// SPEC: when an admin command arrives, the perception source must hand it
    /// directly to <see cref="IAdminCommandHandler.HandleAsync"/>. This is the
    /// entire reason the dependency exists at this layer.
    /// </summary>
    [Fact]
    public async Task PollAsync_AdminCommand_DispatchesToAdminHandler()
    {
        const string adminBody = "///outreach was garbage";
        _adminHandler.Setup(a => a.HandleAsync(adminBody, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(true);

        var source = CreateSource();
        source.EnqueueInbound("SM_admin_1", adminBody, DateTimeOffset.UtcNow);

        await source.PollAsync(DateTimeOffset.UtcNow.AddMinutes(-5));

        _adminHandler.Verify(
            a => a.HandleAsync(adminBody, It.IsAny<CancellationToken>()),
            Times.Once,
            "admin commands must be routed directly to the admin handler at the perception layer");
    }

    /// <summary>
    /// SPEC: admin commands must NOT create or mutate conversation threads.
    /// Strict mocks guarantee this — any call to SaveThreadAsync or
    /// AddMessageAsync would fail the test, because we never set those up.
    ///
    /// This is the architectural invariant that prevents admin tags from
    /// appearing in Ani's conversation substrate (the regression we saw
    /// Apr 28 when "outreach was garbage" leaked into Ani's inner thoughts).
    /// </summary>
    [Fact]
    public async Task PollAsync_AdminCommand_DoesNotPersistToConversation()
    {
        const string adminBody = "///please stop using exclamation points";
        _adminHandler.Setup(a => a.HandleAsync(adminBody, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(true);

        var source = CreateSource();
        source.EnqueueInbound("SM_admin_2", adminBody, DateTimeOffset.UtcNow);

        await source.PollAsync(DateTimeOffset.UtcNow.AddMinutes(-5));

        // Strict mocks would have already failed if any of these were invoked.
        // Explicit Verify(...Never) is belt-and-suspenders documentation.
        _conversations.Verify(c => c.SaveThreadAsync(
            It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()), Times.Never);
        _conversations.Verify(c => c.AddMessageAsync(
            It.IsAny<Guid>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// SPEC: admin commands must NOT emit a PerceptionEvent. PerceptionEvents
    /// flow into the cognitive cycle's context window — emitting one would
    /// mean Ani sees the admin command as if it were a relational message
    /// from Mark, which is exactly the substrate-leak bug.
    /// </summary>
    [Fact]
    public async Task PollAsync_AdminCommand_DoesNotEmitPerceptionEvent()
    {
        const string adminBody = "///note for tomorrow's review";
        _adminHandler.Setup(a => a.HandleAsync(adminBody, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(true);

        var source = CreateSource();
        source.EnqueueInbound("SM_admin_3", adminBody, DateTimeOffset.UtcNow);

        var events = await source.PollAsync(DateTimeOffset.UtcNow.AddMinutes(-5));

        events.Should().BeEmpty(
            "admin commands are administrative metadata — they must not become " +
            "PerceptionEvents that feed the cognitive cycle's context window");
    }

    /// <summary>
    /// SPEC: even if the admin handler throws, the perception source must NOT
    /// fall through to the conversation pipeline. The handler failure is logged
    /// and the message is still consumed — it is never re-classified as a
    /// relational message just because the admin path errored.
    ///
    /// This prevents the "graceful degradation" anti-pattern where a failed
    /// admin handler would surreptitiously turn the command into substrate.
    /// </summary>
    [Fact]
    public async Task PollAsync_AdminCommandHandlerThrows_StillSkipsConversationPipeline()
    {
        const string adminBody = "///broken handler scenario";
        _adminHandler.Setup(a => a.HandleAsync(adminBody, It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new InvalidOperationException("simulated handler failure"));

        var source = CreateSource();
        source.EnqueueInbound("SM_admin_4", adminBody, DateTimeOffset.UtcNow);

        var events = await source.PollAsync(DateTimeOffset.UtcNow.AddMinutes(-5));

        events.Should().BeEmpty("a failed admin handler must not promote the command to a perception event");
        _conversations.Verify(c => c.SaveThreadAsync(
            It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()), Times.Never);
        _conversations.Verify(c => c.AddMessageAsync(
            It.IsAny<Guid>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// CONTROL: a normal (non-admin) message MUST still flow through the
    /// conversation pipeline. Without this counterpart, the "skip" tests
    /// could pass by accident (e.g. if all messages were short-circuited).
    /// </summary>
    [Fact]
    public async Task PollAsync_NormalMessage_FlowsThroughConversationPipeline()
    {
        const string normalBody = "hey, just thinking about you";
        var sentAt = DateTimeOffset.UtcNow;

        // Override the GetActiveThreadAsync setup from the constructor: this
        // test needs both the initial-thread-lookup call AND the timeout-check
        // call to return null. Both calls match the same setup, so a single
        // ReturnsAsync(null) covers both invocations.
        // (Setup is already defined in the constructor; nothing extra needed.)

        _conversations.Setup(c => c.SaveThreadAsync(
                It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _conversations.Setup(c => c.GetRecentThreadsAsync(1, It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new List<ConversationThread>());

        _memory.Setup(m => m.GetByTypeAsync(
                MemoryType.Episodic, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<MemoryRecord>());

        _conversations.Setup(c => c.AddMessageAsync(
                It.IsAny<Guid>(), It.IsAny<ConversationMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var source = CreateSource();
        source.EnqueueInbound("SM_normal_1", normalBody, sentAt);

        var events = await source.PollAsync(DateTimeOffset.UtcNow.AddMinutes(-5));

        events.Should().HaveCount(1, "a non-admin message must produce exactly one perception event");
        _adminHandler.Verify(a => a.HandleAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "non-admin messages must not invoke the admin handler");
        _conversations.Verify(c => c.AddMessageAsync(
            It.IsAny<Guid>(),
            It.Is<ConversationMessage>(m => m.Content == normalBody && m.Role == Roles.Mark),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request, ct));
    }
}
