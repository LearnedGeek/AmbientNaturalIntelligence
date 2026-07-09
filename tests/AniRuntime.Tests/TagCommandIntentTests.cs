using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Admin.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #93 Phase 2 (2026-07-06) — spec tests for <see cref="TagCommand"/>'s
/// integration with <see cref="ITagIntentClassifier"/>. Pins the substrate-
/// mutation routing based on classifier verdict + confidence.
///
/// <para>
/// Strict-mock discipline (Theme K): every collaborator is mocked with
/// <c>MockBehavior.Strict</c>. Any accidental call surfaces as a test
/// failure rather than a silent stub-return.
/// </para>
/// </summary>
public class TagCommandIntentTests
{
    private static ConversationThread MakeThread(string aniReply, string markMessage)
    {
        return new ConversationThread
        {
            Id = Guid.NewGuid(),
            LastMessageAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Messages = new List<ConversationMessage>
            {
                new() { Role = Roles.Mark, Content = markMessage, SentAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
                new() { Role = Roles.Ani,  Content = aniReply,    SentAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            },
        };
    }

    private sealed class Rig
    {
        public Mock<IConversationService> Conv    { get; }
        public Mock<IMemoryPersistence>   Persist { get; }
        public Mock<ITagIntentClassifier> Class   { get; }
        public TagCommand                 Cmd     { get; }

        public Rig(ConversationThread activeThread)
        {
            Conv    = new Mock<IConversationService>(MockBehavior.Strict);
            Persist = new Mock<IMemoryPersistence>  (MockBehavior.Strict);
            Class   = new Mock<ITagIntentClassifier>(MockBehavior.Strict);

            Conv.Setup(c => c.GetActiveThreadAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(activeThread);

            Persist.Setup(p => p.SaveConfabulationFlagAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Cmd = new TagCommand(Conv.Object, Persist.Object, Class.Object,
                NullLogger<TagCommand>.Instance);
        }
    }

    // ── Invalidate path: high-confidence invalidate calls the invalid API ─

    [Fact]
    public async Task HighConfidenceInvalidate_MutatesValidity()
    {
        var thread = MakeThread(aniReply: "and we went to Peru together",
                                markMessage: "how was your weekend");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            "broken output", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(TagIntent.Invalidate, 0.92f, "confab"));

        rig.Persist.Setup(p => p.MarkConversationTurnInvalidAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await rig.Cmd.ExecuteAsync("tag broken output", CancellationToken.None);

        result.Should().Contain("Substrate-invalidated 1 record");
        rig.Persist.Verify(p => p.MarkConversationTurnInvalidAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        rig.Persist.Verify(p => p.MarkConversationTurnConfirmedAsync(
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Confirm path: high-confidence confirm calls the confirmed API ─────

    [Fact]
    public async Task HighConfidenceConfirm_SetsConfirmedAt()
    {
        var thread = MakeThread(aniReply: "you were headed to the gym earlier",
                                markMessage: "yeah I just got back");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            "yes", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(TagIntent.Confirm, 0.95f, "endorsement"));

        rig.Persist.Setup(p => p.MarkConversationTurnConfirmedAsync(
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await rig.Cmd.ExecuteAsync("tag yes", CancellationToken.None);

        result.Should().Contain("Substrate-confirmed 1 record");
        rig.Persist.Verify(p => p.MarkConversationTurnConfirmedAsync(
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        rig.Persist.Verify(p => p.MarkConversationTurnInvalidAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Confidence floor: low confidence skips substrate mutation ─────────

    [Fact]
    public async Task LowConfidenceInvalidate_DoesNotMutate()
    {
        var thread = MakeThread("some reply", "some message");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(TagIntent.Invalidate, 0.45f, "unclear"));

        var result = await rig.Cmd.ExecuteAsync("tag hmm", CancellationToken.None);

        result.Should().Contain("confidence 0.45");
        result.Should().Contain("no substrate change");
        rig.Persist.Verify(p => p.MarkConversationTurnInvalidAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Persist.Verify(p => p.MarkConversationTurnConfirmedAsync(
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LowConfidenceConfirm_DoesNotMutate()
    {
        var thread = MakeThread("some reply", "some message");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(TagIntent.Confirm, 0.55f, "maybe"));

        var result = await rig.Cmd.ExecuteAsync("tag maybe", CancellationToken.None);

        result.Should().Contain("no substrate change");
        rig.Persist.Verify(p => p.MarkConversationTurnConfirmedAsync(
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Clarify / Unrelated / Unknown: no substrate mutation, no low-conf note ─

    [Theory]
    [InlineData(TagIntent.Clarify)]
    [InlineData(TagIntent.Unrelated)]
    [InlineData(TagIntent.Unknown)]
    public async Task NonVerdictIntents_DoNotMutateAndDoNotWarn(TagIntent intent)
    {
        var thread = MakeThread("some reply", "some message");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(intent, 0.90f, "not a verdict"));

        var result = await rig.Cmd.ExecuteAsync("tag hmm", CancellationToken.None);

        result.Should().NotContain("Substrate-invalidated");
        result.Should().NotContain("Substrate-confirmed");
        result.Should().NotContain("no substrate change");
        rig.Persist.Verify(p => p.MarkConversationTurnInvalidAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Persist.Verify(p => p.MarkConversationTurnConfirmedAsync(
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Persist throws: classifier verdict still applied, but user sees error ─

    [Fact]
    public async Task ConfirmPersistFails_ReturnsErrorSuffix_FlagSavedFirst()
    {
        var thread = MakeThread("reply", "message");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(TagIntent.Confirm, 0.9f, "yes"));

        rig.Persist.Setup(p => p.MarkConversationTurnConfirmedAsync(
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));

        var result = await rig.Cmd.ExecuteAsync("tag yes", CancellationToken.None);

        result.Should().Contain("substrate confirmation failed");
        rig.Persist.Verify(p => p.SaveConfabulationFlagAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
