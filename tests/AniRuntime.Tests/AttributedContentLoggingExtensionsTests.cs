using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Attribution (F-2) Phase 1 P8 (2026-08-23) — verifies the
/// <see cref="AttributedContentLoggingExtensions.LogAttribution{T}"/>
/// extension emits the canonical structured-log shape and handles the
/// null-content no-op contract. First read-side consumer of the
/// <see cref="IAttributedContent{T}"/> attribution fields — producer-side
/// telemetry that makes attribution decisions observable in journal logs
/// without changing any downstream signature.
/// </summary>
public class AttributedContentLoggingExtensionsTests
{
    [Fact]
    public void LogAttribution_EmitsStructuredF2AttributionLine()
    {
        // Verify the structured-log call fires at LogLevel.Information
        // (matches F1_PROVENANCE convention — Info tier so grep works
        // against the default journal without a per-category serilog
        // override) with the canonical message template
        // ("F2_ATTRIBUTION attributedTo={AttributedTo} trust={Trust} …").
        var mockLog = new Mock<ILogger>(MockBehavior.Loose);
        var record  = new MemoryRecord
        {
            Content                    = "test",
            AttributedTo               = AttributedTo.Ani,
            AttributedAt               = DateTimeOffset.UtcNow,
            AttributionTrust           = "verified",
            AttributedSourceDescriptor = "twilio-inbound:SM123",
        };

        record.LogAttribution(mockLog.Object);

        mockLog.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("F2_ATTRIBUTION")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "LogAttribution must emit exactly one F2_ATTRIBUTION Info-level log per call");
    }

    [Fact]
    public void LogAttribution_NullRecord_IsNoOp()
    {
        // Opportunistic-wiring safety: callers can invoke without a
        // null-guard even at sites where the record may be null.
        var mockLog = new Mock<ILogger>(MockBehavior.Strict);
        MemoryRecord? nullRecord = null;

        // Strict mock — any log call would throw. If this passes,
        // LogAttribution correctly skipped the log call.
        var act = () => nullRecord.LogAttribution(mockLog.Object);
        act.Should().NotThrow("null-record invocation must be a silent no-op");
    }

    [Fact]
    public void LogAttribution_GenericForm_ResolvesForIAttributedContent()
    {
        // Sanity check that the generic form binds when the caller has
        // an IAttributedContent<T> reference (future producers may emit
        // envelope-shaped attributed content beyond MemoryRecord).
        var mockLog = new Mock<ILogger>(MockBehavior.Loose);
        var record  = new MemoryRecord
        {
            Content          = "test",
            AttributedTo     = AttributedTo.Mark,
            AttributionTrust = "verified",
        };
        IAttributedContent<MemoryRecord> viaInterface = record;

        viaInterface.LogAttribution(mockLog.Object);

        mockLog.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("F2_ATTRIBUTION")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
