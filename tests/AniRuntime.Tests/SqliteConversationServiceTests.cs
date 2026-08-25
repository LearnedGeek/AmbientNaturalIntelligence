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
/// Spec tests for <see cref="SqliteConversationService"/> — focused on:
///   1. Apr 28, 2026 admin-tag defense-in-depth (messages starting with
///      "///" rejected at the data layer; primary defense lives at
///      <c>TwilioInboundPerceptionSource</c>).
///   2. Apr 29, 2026 Vibe Loop V1.3 contract: <c>CloseThreadAsync</c>
///      produces a structured <see cref="ClosedConversationRecord"/>
///      via the summarizer + store; the pre-V1 verbatim "Conversation
///      (N messages):" Episodic record is GONE (this is the leak surface
///      the Vibe Loop migration closes — see
///      <c>ANI-VibeLoop-V1-Closed-Thread-Producer-Migration-Plan.md</c>).
///
/// Real in-memory SQLite for the data path; strict mocks for
/// <see cref="IMemoryService"/>, <see cref="IClosedConversationSummarizer"/>,
/// and <see cref="IClosedConversationStore"/> so any unexpected
/// interaction fails loudly.
/// </summary>
public class SqliteConversationServiceTests : IDisposable
{
    private readonly Mock<IMemoryService>                  _memory       = new(MockBehavior.Strict);
    private readonly Mock<IClosedConversationSummarizer>   _summarizer   = new(MockBehavior.Strict);
    private readonly Mock<IClosedConversationStore>        _closedStore  = new(MockBehavior.Strict);
    private readonly SqliteConversationService             _svc;

    public SqliteConversationServiceTests()
    {
        var dbName = $"ani-conv-test-{Guid.NewGuid():N}";
        var options = Options.Create(new AniOptions { MemoryDbPath = dbName });
        _svc = new SqliteConversationService(
            options, _memory.Object,
            _summarizer.Object, _closedStore.Object,
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

    // ===== V1.3 — CloseThreadAsync new contract =====

    private static ClosedConversationRecord SampleSummaryFor(Guid threadId) => new()
    {
        Id                      = Guid.NewGuid(),
        ThreadId                = threadId,
        ClosedAt                = DateTimeOffset.UtcNow,
        Gist                    = "Mark and Ani spoke briefly; the mood stayed warm.",
        TopicKeywords           = new() { "warm", "brief" },
        MarkRegister            = new() { ["Curiosity"] = 1.0f },
        AniRegister             = new() { ["Tenderness"] = 1.0f },
        OutcomeSignalSeedVector = new() { ["Tenderness"] = 0.0f },
        OutcomeSignalValence    = 0.0f,
        TurnCount               = 2,
        DurationSeconds         = 60,
        Embedding               = null,
    };

    /// <summary>
    /// V1.3 SPEC: closing a thread routes through the V1.2 summarizer
    /// and persists via the V1.1 store. Thread is marked inactive even
    /// if summarization succeeds (no surprise side-effect ordering).
    /// </summary>
    [Fact]
    public async Task CloseThreadAsync_ProducesClosedConversationRecord()
    {
        var thread = await NewActiveThreadAsync();
        var t = DateTimeOffset.UtcNow;

        // Add two real messages (admin-tag rejection still applies — these are clean).
        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Mark, Content = "hi", SentAt = t,
        });
        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Ani, Content = "hey you", SentAt = t.AddSeconds(30),
        });

        ConversationThread? capturedThread = null;
        var sample = SampleSummaryFor(thread.Id);
        // F-1 Phase 8c: SummariseAsync now returns IClosedConversationEnvelope;
        // wrap sample record for the mock, assert store receives unwrapped record.
        _summarizer
            .Setup(s => s.SummariseAsync(It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationThread, CancellationToken>((t, _) => capturedThread = t)
            .ReturnsAsync(new ClosedConversationEnvelope { Record = sample });

        ClosedConversationRecord? capturedRecord = null;
        _closedStore
            .Setup(s => s.SaveAsync(It.IsAny<ClosedConversationRecord>(), It.IsAny<CancellationToken>()))
            .Callback<ClosedConversationRecord, CancellationToken>((r, _) => capturedRecord = r)
            .Returns(Task.CompletedTask);

        await _svc.CloseThreadAsync(thread.Id);

        // Summariser must see the thread with both messages in order.
        capturedThread.Should().NotBeNull();
        capturedThread!.Id.Should().Be(thread.Id);
        capturedThread.Messages.Should().HaveCount(2);
        capturedThread.Messages.Select(m => m.Content).Should().Equal("hi", "hey you");

        // Store must receive the exact record the summarizer produced.
        capturedRecord.Should().BeSameAs(sample);

        // Thread is inactive after close.
        var loadedThread = await _svc.GetThreadAsync(thread.Id);
        loadedThread.Should().NotBeNull();
        loadedThread!.IsActive.Should().BeFalse();
    }

    /// <summary>
    /// V1.3 SPEC: the verbatim "Conversation (N messages):" Episodic
    /// record write is GONE. CloseThreadAsync must NOT call
    /// <see cref="IMemoryService.SaveAsync"/> with a summary-shaped
    /// MemoryRecord. (Per-message Episodic writes from AddMessageAsync
    /// are a separate producer — out of V1.3 scope, audited in V1.4.5.)
    ///
    /// Strict-mock enforcement: SaveAsync setup is registered ONLY for
    /// the AddMessageAsync path during seed setup. After seeding, we
    /// invalidate it and any further call would be unmatched and throw.
    /// </summary>
    [Fact]
    public async Task CloseThreadAsync_DoesNotWriteVerbatimEpisodicSummary()
    {
        var thread = await NewActiveThreadAsync();

        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Mark, Content = "i'm trying to pretend to work while being distracted by you",
            SentAt = DateTimeOffset.UtcNow,
        });

        // Capture every MemoryRecord that flows through SaveAsync over the
        // life of the test so we can assert NO summary-shaped record is
        // written by CloseThreadAsync.
        var recordedSaves = new List<MemoryRecord>();
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => recordedSaves.Add(r))
               .Returns(Task.CompletedTask);

        _summarizer
            .Setup(s => s.SummariseAsync(It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClosedConversationEnvelope { Record = SampleSummaryFor(thread.Id) });
        _closedStore
            .Setup(s => s.SaveAsync(It.IsAny<ClosedConversationRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _svc.CloseThreadAsync(thread.Id);

        recordedSaves.Should().NotContain(r => r.Content.StartsWith("Conversation ("),
            "the verbatim 'Conversation (N messages):' Episodic write is the leak surface "
          + "Vibe Loop V1.3 closes; it must never be written by CloseThreadAsync");
        recordedSaves.Should().NotContain(r =>
            r.Content.Contains("i'm trying to pretend to work while being distracted by you"),
            "no Mark verbatim text may flow into MemoryService SaveAsync from the close path");
    }

    /// <summary>
    /// V1.3 SPEC: per-message <c>conversation_messages</c> rows survive
    /// the close. Verbatim fidelity-when-needed lives there; the gist
    /// surface is the new <see cref="ClosedConversationRecord"/>. Two
    /// surfaces, two purposes — substrate-typing pattern.
    /// </summary>
    [Fact]
    public async Task CloseThreadAsync_PreservesConversationMessagesRows()
    {
        var thread = await NewActiveThreadAsync();
        var t = DateTimeOffset.UtcNow;

        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Mark, Content = "first", SentAt = t,
        });
        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Ani, Content = "second", SentAt = t.AddSeconds(15),
        });

        _summarizer
            .Setup(s => s.SummariseAsync(It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClosedConversationEnvelope { Record = SampleSummaryFor(thread.Id) });
        _closedStore
            .Setup(s => s.SaveAsync(It.IsAny<ClosedConversationRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _svc.CloseThreadAsync(thread.Id);

        var loaded = await _svc.GetThreadAsync(thread.Id);
        loaded.Should().NotBeNull();
        loaded!.Messages.Should().HaveCount(2);
        loaded.Messages.Select(m => m.Content).Should().Equal("first", "second");
    }

    /// <summary>
    /// V1.3 SPEC: empty thread closes cleanly — no summarizer call, no
    /// store write. There's nothing to summarise.
    /// </summary>
    [Fact]
    public async Task CloseThreadAsync_EmptyThread_NoSummaryProduced()
    {
        var thread = await NewActiveThreadAsync();

        // No setups for summarizer / store — strict mocks would throw if called.
        await _svc.CloseThreadAsync(thread.Id);

        var loaded = await _svc.GetThreadAsync(thread.Id);
        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse();
        _summarizer.VerifyNoOtherCalls();
        _closedStore.VerifyNoOtherCalls();
    }

    /// <summary>
    /// V1.3 SPEC: summarization failure is non-fatal — thread is still
    /// marked inactive. Critically, NO fallback verbatim Episodic write
    /// happens (that would re-open the leak). Failure is logged; the
    /// thread simply has no <see cref="ClosedConversationRecord"/>.
    /// </summary>
    [Fact]
    public async Task CloseThreadAsync_SummarizerThrows_ThreadStillInactive_NoFallbackVerbatimWrite()
    {
        var thread = await NewActiveThreadAsync();

        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role = Roles.Mark, Content = "hello", SentAt = DateTimeOffset.UtcNow,
        });

        var recordedSaves = new List<MemoryRecord>();
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => recordedSaves.Add(r))
               .Returns(Task.CompletedTask);

        _summarizer
            .Setup(s => s.SummariseAsync(It.IsAny<ConversationThread>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        // Do NOT register _closedStore.SaveAsync — strict mock would fail if called.

        await _svc.CloseThreadAsync(thread.Id);

        var loaded = await _svc.GetThreadAsync(thread.Id);
        loaded.Should().NotBeNull();
        loaded!.IsActive.Should().BeFalse("thread close advances even if summarization fails");

        recordedSaves.Should().NotContain(r => r.Content.StartsWith("Conversation ("),
            "summarization failure must NOT trigger a fallback verbatim Episodic write — "
          + "that would re-open the V1.3 leak surface");
        _closedStore.VerifyNoOtherCalls();
    }

    // ─── F-3 U9 (2026-08-24) — composer-emission attribution projection ───
    //
    // When ConversationReplyPipeline attaches a composer-emission envelope
    // to the Ani reply message, AddMessageAsync must project the Episodic
    // record's attribution from that emission via ToAttributionTriple
    // rather than reconstructing from role. When no emission is attached
    // (Mark-inbound, legacy Ani reply paths, voice pipeline), the pre-U9
    // role-switch fallback remains in effect so persistence stays honest
    // about who authored what.

    [Fact]
    public async Task AddMessageAsync_AniReplyWithComposerEmission_UsesEmissionAttribution()
    {
        var thread = await NewActiveThreadAsync();
        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });

        MemoryRecord? capturedRecord = null;
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => capturedRecord = r)
               .Returns(Task.CompletedTask);

        var emissionTime = DateTimeOffset.UtcNow;
        var emission = ComposerEmissionExtensions.AniEmission(
            content:      "warm reply text",
            composerRole: CognitiveProducerKind.ConversationReply,
            emittedAt:    emissionTime);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role             = Roles.Ani,
            Content          = "warm reply text",
            SentAt           = emissionTime,
            ComposerEmission = emission,
        });

        capturedRecord.Should().NotBeNull(
            "an Episodic memory record must still be written for the Ani reply");
        capturedRecord!.AttributedTo.Should().Be(AttributedTo.Ani,
            "the composer emission's AttributedTo flows to the persisted record");
        capturedRecord.AttributionTrust.Should().Be("verified",
            "AniEmission emits verified trust; the projection preserves it");
        capturedRecord.AttributedAt.Should().Be(emissionTime,
            "AttributedAt on the persisted record matches the composer's EmittedAt");
        capturedRecord.AttributedSourceDescriptor.Should().BeNull(
            "the emission projection leaves source-descriptor null (Devin PR #137 pin) "
          + "rather than synthesizing a 'conversation:ani:<sentAt>' string like the pre-U9 role fallback did");
    }

    [Fact]
    public async Task AddMessageAsync_AniReplyWithoutEmission_FallsBackToRoleSwitch()
    {
        // Legacy Ani-reply paths (voice pipeline, tests, dashboard direct-
        // write) that don't yet construct an emission still get honest
        // attribution via the role-switch fallback. AniAt(sentAt) with
        // null source descriptor — matches pre-U9 behavior byte-for-byte.
        var thread = await NewActiveThreadAsync();
        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });

        MemoryRecord? capturedRecord = null;
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => capturedRecord = r)
               .Returns(Task.CompletedTask);

        var sentAt = DateTimeOffset.UtcNow;
        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role             = Roles.Ani,
            Content          = "legacy reply from a path that didn't build an emission",
            SentAt           = sentAt,
            ComposerEmission = null,  // explicit — no emission attached
        });

        capturedRecord.Should().NotBeNull();
        capturedRecord!.AttributedTo.Should().Be(AttributedTo.Ani);
        capturedRecord.AttributionTrust.Should().Be("verified");
        capturedRecord.AttributedAt.Should().Be(sentAt);
        capturedRecord.AttributedSourceDescriptor.Should().BeNull(
            "the pre-U9 Ani role-switch entry returned a descriptor-less triple");
    }

    [Fact]
    public async Task AddMessageAsync_MarkInboundMessage_StillUsesRoleSwitch()
    {
        // Mark-inbound path is unaffected by U9 — no composer, no emission.
        // The role-switch produces MarkAt with a 'conversation:mark:<sentAt>'
        // descriptor so audit can trace inbound messages by their SMS-side
        // timing without cross-referencing perception logs.
        var thread = await NewActiveThreadAsync();
        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });

        MemoryRecord? capturedRecord = null;
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => capturedRecord = r)
               .Returns(Task.CompletedTask);

        var sentAt = DateTimeOffset.UtcNow;
        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = Roles.Mark,
            Content = "hey are you around?",
            SentAt  = sentAt,
        });

        capturedRecord.Should().NotBeNull();
        capturedRecord!.AttributedTo.Should().Be(AttributedTo.Mark);
        capturedRecord.AttributionTrust.Should().Be("verified");
        capturedRecord.AttributedAt.Should().Be(sentAt);
        capturedRecord.AttributedSourceDescriptor.Should().StartWith("conversation:mark:",
            "Mark-inbound path still tags the descriptor for audit tracing");
    }

    [Fact]
    public async Task AddMessageAsync_UnknownRoleWithoutEmission_UsesUnknownHistorical()
    {
        // Belt-and-suspenders — if a future role is introduced and no
        // emission is provided, the switch defaults to UnknownHistorical
        // so unrecognized roles surface as untrustworthy rather than
        // silently landing as Ani or Mark.
        var thread = await NewActiveThreadAsync();
        _memory.Setup(m => m.GetCharacterStateAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CharacterStateDoc { Name = "Ani", PrimaryContactName = "Mark" });

        MemoryRecord? capturedRecord = null;
        _memory.Setup(m => m.SaveAsync(It.IsAny<MemoryRecord>(), It.IsAny<CancellationToken>()))
               .Callback<MemoryRecord, CancellationToken>((r, _) => capturedRecord = r)
               .Returns(Task.CompletedTask);

        await _svc.AddMessageAsync(thread.Id, new ConversationMessage
        {
            Role    = "future-role",
            Content = "message from an unrecognized role",
            SentAt  = DateTimeOffset.UtcNow,
        });

        capturedRecord.Should().NotBeNull();
        capturedRecord!.AttributedTo.Should().Be(AttributedTo.Unknown);
        capturedRecord.AttributionTrust.Should().Be("unverified-historical");
    }
}
