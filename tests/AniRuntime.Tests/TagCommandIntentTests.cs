using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Admin.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Issue #93 Phase 2.1 (2026-07-09) — spec tests for <see cref="TagCommand"/>
/// under the CLASSIFIER-AS-SIGNAL discipline.
///
/// <para>
/// **Design change from Phase 2 (2026-07-06):** the auto-mutation path
/// (verdict → MarkConversationTurnInvalid/Confirmed) has been retired.
/// Mark's 2026-07-09 21:12 CDT observation after two real-world
/// misclassifications and a substrate-invalidation-on-misread: *"using
/// regex for something like this is hurting us. We can[not] apply an
/// imprecise determination like regex to a deterministic rating."*
/// The classifier stays (as observational signal), but no imprecise LLM
/// verdict now drives a deterministic substrate mutation. Mutations move
/// to explicit surfaces (future UI review, `///invalidate` / `///confirm`
/// admin commands).
/// </para>
///
/// <para>
/// Strict-mock discipline: every collaborator is mocked with
/// <c>MockBehavior.Strict</c>. Any accidental call to a persistence
/// mutation method surfaces as a test failure.
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

            // The observation-save path — every tag saves regardless of
            // classifier verdict. This is the ONLY persistence call now.
            Persist.Setup(p => p.SaveConfabulationFlagAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Cmd = new TagCommand(Conv.Object, Persist.Object, Class.Object,
                NullLogger<TagCommand>.Instance);
        }
    }

    // ── The core discipline: no verdict shape triggers a substrate mutation ─
    //
    // Under the classifier-as-signal design, MarkConversationTurnInvalidAsync
    // and MarkConversationTurnConfirmedAsync must never be called by
    // TagCommand — regardless of what the classifier returns. Strict-mock
    // discipline makes any accidental call a test failure.

    [Theory]
    [InlineData(TagIntent.Invalidate, 0.95f)]  // canonical invalidate — must NOT mutate
    [InlineData(TagIntent.Confirm,    0.95f)]  // canonical confirm — must NOT mutate
    [InlineData(TagIntent.Clarify,    0.90f)]
    [InlineData(TagIntent.Unrelated,  0.90f)]
    [InlineData(TagIntent.Unknown,    0.00f)]
    [InlineData(TagIntent.Invalidate, 0.45f)]  // low-confidence — also must NOT mutate
    [InlineData(TagIntent.Confirm,    0.55f)]
    public async Task ClassifierVerdict_NeverTriggersSubstrateMutation(
        TagIntent intent, float confidence)
    {
        var thread = MakeThread(
            aniReply: "some reply content",
            markMessage: "some message content");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(intent, confidence, "test verdict"));

        var result = await rig.Cmd.ExecuteAsync("tag some note", CancellationToken.None);

        // Result carries the classifier verdict as observational metadata
        result.Should().Contain(intent.ToString().ToLowerInvariant());
        result.Should().Contain("no substrate change");

        // Zero mutation calls — strict-mock guarantees this via
        // MockBehavior.Strict raising on any unmocked invocation.
        rig.Persist.Verify(p => p.SaveConfabulationFlagAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "every tag saves to the observations table");
    }

    // ── Classifier still gets called ──────────────────────────────────────
    //
    // We retired the mutation, not the classification. The verdict is still
    // captured as observational metadata via the log line + return message.

    [Fact]
    public async Task Classifier_IsAlwaysCalled_EvenThoughNoMutationFollows()
    {
        var thread = MakeThread("reply", "message");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            "test note", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(TagIntent.Confirm, 0.95f, "endorsement"));

        await rig.Cmd.ExecuteAsync("tag test note", CancellationToken.None);

        rig.Class.Verify(c => c.ClassifyAsync(
            "test note", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── The observation record is always saved ────────────────────────────

    [Fact]
    public async Task EveryTag_SavesToObservationsTable()
    {
        var thread = MakeThread("reply", "message");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(TagIntent.Clarify, 0.80f, "reflective note"));

        await rig.Cmd.ExecuteAsync("tag hmm interesting", CancellationToken.None);

        rig.Persist.Verify(p => p.SaveConfabulationFlagAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            "hmm interesting",              // topicCategory = Mark's raw note
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Result carries observational metadata for downstream review ──────

    [Fact]
    public async Task Result_IncludesClassifierVerdictAndConfidence()
    {
        var thread = MakeThread("some reply", "some message");
        var rig = new Rig(thread);

        rig.Class.Setup(c => c.ClassifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIntentVerdict(TagIntent.Invalidate, 0.85f, "fabricated"));

        var result = await rig.Cmd.ExecuteAsync("tag confab", CancellationToken.None);

        result.Should().Contain("invalidate");
        result.Should().Contain("0.85");
        result.Should().Contain("no substrate change");
    }
}
