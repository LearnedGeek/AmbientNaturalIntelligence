using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Outreach;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 2.1 — pins the retrieval + fallback contract of
/// <see cref="AnchorBearingOutreachSeedSelector"/>. Scoring math itself
/// is pinned by <see cref="OutreachSeedScoringTests"/>; this file covers
/// the "pull last N, pick top, fall back to just-generated" surface.
/// </summary>
public class AnchorBearingOutreachSeedSelectorTests
{
    private static CharacterStateDoc State() => new()
    {
        FamilyContext = new List<string> { "Sister Lila" },
        Interests     = new List<string> { "Fleetwood Mac Landslide" },
    };

    private static MemoryRecord Thought(string content, float importance) => new()
    {
        Type       = MemoryType.InnerThought,
        Content    = content,
        Importance = importance,
        OccurredAt = DateTimeOffset.UtcNow,
    };

    private static AnchorBearingOutreachSeedSelector Build(
        IEnumerable<MemoryRecord>? recent = null,
        AniOptions? opts = null,
        bool throwOnSearch = false)
    {
        var mock = new Mock<IMemorySearch>();
        if (throwOnSearch)
        {
            mock.Setup(s => s.GetByTypeAsync(
                    It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("simulated persistence fault"));
        }
        else
        {
            mock.Setup(s => s.GetByTypeAsync(
                    It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(recent ?? Array.Empty<MemoryRecord>());
        }

        return new AnchorBearingOutreachSeedSelector(
            mock.Object,
            Options.Create(opts ?? new AniOptions()),
            NullLogger<AnchorBearingOutreachSeedSelector>.Instance);
    }

    [Fact]
    public async Task Select_FlagOff_ReturnsJustGeneratedUnchanged()
    {
        var selector = Build(
            recent: new[] { Thought("thinking about Lila", 0.9f) },
            opts: new AniOptions { OutreachSeedAnchorBearingSelectionEnabled = false });

        var result = await selector.SelectAsync(
            "sitting on the couch", State(), CancellationToken.None);

        result.Should().Be("sitting on the couch");
    }

    [Fact]
    public async Task Select_EmptyRecentPool_ReturnsJustGenerated()
    {
        var selector = Build(recent: Array.Empty<MemoryRecord>());

        var result = await selector.SelectAsync(
            "sitting on the couch", State(), CancellationToken.None);

        result.Should().Be("sitting on the couch");
    }

    [Fact]
    public async Task Select_NullCharacterState_ReturnsJustGenerated()
    {
        var selector = Build(recent: new[] { Thought("thinking about Lila", 0.9f) });

        var result = await selector.SelectAsync(
            "sitting on the couch", null!, CancellationToken.None);

        result.Should().Be("sitting on the couch");
    }

    [Fact]
    public async Task Select_PoolHasHigherScoringAnchorBearingThought_UpgradesSeed()
    {
        // Just-generated: register-locked at Importance 0.9 → score 0.9
        // Pool entry: anchor-bearing at Importance 0.75 → score 0.75 × 1.3 = 0.975 (wins)
        var selector = Build(recent: new[]
        {
            Thought("thinking about Lila's weird robot emojis", 0.75f),
            Thought("sitting here on the couch, cold again", 0.9f),  // same as just-generated
        });

        var result = await selector.SelectAsync(
            "sitting here on the couch, cold again", State(), CancellationToken.None);

        result.Should().Be("thinking about Lila's weird robot emojis");
    }

    [Fact]
    public async Task Select_PoolHasNoAnchorBearingHigherScore_KeepsJustGenerated()
    {
        var selector = Build(recent: new[]
        {
            Thought("sitting here on the couch again", 0.5f),  // no anchor, mid importance
            Thought("rain tapping the window", 0.4f),           // no anchor, low importance
        });

        var result = await selector.SelectAsync(
            "cold corner of the bookstore", State(), CancellationToken.None);

        // Just-generated scores 0.5 (implied Importance), pool entries 0.5 and 0.4.
        // Tie goes to just-generated (bestScore starts there).
        result.Should().Be("cold corner of the bookstore");
    }

    [Fact]
    public async Task Select_TransportFailure_FailsOpenToJustGenerated()
    {
        var selector = Build(throwOnSearch: true);

        var result = await selector.SelectAsync(
            "sitting on the couch", State(), CancellationToken.None);

        result.Should().Be("sitting on the couch");
    }

    [Fact]
    public async Task Select_JustGeneratedAlsoInPool_UsesPersistedImportance()
    {
        // When the just-generated thought has already been persisted (racing
        // with the metadata-save), it'll appear in the recent pool with its
        // actual Importance (0.9). The selector should use that persisted
        // Importance (0.9 → score 0.9) rather than the fallback 0.5 default.
        //
        // A separate anchor-bearing pool entry at Importance 0.7 scores
        // 0.91 (= 0.7 × 1.3), which just barely beats 0.9. This verifies the
        // selector reads persisted Importance accurately rather than running
        // the fallback 0.5 (which would score the register-locked thought
        // at 0.5 and let the anchor-bearing thought win trivially).
        var selector = Build(recent: new[]
        {
            Thought("sitting here on the couch", 0.9f),            // just-generated, persisted
            Thought("Lila said something about her job", 0.7f),    // anchor-bearing, lower raw Importance
        });

        var result = await selector.SelectAsync(
            "sitting here on the couch", State(), CancellationToken.None);

        result.Should().Be("Lila said something about her job",
            "anchor-bearing at 0.7 × 1.3 = 0.91 narrowly beats register-locked at 0.9");
    }
}
