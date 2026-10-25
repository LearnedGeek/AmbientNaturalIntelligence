using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AniRuntime.Tests;

/// <summary>
/// F-4 Phase 5a — pins <see cref="TemporalContextProvider"/>'s shape and
/// fail-open contract. The provider is the writer half of Phase 5a;
/// it queries recent <see cref="MemoryType.InnerThought"/> records so the
/// reader (<c>InnerThoughtPromptCommand</c>) can inject them with concrete
/// timestamps. See issue #184.
/// </summary>
public class TemporalContextProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 18, 0, 0, TimeSpan.Zero);

    private static TemporalContextProvider BuildProvider(
        IEnumerable<MemoryRecord>? records = null,
        bool throwOnCall = false)
    {
        var mock = new Mock<IMemorySearch>();
        if (throwOnCall)
        {
            mock.Setup(s => s.GetByTypeAsync(
                    It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("simulated DB failure"));
        }
        else
        {
            mock.Setup(s => s.GetByTypeAsync(
                    It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(records ?? Array.Empty<MemoryRecord>());
        }
        return new TemporalContextProvider(mock.Object, NullLogger<TemporalContextProvider>.Instance);
    }

    private static MemoryRecord Thought(string content, DateTimeOffset occurredAt) => new()
    {
        Content    = content,
        Type       = MemoryType.InnerThought,
        OccurredAt = occurredAt,
        CreatedAt  = occurredAt,
    };

    [Fact]
    public async Task Build_NoPriorThoughts_ReturnsNullElapsedAndEmptyList()
    {
        var provider = BuildProvider(records: Array.Empty<MemoryRecord>());
        var ctx = await provider.BuildAsync(Now, takeK: 3, CancellationToken.None);

        ctx.HoursSinceLastInnerThought.Should().BeNull();
        ctx.RecentInnerThoughts.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_SinglePriorThought_ComputesElapsedHours()
    {
        var threeHoursAgo = Now.AddHours(-3);
        var provider = BuildProvider(records: new[]
        {
            Thought("I think about how light moves through glass.", threeHoursAgo),
        });

        var ctx = await provider.BuildAsync(Now, takeK: 3, CancellationToken.None);

        ctx.HoursSinceLastInnerThought.Should().BeApproximately(3.0, 0.01);
        ctx.RecentInnerThoughts.Should().ContainSingle();
        ctx.RecentInnerThoughts[0].Content.Should().StartWith("I think about");
    }

    [Fact]
    public async Task Build_ElapsedNeverNegative_EvenIfRecordAheadOfNow()
    {
        // Defensive: a drifted clock or seed data could put the record slightly
        // ahead of `now`. The provider clamps so the composer never sees a
        // negative-elapsed phrase like "-0.1 hours ago."
        var provider = BuildProvider(records: new[]
        {
            Thought("future thought", Now.AddMinutes(5)),
        });

        var ctx = await provider.BuildAsync(Now, takeK: 3, CancellationToken.None);

        ctx.HoursSinceLastInnerThought.Should().BeGreaterThanOrEqualTo(0.0);
    }

    [Fact]
    public async Task Build_FiltersEmptyContentRecords()
    {
        var provider = BuildProvider(records: new[]
        {
            Thought("real thought",             Now.AddHours(-1)),
            Thought("   ",                      Now.AddHours(-2)),
            Thought(string.Empty,               Now.AddHours(-3)),
            Thought("another real thought",     Now.AddHours(-4)),
        });

        var ctx = await provider.BuildAsync(Now, takeK: 5, CancellationToken.None);

        ctx.RecentInnerThoughts.Should().HaveCount(2);
        ctx.RecentInnerThoughts.Select(t => t.Content)
            .Should().BeEquivalentTo(new[] { "real thought", "another real thought" });
    }

    [Fact]
    public async Task Build_ZeroTakeK_ReturnsEmptyWithoutDbCall()
    {
        var provider = BuildProvider();
        var ctx = await provider.BuildAsync(Now, takeK: 0, CancellationToken.None);

        ctx.HoursSinceLastInnerThought.Should().BeNull();
        ctx.RecentInnerThoughts.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_SearchThrows_FailsOpenToEmpty()
    {
        // Fail-open per the interface contract: a DB stall must never block
        // the cognitive cycle. Composer sees null + empty and skips the block.
        var provider = BuildProvider(throwOnCall: true);
        var ctx = await provider.BuildAsync(Now, takeK: 3, CancellationToken.None);

        ctx.HoursSinceLastInnerThought.Should().BeNull();
        ctx.RecentInnerThoughts.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_CancellationFlowsThrough()
    {
        // OperationCanceledException must propagate — fail-open does not
        // swallow cancellation (CLAUDE.md §4). Caller's token is honored.
        using var cts = new CancellationTokenSource();
        var mock = new Mock<IMemorySearch>();
        mock.Setup(s => s.GetByTypeAsync(
                It.IsAny<MemoryType>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var provider = new TemporalContextProvider(mock.Object, NullLogger<TemporalContextProvider>.Instance);

        Func<Task> act = () => provider.BuildAsync(Now, takeK: 3, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
