using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Memory;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Spec tests for <see cref="SqliteConversationService"/> — focused on the
/// Apr 28, 2026 defense-in-depth contract: admin-tag content (messages
/// starting with "///") must be rejected at the data layer and never
/// inserted into <c>conversation_messages</c>.
///
/// These tests use a real in-memory SQLite database (no schema mocking) and
/// a strict <see cref="IMemoryService"/> mock — strict so that any unexpected
/// memory-service interaction during AddMessageAsync would fail loudly. The
/// primary defense lives at <c>TwilioInboundPerceptionSource</c>; this layer
/// is the safety net.
/// </summary>
public class SqliteConversationServiceTests : IDisposable
{
    private readonly Mock<IMemoryService>      _memory = new(MockBehavior.Strict);
    private readonly SqliteConversationService _svc;

    public SqliteConversationServiceTests()
    {
        var dbName = $"ani-conv-test-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions { MemoryDbPath = dbName });
        _svc = new SqliteConversationService(
            options, _memory.Object,
            NullLogger<SqliteConversationService>.Instance);
    }

    public void Dispose() => _svc.Dispose();

    private async Task<ConversationThread> NewActiveThreadAsync()
    {
        var thread = new ConversationThread
        {
            InitiatedBy   = Roles.Mark,
            StartedAt     = DateTimeOffset.UtcNow,
            LastMessageAt = DateTimeOffset.UtcNow,
        };
        await _svc.SaveThreadAsync(thread);
        return thread;
    }

    /// <summary>
    /// CONTROL: a normal message is persisted as expected. Without this we
    /// can't tell whether the admin-rejection tests are passing because of
    /// the short-circuit or because persistence is broken outright.
    /// </summary>
    [Fact]
    public async Task AddMessageAsync_NormalMessage_IsPersisted()
    {
        var thread = await NewActiveThreadAsync();

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = Roles.Mark,
            Content = "hey, just checking in",
            SentAt  = DateTimeOffset.UtcNow,
        });

        var loaded = await _svc.GetThreadAsync(thread.Id);
        loaded.Should().NotBeNull();
        loaded!.Messages.Should().ContainSingle()
              .Which.Content.Should().Be("hey, just checking in");
    }

    /// <summary>
    /// SPEC: admin commands must NOT be persisted to conversation_messages.
    /// This is the data-layer defense-in-depth fix from Apr 28. Even if a
    /// future producer leaks past the perception-source gate, this guard
    /// keeps the substrate clean.
    /// </summary>
    [Theory]
    [InlineData("///outreach was garbage")]
    [InlineData("///that response made no sense")]
    [InlineData("   ///note for review")]            // leading whitespace still detected
    [InlineData("///")]                              // bare prefix
    public async Task AddMessageAsync_AdminCommand_IsRejectedAtDataLayer(string adminBody)
    {
        var thread = await NewActiveThreadAsync();

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = Roles.Mark,
            Content = adminBody,
            SentAt  = DateTimeOffset.UtcNow,
        });

        var loaded = await _svc.GetThreadAsync(thread.Id);
        loaded.Should().NotBeNull();
        loaded!.Messages.Should().BeEmpty(
            $"admin tag content (\"{adminBody}\") must never enter conversation_messages — " +
            "it would otherwise leak into substrate via CloseThreadAsync summaries and " +
            "structured per-speaker context surfaces");
    }

    /// <summary>
    /// SPEC: the admin short-circuit must run BEFORE the INSERT, not after.
    /// Mixing admin and normal messages in the same thread must leave only
    /// the normal messages — the admin row never appears.
    ///
    /// Pre-Apr-28 history: an earlier short-circuit only skipped the
    /// downstream Episodic-memory save AFTER the row was already inserted.
    /// That left the admin row in conversation_messages, where
    /// CloseThreadAsync would later surface it. This test pins the corrected
    /// ordering.
    /// </summary>
    [Fact]
    public async Task AddMessageAsync_AdminBetweenNormalMessages_OnlyNormalsPersisted()
    {
        var thread = await NewActiveThreadAsync();
        var t = DateTimeOffset.UtcNow;

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Mark, Content = "first message", SentAt = t,
        });
        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Mark, Content = "///admin tag in the middle", SentAt = t.AddSeconds(1),
        });
        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Mark, Content = "third message", SentAt = t.AddSeconds(2),
        });

        var loaded = await _svc.GetThreadAsync(thread.Id);
        loaded.Should().NotBeNull();
        loaded!.Messages.Should().HaveCount(2);
        loaded.Messages.Select(m => m.Content).Should().Equal("first message", "third message");
    }
}
