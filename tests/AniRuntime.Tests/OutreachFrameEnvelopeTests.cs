using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 8b (2026-08-19) — verifies
/// <see cref="OutreachFrameEnvelope"/> implements
/// <see cref="IOutreachFrameEnvelope"/> correctly, exposes the wrapped
/// <see cref="OutreachFrame"/> record via passthroughs, and produces
/// per-frame-type SourceType tags that let downstream audit / telemetry
/// distinguish `frame.shared` from `frame.ani-interior` etc.
/// </summary>
public class OutreachFrameEnvelopeTests
{
    [Fact]
    public void Envelope_WrapsRecord_ExposesPassthroughs()
    {
        var frame = new OutreachFrame(OutreachFrameType.Shared, "Mark said: teaching was long today", 0.85f);
        var env   = new OutreachFrameEnvelope { Frame = frame };

        env.FrameType.Should().Be(OutreachFrameType.Shared);
        env.Anchor.Should().Be("Mark said: teaching was long today");
        env.Confidence.Should().Be(0.85f);
        env.Frame.Should().BeSameAs(frame);
    }

    [Fact]
    public void Envelope_WrappingNoneFrame_ExposesNonePassthroughs()
    {
        // PR #119 review-fix (Devin): the pre-fix static `Envelope.None`
        // was removed to avoid a process-wide-shared CreatedAt on the
        // suppression path. Construction is per-call now
        // (LogAndReturnSuppressed in the selector); this test verifies
        // that wrapping OutreachFrame.None still surfaces the expected
        // suppression passthroughs.
        var env = new OutreachFrameEnvelope { Frame = OutreachFrame.None };

        env.Frame.Should().BeSameAs(OutreachFrame.None);
        env.FrameType.Should().Be(OutreachFrameType.None);
        env.Anchor.Should().BeEmpty();
        env.Confidence.Should().Be(0f);
    }

    // PR #119 review-fix (Devin BUG): SourceType tag now uses kebab-case
    // matching the sibling envelope convention (thought.third-person-frame,
    // world-seed.associative-anchor). Pre-fix `.ToString().ToLowerInvariant()`
    // produced `frame.aniinterior` — inconsistent with dashboards/greps.
    [Theory]
    [InlineData(OutreachFrameType.None,            "frame.none")]
    [InlineData(OutreachFrameType.Shared,          "frame.shared")]
    [InlineData(OutreachFrameType.AniDomain,       "frame.ani-domain")]
    [InlineData(OutreachFrameType.AniInterior,     "frame.ani-interior")]
    [InlineData(OutreachFrameType.WorldPerception, "frame.world-perception")]
    public void SourceType_ComposesFromFrameType_UsingKebabCaseConvention(OutreachFrameType type, string expected)
    {
        IProvenancedContent<OutreachFrame> env = new OutreachFrameEnvelope
        {
            Frame = new OutreachFrame(type, "anchor", 0.5f),
        };
        env.SourceType.Should().Be(expected);
    }

    [Fact]
    public void Producer_Identifies_OutreachFrameSelector()
    {
        IProvenancedContent<OutreachFrame> env = new OutreachFrameEnvelope
        {
            Frame = OutreachFrame.None,
        };
        env.Producer.Should().Be("OutreachFrameSelector");
    }

    [Fact]
    public async Task CreatedAt_IsStableAcrossReads()
    {
        // Sibling-impl discipline (PR #112): captured once at construction.
        IProvenancedContent<OutreachFrame> env = new OutreachFrameEnvelope
        {
            Frame = OutreachFrame.None,
        };

        var first = env.CreatedAt;
        await Task.Delay(20);
        var second = env.CreatedAt;

        second.Should().Be(first, "CreatedAt must be captured once at construction, not recomputed on each read");
    }

    [Fact]
    public void SemanticKey_IsNull()
    {
        IProvenancedContent<OutreachFrame> env = new OutreachFrameEnvelope
        {
            Frame = OutreachFrame.None,
        };
        env.SemanticKey.Should().BeNull();
    }

    // PR discipline: OutreachFrame record retains value equality (Phase 4
    // lesson — records with stored CreatedAt break equality; wrapping in a
    // class envelope keeps the record's equality intact).
    [Fact]
    public void WrappedRecord_RetainsValueEquality_UnaffectedByEnvelope()
    {
        var frameA = new OutreachFrame(OutreachFrameType.Shared, "same anchor", 0.5f);
        var frameB = new OutreachFrame(OutreachFrameType.Shared, "same anchor", 0.5f);

        frameA.Should().Be(frameB, "record value equality preserved — envelope is a separate class wrapper");

        var envA = new OutreachFrameEnvelope { Frame = frameA };
        var envB = new OutreachFrameEnvelope { Frame = frameB };

        // Envelopes are classes → reference equality by default. Consumers
        // that want value equality compare the wrapped .Frame.
        envA.Frame.Should().Be(envB.Frame);
    }
}
