using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Foundation Input (F-1) Phase 8f (2026-08-20) — verifies
/// <see cref="EmotionalContextEnvelope"/> implements
/// <see cref="IEmotionalContextEnvelope"/> correctly, exposes the wrapped
/// <see cref="EmotionalContextResult"/> record via
/// <see cref="IProvenancedContent{T}.Content"/>, and produces the canonical
/// single-value SourceType tag (<c>emotional-context.per-cycle</c>)
/// matching the sibling envelope kebab-case convention.
/// </summary>
public class EmotionalContextEnvelopeTests
{
    private static EmotionalContextResult SampleResult() => new(
        RelationshipHealth: null,
        EmotionalDrift:     null,
        PatternAwareness:   null,
        ProcessedThemes:    Array.Empty<string>());

    [Fact]
    public void Envelope_WrapsResult_ExposesContentViaInterface()
    {
        var result = SampleResult();
        IProvenancedContent<EmotionalContextResult> env = new EmotionalContextEnvelope { Result = result };

        env.Content.Should().BeSameAs(result);
    }

    [Fact]
    public void SourceType_IsCanonicalPerCycleTag()
    {
        // Single-producer surface: hardcoded SourceType, no source enum.
        // Kebab-case per sibling-envelope convention (frame.ani-interior,
        // world-seed.circadian, closed-conversation.valid,
        // recent-outreach-context.recent-episodic).
        IProvenancedContent<EmotionalContextResult> env = new EmotionalContextEnvelope
        {
            Result = SampleResult(),
        };
        env.SourceType.Should().Be("emotional-context.per-cycle");
    }

    [Fact]
    public void Producer_Identifies_EmotionalContextBuilder()
    {
        IProvenancedContent<EmotionalContextResult> env = new EmotionalContextEnvelope
        {
            Result = SampleResult(),
        };
        env.Producer.Should().Be("EmotionalContextBuilder");
    }

    [Fact]
    public async Task CreatedAt_IsStableAcrossReads()
    {
        // Sibling-impl discipline (PR #112): captured once at construction.
        IProvenancedContent<EmotionalContextResult> env = new EmotionalContextEnvelope
        {
            Result = SampleResult(),
        };

        var first = env.CreatedAt;
        await Task.Delay(20);
        var second = env.CreatedAt;

        second.Should().Be(first,
            "CreatedAt must be captured once at construction, not recomputed on each read");
    }

    [Fact]
    public void SemanticKey_IsNull()
    {
        // EmotionalContextResult has no embedding surface.
        IProvenancedContent<EmotionalContextResult> env = new EmotionalContextEnvelope
        {
            Result = SampleResult(),
        };
        env.SemanticKey.Should().BeNull();
    }

    // Sibling-impl class-wrap discipline (Phase 4 lesson): EmotionalContextResult
    // is a sealed record. Wrapping in the envelope class means:
    //   1. Two envelopes have reference equality (they're classes with
    //      per-instance CreatedAt).
    //   2. The wrapped record's OWN equality behavior is unaffected by the wrap.
    //
    // Note on the record's equality shape: EmotionalContextResult includes
    // `IReadOnlyList<string> ProcessedThemes` — C# record equality does NOT
    // deep-compare collections, so two records with .SequenceEqual themes but
    // different list references will NOT compare equal. That's a property of
    // the underlying record type, not affected by the envelope. This test
    // pins the property we DO want: that wrapping the same record instance
    // in two envelopes gives .Content references that compare equal (same
    // object identity), while the envelope-level equality is per-instance.
    [Fact]
    public void EnvelopePerInstance_WrappedRecordSameObjectAcrossEnvelopes()
    {
        var result = SampleResult();

        var envA = new EmotionalContextEnvelope { Result = result };
        var envB = new EmotionalContextEnvelope { Result = result };

        // Envelopes: reference equality (class). Different instances → NOT equal.
        envA.Should().NotBeSameAs(envB);

        // Wrapped record: same object identity → compares equal trivially and
        // survives the envelope wrap unchanged.
        envA.Result.Should().BeSameAs(envB.Result);
        envA.Result.Should().Be(envB.Result);
    }
}
