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

    /// <summary>
    /// Issue #46 spec: when an <see cref="IVibeBiasObservationStore"/> is
    /// provided, ObserveAsync persists one structured record per
    /// successful pass — carrying call site, contact name, candidate
    /// count, tier breakdown, surfaced-records JSON, recommended register,
    /// recommended value, and diversity score reason.
    /// </summary>
    [Fact]
    public async Task ObserveAsync_WithStore_PersistsStructuredRecord()
    {
        var registers = ClosedConversationSummarizer.Registers;
        var vec       = new float[registers.Count];
        var tendIdx   = -1;
        for (var i = 0; i < registers.Count; i++)
            if (registers[i] == "Tenderness") { tendIdx = i; break; }
        vec[tendIdx]  = 0.81f;

        var result = new VibeBiasResult(
            AllCandidates: new[]
            {
                new VibeBiasContribution(Guid.NewGuid(), 0.45f, 0.6f, "Heavy",  15.1f, new float[registers.Count], +1.0f),
                new VibeBiasContribution(Guid.NewGuid(), 0.30f, 0.5f, "Medium", 30.0f, new float[registers.Count], -0.2f),
                new VibeBiasContribution(Guid.NewGuid(), 0.10f, 0.3f, "Light",  60.0f, new float[registers.Count],  0.0f),
            },
            SurfacedTopN: new[]
            {
                new VibeBiasContribution(Guid.NewGuid(), 0.45f, 0.6f, "Heavy", 15.1f, new float[registers.Count], +1.0f),
            },
            RecommendedStrategyRegister: vec,
            DiversityScoreReason:        "MMR surfaced top 1 (lambda=0.30)");

        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var mockStore = new Mock<IVibeBiasObservationStore>(MockBehavior.Strict);
        VibeBiasObservationRecord? captured = null;
        mockStore.Setup(s => s.SaveAsync(It.IsAny<VibeBiasObservationRecord>(), It.IsAny<CancellationToken>()))
            .Callback<VibeBiasObservationRecord, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);

        var threadId = Guid.NewGuid();
        await VibeBiasObservation.ObserveAsync(
            biasService:        _mockBias.Object,
            snapshot:           SnapshotWithClosedConversation(),
            callSite:           "reply",
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None,
            observationStore:   mockStore.Object,
            threadId:           threadId);

        captured.Should().NotBeNull();
        captured!.CallSite.Should().Be("reply");
        captured.ThreadId.Should().Be(threadId);
        captured.ContactName.Should().Be("Mark");
        captured.CandidateCount.Should().Be(3);
        captured.TierBreakdownLight.Should().Be(1);
        captured.TierBreakdownMedium.Should().Be(1);
        captured.TierBreakdownHeavy.Should().Be(1);
        captured.RecommendedRegister.Should().Be("Tenderness");
        captured.RecommendedValue.Should().BeApproximately(0.81f, 0.0001f);
        captured.DiversityScoreReason.Should().Be("MMR surfaced top 1 (lambda=0.30)");
        captured.SurfacedRecordsJson.Should().Contain("\"weight\":0.45");
        captured.SurfacedRecordsJson.Should().Contain("\"tier\":\"Heavy\"");
    }

    /// <summary>
    /// Issue #46 spec: outreach call site persists with null ThreadId —
    /// outreach is pre-thread, so the schema's nullable thread_id is
    /// exercised explicitly.
    /// </summary>
    [Fact]
    public async Task ObserveAsync_OutreachCallSite_PersistsWithNullThreadId()
    {
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyResult());

        var mockStore = new Mock<IVibeBiasObservationStore>(MockBehavior.Strict);
        VibeBiasObservationRecord? captured = null;
        mockStore.Setup(s => s.SaveAsync(It.IsAny<VibeBiasObservationRecord>(), It.IsAny<CancellationToken>()))
            .Callback<VibeBiasObservationRecord, CancellationToken>((r, _) => captured = r)
            .Returns(Task.CompletedTask);

        await VibeBiasObservation.ObserveAsync(
            biasService:        _mockBias.Object,
            snapshot:           SnapshotWithClosedConversation(),
            callSite:           "outreach",
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None,
            observationStore:   mockStore.Object,
            threadId:           null);

        captured.Should().NotBeNull();
        captured!.CallSite.Should().Be("outreach");
        captured.ThreadId.Should().BeNull();
    }

    /// <summary>
    /// Issue #46 spec: when the structured-record store throws,
    /// the failure is logged and swallowed. Observational invariant —
    /// persistence failure must NOT propagate into dispatch.
    /// </summary>
    [Fact]
    public async Task ObserveAsync_StoreThrows_DoesNotPropagate()
    {
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyResult());

        var mockStore = new Mock<IVibeBiasObservationStore>(MockBehavior.Strict);
        mockStore.Setup(s => s.SaveAsync(It.IsAny<VibeBiasObservationRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sqlite write failed"));

        var act = async () => await VibeBiasObservation.ObserveAsync(
            biasService:        _mockBias.Object,
            snapshot:           SnapshotWithClosedConversation(),
            callSite:           "reply",
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None,
            observationStore:   mockStore.Object,
            threadId:           Guid.NewGuid());

        await act.Should().NotThrowAsync(
            "persistence failure must not propagate — observational instrumentation invariant");
    }

    /// <summary>
    /// Issue #46 spec: when no store is provided (null), the helper still
    /// emits Serilog telemetry as before — the persistence add-on is
    /// opt-in via DI, not required.
    /// </summary>
    [Fact]
    public async Task ObserveAsync_NullStore_StillEmitsTelemetryWithoutThrowing()
    {
        _mockBias.Setup(b => b.ComputeBiasAsync(
                It.IsAny<string>(), It.IsAny<MarkRegisterContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyResult());

        var act = async () => await VibeBiasObservation.ObserveAsync(
            biasService:        _mockBias.Object,
            snapshot:           SnapshotWithClosedConversation(),
            callSite:           "outreach",
            log:                NullLogger.Instance,
            ct:                 CancellationToken.None,
            observationStore:   null,
            threadId:           null);

        await act.Should().NotThrowAsync();
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
