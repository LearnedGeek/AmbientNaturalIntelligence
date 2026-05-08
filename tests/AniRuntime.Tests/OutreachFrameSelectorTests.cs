using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Coreference;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme N Phase N.1 (May 8, 2026) — contract spec tests for the
/// <see cref="IOutreachFrameSelector"/> skeleton. These pin the
/// architectural properties of the selector that must hold across all
/// future implementations (N.2 deterministic ranking, possibly N+ LM-Kit
/// coref-based selection).
///
/// **Pinned invariants:**
/// - Selector returns deterministically (same snapshot → same result).
/// - Null snapshot throws ArgumentNullException.
/// - Cancellation is honored.
/// - The `None` fallback is the safe default when substrate is thin.
/// - Returned <see cref="OutreachFrame"/> is well-formed (frame type +
///   anchor + confidence consistent).
///
/// **N.1 specific:** the skeleton always returns <see cref="OutreachFrame.None"/>.
/// When N.2 ships scoring, the "always None" tests below will be deleted
/// and replaced with frame-specific selection tests; the rest of the
/// invariant tests stay.
/// </summary>
public class OutreachFrameSelectorTests
{
    private static IOutreachFrameSelector Selector() =>
        new OutreachFrameSelector(NullLogger<OutreachFrameSelector>.Instance);

    private static ContextSnapshot EmptySnapshot() => new()
    {
        CharacterState = new CharacterStateDoc { PrimaryContactName = "Mark" },
    };

    // ── Contract invariants (must hold across all future impls) ──────────

    [Fact]
    public async Task SelectFrame_NullSnapshot_Throws()
    {
        var selector = Selector();
        var act = () => selector.SelectFrameAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SelectFrame_CancellationRequested_Throws()
    {
        var selector = Selector();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => selector.SelectFrameAsync(EmptySnapshot(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SelectFrame_ReturnsWellFormedFrame()
    {
        var selector = Selector();
        var result = await selector.SelectFrameAsync(EmptySnapshot(), CancellationToken.None);

        result.Should().NotBeNull();
        // Confidence in valid range
        result.Confidence.Should().BeInRange(0f, 1f);
        // Anchor non-null (empty string allowed for None frame)
        result.Anchor.Should().NotBeNull();
        // None ↔ empty anchor + zero confidence consistency
        if (result.FrameType == OutreachFrameType.None)
        {
            result.Anchor.Should().BeEmpty();
            result.Confidence.Should().Be(0f);
        }
    }

    [Fact]
    public async Task SelectFrame_Deterministic_SameSnapshotSameResult()
    {
        var selector = Selector();
        var snapshot = EmptySnapshot();

        var result1 = await selector.SelectFrameAsync(snapshot, CancellationToken.None);
        var result2 = await selector.SelectFrameAsync(snapshot, CancellationToken.None);

        result1.Should().Be(result2,
            "the selector must be deterministic — no LLM calls, no randomness");
    }

    // ── OutreachFrame.None canonical helper ──────────────────────────────

    [Fact]
    public void OutreachFrame_None_IsCanonicalSuppressFallback()
    {
        OutreachFrame.None.FrameType.Should().Be(OutreachFrameType.None);
        OutreachFrame.None.Anchor.Should().BeEmpty();
        OutreachFrame.None.Confidence.Should().Be(0f);
    }

    [Fact]
    public void OutreachFrame_NoneSingleton_ReferenceEquality()
    {
        // Same instance referenced across calls — small allocation hygiene
        // and lets producers compare via ReferenceEquals if they want.
        var a = OutreachFrame.None;
        var b = OutreachFrame.None;
        ReferenceEquals(a, b).Should().BeTrue();
    }

    // ── N.1 skeleton-specific (delete when N.2 ships scoring) ────────────

    [Fact]
    public async Task N1Skeleton_AlwaysReturnsNone_PendingN2Scoring()
    {
        // EXPECTED TO FAIL once N.2 ships frame scoring. This test exists
        // to pin the current N.1 deliverable boundary — selector is
        // reachable + safe-fallback is firing — and to make the N.1 →
        // N.2 transition explicit by failing at that point.
        var selector = Selector();
        var snapshot = EmptySnapshot();

        var result = await selector.SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.None,
            "N.1 ships skeleton only; scoring lands in N.2");
    }
}
