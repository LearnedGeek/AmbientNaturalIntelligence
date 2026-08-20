using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Coreference;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme N Phase N.2 (May 8, 2026) — spec tests for the deterministic-ranking
/// implementation of <see cref="IOutreachFrameSelector"/>. The N.1 contract
/// invariant tests are preserved; the N.1 "always None" tripwire was deleted
/// when N.2 landed scoring (its purpose was to force this transition).
///
/// **N.2 coverage:**
/// - Each of the four frame types selectable when its tier's substrate is
///   the only substrate present.
/// - Substrate-thin path: empty snapshot → <see cref="OutreachFrame.None"/>.
/// - Tie-break: equal-score candidates resolve via the
///   <c>SHARED &gt; ANI_DOMAIN &gt; ANI_INTERIOR &gt; WORLD_PERCEPTION</c>
///   priority order (project preference for grounded shared content).
/// - Telemetry: <c>N4_FRAME_SELECTED</c> + <c>N4_FRAME_SUPPRESSED</c> log
///   lines emitted on the corresponding paths.
/// </summary>
public class OutreachFrameSelectorTests
{
    private static readonly DateTimeOffset NOW =
        new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static IOutreachFrameSelector Selector(ILogger<OutreachFrameSelector>? log = null) =>
        new OutreachFrameSelector(log ?? NullLogger<OutreachFrameSelector>.Instance);

    private static ContextSnapshot EmptySnapshot() => new()
    {
        BuiltAt        = NOW,
        CharacterState = new CharacterStateDoc { PrimaryContactName = "Mark" },
    };

    private static MemoryRecord Memory(
        EpistemicTier   provenance,
        string          content,
        string?         sourceName = null,
        DateTimeOffset? occurredAt = null,
        float           importance = 0.5f) =>
        new()
        {
            Provenance = provenance,
            Content    = content,
            SourceName = sourceName,
            OccurredAt = occurredAt ?? NOW,
            Importance = importance,
        };

    private sealed class CapturingLogger : ILogger<OutreachFrameSelector>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(
            LogLevel level, EventId id, TState state,
            Exception? ex, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, ex));
    }

    // ─── Contract invariants (preserved from N.1) ────────────────────────

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
        result.Confidence.Should().BeInRange(0f, 1f);
        result.Anchor.Should().NotBeNull();
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
        snapshot.RelevantMemory.Add(Memory(
            provenance: EpistemicTier.Facts,
            content:    "Mark said: I'm tired today",
            occurredAt: NOW.AddHours(-2)));

        var result1 = await selector.SelectFrameAsync(snapshot, CancellationToken.None);
        var result2 = await selector.SelectFrameAsync(snapshot, CancellationToken.None);

        // F-1 Phase 8b (2026-08-19): SelectFrameAsync now returns
        // IOutreachFrameEnvelope. The envelope wrapper is a class (deliberate,
        // to protect the wrapped record's equality — Phase 4 sibling-impl
        // discipline). Compare the wrapped record via .Content, which IS
        // value-equal (record semantics), to verify determinism.
        result1.Content.Should().Be(result2.Content,
            "the selector must be deterministic — no LLM calls, no randomness");
    }

    // ─── OutreachFrame.None canonical helper ─────────────────────────────

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
        var a = OutreachFrame.None;
        var b = OutreachFrame.None;
        ReferenceEquals(a, b).Should().BeTrue();
    }

    // ─── Substrate-thin path ─────────────────────────────────────────────

    [Fact]
    public async Task SelectFrame_EmptySnapshot_ReturnsNone()
    {
        var selector = Selector();
        var result   = await selector.SelectFrameAsync(EmptySnapshot(), CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.None);
        result.Anchor.Should().BeEmpty();
        result.Confidence.Should().Be(0f);
    }

    [Fact]
    public async Task SelectFrame_BelowMinAcceptableScore_ReturnsNone()
    {
        // A 7-day-old SHARED candidate at the very edge of the lookback
        // window is recency≈0 → score≈0 → suppressed.
        var snapshot = EmptySnapshot();
        snapshot.RelevantMemory.Add(Memory(
            provenance: EpistemicTier.Facts,
            content:    "Mark said: long ago",
            occurredAt: NOW.AddDays(-7).AddMinutes(-1),
            importance: 0.9f));

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);
        result.FrameType.Should().Be(OutreachFrameType.None);
    }

    // ─── Per-frame selection ─────────────────────────────────────────────

    [Fact]
    public async Task SelectFrame_OnlySharedSubstrate_PicksShared()
    {
        var snapshot = EmptySnapshot();
        snapshot.RelevantMemory.Add(Memory(
            provenance: EpistemicTier.Facts,
            content:    "Mark said: I'm starting that new role next week",
            occurredAt: NOW.AddHours(-3),
            importance: 0.7f));

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.Shared);
        result.Anchor.Should().StartWith("Mark said:");
        result.Confidence.Should().BeGreaterThan(0f);
    }

    [Fact]
    public async Task SelectFrame_SharedFromClosedConversationGist_PicksShared()
    {
        var snapshot = EmptySnapshot();
        snapshot.RecentClosedConversation = new ClosedConversationRecord
        {
            ClosedAt = NOW.AddHours(-1),
            Gist     = "Mark and Ani talked about the kitchen lights and how late it was getting.",
        };

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.Shared);
        result.Anchor.Should().Contain("kitchen lights");
    }

    [Fact]
    public async Task SelectFrame_OnlyAniDomainSubstrate_PicksAniDomain()
    {
        var snapshot = EmptySnapshot();
        snapshot.RelevantMemory.Add(Memory(
            provenance: EpistemicTier.Facts,
            content:    "Ani works at a bookstore in a small Wisconsin town shelving romance novels.",
            sourceName: "character-seed",
            importance: 0.8f));

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.AniDomain);
        result.Anchor.Should().Contain("bookstore");
        result.Confidence.Should().BeGreaterThan(0f);
    }

    [Fact]
    public async Task SelectFrame_OnlyAniInteriorSubstrate_PicksAniInterior()
    {
        var snapshot = EmptySnapshot();
        snapshot.SimilarRecentThoughts.Add(Memory(
            provenance: EpistemicTier.Interior,
            content:    "i was thinking about how the rain sounds different at midnight",
            occurredAt: NOW.AddMinutes(-30),
            importance: 0.6f));

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.AniInterior);
        result.Anchor.Should().Contain("midnight");
        result.Confidence.Should().BeGreaterThan(0f);
    }

    [Fact]
    public async Task SelectFrame_OnlyAniInteriorFromRelevantMemory_PicksAniInterior()
    {
        var snapshot = EmptySnapshot();
        snapshot.RelevantMemory.Add(Memory(
            provenance: EpistemicTier.Interior,
            content:    "i imagined sarah sneaking a romance paperback at the counter today",
            occurredAt: NOW.AddMinutes(-45),
            importance: 0.5f));

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.AniInterior);
    }

    [Fact]
    public async Task SelectFrame_OnlyWorldPerceptionFromPerceptions_PicksWorldPerception()
    {
        var snapshot = EmptySnapshot();
        snapshot.Perceptions.Add(new PerceptionEvent
        {
            SourceName       = "rss",
            Category         = PerceptionCategory.Content,
            Summary          = "Snow expected overnight in Madison.",
            ContactRelevance = 0.7f,
            OccurredAt       = NOW.AddMinutes(-30),
        });

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.WorldPerception);
        result.Anchor.Should().Contain("Snow");
    }

    [Fact]
    public async Task SelectFrame_OnlyWorldPerceptionFromRelevantMemory_PicksWorldPerception()
    {
        var snapshot = EmptySnapshot();
        snapshot.RelevantMemory.Add(Memory(
            provenance: EpistemicTier.Facts,
            content:    "Article: The hidden lives of bookstore clerks",
            sourceName: "rss",
            occurredAt: NOW.AddMinutes(-20),
            importance: 0.6f));

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.WorldPerception);
    }

    // ─── Tie-break ───────────────────────────────────────────────────────

    [Fact]
    public async Task SelectFrame_ExactlyEqualScores_PrefersHigherFramePriority()
    {
        // Construct two candidates whose scores are bit-identical so the
        // tie-break path is exercised: SHARED Mark-asserted at 50% recency
        // and Interior at 50% recency, equal salience. Both = 0.5 × 0.5 × 1.0 = 0.25.
        var snapshot = EmptySnapshot();
        snapshot.RelevantMemory.Add(Memory(
            provenance: EpistemicTier.Facts,
            content:    "Mark said: kept it simple today",
            occurredAt: NOW - TimeSpan.FromTicks(TimeSpan.FromDays(7).Ticks / 2),
            importance: 0.5f));
        snapshot.SimilarRecentThoughts.Add(Memory(
            provenance: EpistemicTier.Interior,
            content:    "thinking about how simple feels these days",
            occurredAt: NOW - TimeSpan.FromTicks(TimeSpan.FromHours(24).Ticks / 2),
            importance: 0.5f));

        var result = await Selector().SelectFrameAsync(snapshot, CancellationToken.None);

        result.FrameType.Should().Be(OutreachFrameType.Shared,
            "tie-break order is SHARED > ANI_DOMAIN > ANI_INTERIOR > WORLD_PERCEPTION");
    }

    // ─── Telemetry ───────────────────────────────────────────────────────

    [Fact]
    public async Task SelectFrame_OnPick_EmitsN4FrameSelected()
    {
        var log      = new CapturingLogger();
        var selector = Selector(log);

        var snapshot = EmptySnapshot();
        snapshot.RelevantMemory.Add(Memory(
            provenance: EpistemicTier.Facts,
            content:    "Mark said: I'm starting that new role next week",
            occurredAt: NOW.AddHours(-3),
            importance: 0.7f));

        await selector.SelectFrameAsync(snapshot, CancellationToken.None);

        log.Messages.Should().ContainSingle(m => m.Contains("N4_FRAME_SELECTED"));
        var line = log.Messages.Single(m => m.Contains("N4_FRAME_SELECTED"));
        line.Should().Contain("frame=Shared");
        line.Should().Contain("confidence=");
        line.Should().Contain("score=");
        line.Should().Contain("anchor_preview=");
    }

    [Fact]
    public async Task SelectFrame_OnEmptySnapshot_EmitsN4FrameSuppressed()
    {
        var log      = new CapturingLogger();
        var selector = Selector(log);

        await selector.SelectFrameAsync(EmptySnapshot(), CancellationToken.None);

        log.Messages.Should().ContainSingle(m => m.Contains("N4_FRAME_SUPPRESSED"));
        var line = log.Messages.Single(m => m.Contains("N4_FRAME_SUPPRESSED"));
        line.Should().Contain("frame=None");
        line.Should().Contain("substrate-thin");
        line.Should().Contain("top_score=");
    }
}
