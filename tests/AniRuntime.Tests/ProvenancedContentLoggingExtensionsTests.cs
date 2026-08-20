using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 9 (2026-08-20) — verifies the
/// <see cref="ProvenancedContentLoggingExtensions.LogProvenance{T}"/>
/// extension emits the canonical structured-log shape and handles the
/// null-envelope no-op contract. This is the first F-1 consumer:
/// producer-side telemetry that makes envelope provenance observable in
/// journal logs without changing any downstream signature.
/// </summary>
public class ProvenancedContentLoggingExtensionsTests
{
    private static IProvenancedContent<string> BuildStringEnvelope() =>
        new WorldSeedEnvelope
        {
            Text   = "morning light",
            Source = WorldSeedSource.WorldSeed,
        };

    [Fact]
    public void LogProvenance_EmitsStructuredF1ProvenanceLine()
    {
        // Verify the structured-log call fires at LogLevel.Debug with the
        // canonical message template ("F1_PROVENANCE source={SourceType}
        // producer={Producer} createdAt={CreatedAt:O}"). Structured-log
        // fields must use the exact IProvenancedContent<T> property names
        // so grep/dashboards remain consistent.
        var mockLog = new Mock<ILogger>(MockBehavior.Loose);
        var env = BuildStringEnvelope();

        env.LogProvenance(mockLog.Object);

        mockLog.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("F1_PROVENANCE")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "LogProvenance must emit exactly one F1_PROVENANCE debug-level log per call");
    }

    [Fact]
    public void LogProvenance_NullEnvelope_IsNoOp()
    {
        // Opportunistic wiring safety: callers should be able to invoke
        // LogProvenance without a null-guard even at sites where the
        // envelope reference may be null (e.g., conditional producer paths).
        var mockLog = new Mock<ILogger>(MockBehavior.Strict);
        IProvenancedContent<string>? nullEnv = null;

        // Strict mock — any log call would throw MockException. If this
        // test passes, LogProvenance correctly skipped the log call.
        var act = () => nullEnv.LogProvenance(mockLog.Object);
        act.Should().NotThrow("null-envelope invocation must be a silent no-op");
    }

    [Fact]
    public void LogProvenance_WorksAcrossAllPhase8EnvelopeShapes()
    {
        // Sanity check the extension resolves for every Phase 8 envelope
        // shape — different wrapped payload types (string, record, class)
        // must all bind through the IProvenancedContent<T> interface without
        // per-shape overloads or generic-inference failures.
        var mockLog = new Mock<ILogger>(MockBehavior.Loose);

        new WorldSeedEnvelope
        {
            Text = "s", Source = WorldSeedSource.WorldSeed,
        }.LogProvenance(mockLog.Object);

        new OutreachFrameEnvelope
        {
            Frame = new OutreachFrame(OutreachFrameType.Shared, "a", 0.5f),
        }.LogProvenance(mockLog.Object);

        new ClosedConversationEnvelope
        {
            Record = new ClosedConversationRecord { Gist = "g" },
        }.LogProvenance(mockLog.Object);

        new OutreachDecisionEnvelope
        {
            Decision = new OutreachDecision { ShouldReach = true },
            Source   = OutreachDecisionSource.LlmParsed,
        }.LogProvenance(mockLog.Object);

        new RecentOutreachContextEnvelope
        {
            Context = new RecentOutreachContext(),
        }.LogProvenance(mockLog.Object);

        new EmotionalContextEnvelope
        {
            Result = new EmotionalContextResult(null, null, null, Array.Empty<string>()),
        }.LogProvenance(mockLog.Object);

        // Six Phase 8 envelopes × one call each = 6 log calls.
        mockLog.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("F1_PROVENANCE")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(6),
            "each of the 6 Phase 8 envelope shapes must resolve LogProvenance through IProvenancedContent<T>");
    }
}
