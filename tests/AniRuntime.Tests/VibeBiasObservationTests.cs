using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.LLM;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Vibe Loop V1.5a spec tests for the observational telemetry helper.
/// Pins the contract of <see cref="VibeBiasObservation.ObserveAsync"/>:
///
/// 1. **Calls bias service** when a recent closed conversation provides
///    a Mark register vector.
/// 2. **Short-circuits silently** (no service call) when there's no
///    recent closed conversation OR when the register is empty.
/// 3. **Never throws** — observational instrumentation must not affect
///    dispatch semantics. Failures get logged and swallowed.
/// 4. **Null bias service** — graceful no-op.
///
/// Strict-mock discipline: <see cref="IVibeBiasService.ComputeBiasAsync"/>
/// is set up explicitly when expected to be called, and Verify-Never
/// when expected NOT to be called. Loose mocks would let
/// short-circuit failures default to a silent service-call when the
/// helper should have skipped — masking the contract.
/// </summary>
public class VibeBiasObservationTests
{
    private readonly Mock<IVibeBiasService> _mockBias = new(MockBehavior.Strict);

    private static ContextSnapshot SnapshotWithClosedConversation(
        Dictionary<string, float>? markRegister = null) => new()
    {
        CharacterState = new CharacterStateDoc { PrimaryContactName = "Mark" },
        RecentClosedConversation = new ClosedConversationRecord
        {
            Id           = Guid.NewGuid(),
            ClosedAt     = DateTimeOffset.UtcNow.AddHours(-1),
            MarkRegister = markRegister ?? new Dictionary<string, float>
            {
                { "Tenderness", 0.7f },
                { "Curiosity",  0.3f },
            },
            TurnCount             = 5,
            OutcomeSignalValence  = 0.4f,
        },
    };

    private static VibeBiasResult EmptyResult() => new(
        AllCandidates:               Array.Empty<VibeBiasContribution>(),
        SurfacedTopN:                Array.Empty<VibeBiasContribution>(),
        RecommendedStrategyRegister: new float[ClosedConversationSummarizer.Registers.Count],
        DiversityScoreReason:        "test");

    [Fact]
    public async Task ObserveAsync_NullBiasService_ShortCircuits()
    {
        var snapshot = SnapshotWithClosedConversation();

        await VibeBiasObservation.ObserveAsync(
            biasService: null,
            snapshot:    snapshot,
            callSite:    "outreach",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);

        // No assertion needed — successfully reaching this line means no exception.
    }

    [Fact]
    public async Task ObserveAsync_NoRecentClosedConversation_DoesNotCallService()
    {
        var snapshot = new ContextSnapshot
        {
            CharacterState           = new CharacterStateDoc { PrimaryContactName = "Mark" },
            RecentClosedConversation = null,
        };

        await VibeBiasObservation.ObserveAsync(
            biasService: _mockBias.Object,
            snapshot:    snapshot,
            callSite:    "outreach",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);

        _mockBias.Verify(b => b.ComputeBiasAsync(
            It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no recent closed conversation means no current-state vector — observation cannot run");
    }

    [Fact]
    public async Task ObserveAsync_EmptyMarkRegister_DoesNotCallService()
    {
        var snapshot = SnapshotWithClosedConversation(
            markRegister: new Dictionary<string, float>());

        await VibeBiasObservation.ObserveAsync(
            biasService: _mockBias.Object,
            snapshot:    snapshot,
            callSite:    "outreach",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);

        _mockBias.Verify(b => b.ComputeBiasAsync(
            It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "empty register dict produces a zero vector with no signal — observation is meaningless");
    }

    [Fact]
    public async Task ObserveAsync_HasClosedConversation_CallsService()
    {
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyResult());

        var snapshot = SnapshotWithClosedConversation();

        await VibeBiasObservation.ObserveAsync(
            biasService: _mockBias.Object,
            snapshot:    snapshot,
            callSite:    "outreach",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);

        _mockBias.Verify(b => b.ComputeBiasAsync(
            "Mark",
            It.Is<MarkRegisterContext>(ctx =>
                ctx.MarkRegister.Length == ClosedConversationSummarizer.Registers.Count),
            It.IsAny<CancellationToken>()),
            Times.Once,
            "recent closed conversation present → service is called with a 9-dim ordered Mark register vector");
    }

    [Fact]
    public async Task ObserveAsync_PassesContactName_ToService()
    {
        string?              capturedContactName = null;
        MarkRegisterContext? capturedContext     = null;
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .Callback<string, MarkRegisterContext, CancellationToken>(
                (name, ctx, _) => { capturedContactName = name; capturedContext = ctx; })
            .ReturnsAsync(EmptyResult());

        var snapshot = SnapshotWithClosedConversation();
        snapshot.CharacterState.PrimaryContactName = "Mark";

        await VibeBiasObservation.ObserveAsync(
            biasService: _mockBias.Object,
            snapshot:    snapshot,
            callSite:    "reply",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);

        capturedContactName.Should().Be("Mark");
        capturedContext.Should().NotBeNull();
        capturedContext!.MarkRegister.Should().HaveCount(ClosedConversationSummarizer.Registers.Count);
    }

    [Fact]
    public async Task ObserveAsync_FallbackContactName_WhenSnapshotPrimaryContactIsEmpty()
    {
        string? capturedContactName = null;
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .Callback<string, MarkRegisterContext, CancellationToken>(
                (name, _, _) => capturedContactName = name)
            .ReturnsAsync(EmptyResult());

        var snapshot = SnapshotWithClosedConversation();
        snapshot.CharacterState.PrimaryContactName = string.Empty;   // edge case

        await VibeBiasObservation.ObserveAsync(
            biasService: _mockBias.Object,
            snapshot:    snapshot,
            callSite:    "outreach",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);

        capturedContactName.Should().Be("Mark",
            "when no primary contact name is set, default to 'Mark' rather than throwing — V1 only has one contact anyway");
    }

    [Fact]
    public async Task ObserveAsync_BiasServiceThrows_DoesNotPropagate()
    {
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated failure"));

        var snapshot = SnapshotWithClosedConversation();

        // Must NOT throw — observational instrumentation MUST NOT affect dispatch.
        var act = async () => await VibeBiasObservation.ObserveAsync(
            biasService: _mockBias.Object,
            snapshot:    snapshot,
            callSite:    "outreach",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);

        await act.Should().NotThrowAsync(
            "observational telemetry failures must not propagate; dispatch must continue");
    }

    [Fact]
    public async Task ObserveAsync_ContextAsOf_IsUtcNow()
    {
        DateTimeOffset capturedAsOf = default;
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .Callback<string, MarkRegisterContext, CancellationToken>(
                (_, ctx, _) => capturedAsOf = ctx.AsOf)
            .ReturnsAsync(EmptyResult());

        var beforeCall = DateTimeOffset.UtcNow;
        await VibeBiasObservation.ObserveAsync(
            biasService: _mockBias.Object,
            snapshot:    SnapshotWithClosedConversation(),
            callSite:    "outreach",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);
        var afterCall = DateTimeOffset.UtcNow;

        capturedAsOf.Should().BeOnOrAfter(beforeCall);
        capturedAsOf.Should().BeOnOrBefore(afterCall);
    }

    [Fact]
    public async Task ObserveAsync_VectorOrderMatchesCanonicalRegisters()
    {
        MarkRegisterContext? capturedContext = null;
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .Callback<string, MarkRegisterContext, CancellationToken>(
                (_, ctx, _) => capturedContext = ctx)
            .ReturnsAsync(EmptyResult());

        var snapshot = SnapshotWithClosedConversation(markRegister: new Dictionary<string, float>
        {
            { "Tenderness", 0.5f },   // index 0 in canonical order
            { "Delight",    0.5f },   // index 8 in canonical order
        });

        await VibeBiasObservation.ObserveAsync(
            biasService: _mockBias.Object,
            snapshot:    snapshot,
            callSite:    "outreach",
            log:         NullLogger.Instance,
            ct:          CancellationToken.None);

        capturedContext!.MarkRegister[0].Should().BeApproximately(0.5f, 0.0001f);
        capturedContext!.MarkRegister[8].Should().BeApproximately(0.5f, 0.0001f);
        for (var i = 1; i < 8; i++)
            capturedContext!.MarkRegister[i].Should().Be(0f,
                "intermediate registers default to 0 since they're absent from the source dict");
    }
}
