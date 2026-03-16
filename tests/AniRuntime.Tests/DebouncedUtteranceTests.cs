using AniRuntime.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniRuntime.Tests;

public class DebouncedUtteranceTests
{
    private static DebouncedUtterance Create(int debounceMs = 100) =>
        new(debounceMs, NullLogger.Instance);

    [Fact]
    public void AddSegment_SingleSegment_FiresAfterDebounce()
    {
        var debounce = Create(debounceMs: 50);
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.AddSegment("hello world");

        // Wait for debounce to fire
        Thread.Sleep(150);
        received.Should().Be("hello world");
    }

    [Fact]
    public void AddSegment_MultipleSegments_JoinsWithSpace()
    {
        var debounce = Create(debounceMs: 50);
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.AddSegment("hello");
        debounce.AddSegment("world");

        Thread.Sleep(150);
        received.Should().Be("hello world");
    }

    [Fact]
    public void AddSegment_ResetsTimer_OnNewSegment()
    {
        var debounce = Create(debounceMs: 100);
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.AddSegment("first");
        Thread.Sleep(60); // less than debounce
        debounce.AddSegment("second");
        Thread.Sleep(60); // less than debounce from second
        received.Should().BeNull("debounce should not have fired yet");

        Thread.Sleep(100); // now it should fire
        received.Should().Be("first second");
    }

    [Fact]
    public void Clear_DiscardsAccumulatedSegments()
    {
        var debounce = Create(debounceMs: 50);
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.AddSegment("echo artifact");
        debounce.Clear();

        Thread.Sleep(150);
        received.Should().BeNull("cleared segments should not fire");
    }

    [Fact]
    public void Clear_CancelsActiveTimer()
    {
        var debounce = Create(debounceMs: 50);
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.AddSegment("will be cleared");
        debounce.Clear();

        // Add new segment — should fire independently
        debounce.AddSegment("real speech");
        Thread.Sleep(150);

        received.Should().Be("real speech");
    }

    [Fact]
    public void Flush_EmitsImmediately()
    {
        var debounce = Create(debounceMs: 5000); // very long debounce
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.AddSegment("flush me");
        debounce.Flush();

        received.Should().Be("flush me");
    }

    [Fact]
    public void Flush_EmptySegments_DoesNotFire()
    {
        var debounce = Create();
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.Flush();

        received.Should().BeNull();
    }

    [Fact]
    public void PendingCount_ReflectsAccumulatedSegments()
    {
        var debounce = Create(debounceMs: 5000);

        debounce.PendingCount.Should().Be(0);
        debounce.AddSegment("one");
        debounce.PendingCount.Should().Be(1);
        debounce.AddSegment("two");
        debounce.PendingCount.Should().Be(2);
        debounce.Clear();
        debounce.PendingCount.Should().Be(0);
    }

    [Fact]
    public void AddSegment_IgnoresWhitespace()
    {
        var debounce = Create(debounceMs: 50);
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.AddSegment("");
        debounce.AddSegment("  ");
        debounce.AddSegment(null!);

        Thread.Sleep(150);
        received.Should().BeNull();
        debounce.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentAddAndClear_DoesNotThrow()
    {
        var debounce = Create(debounceMs: 10);
        int fireCount = 0;
        debounce.UtteranceReady += _ => Interlocked.Increment(ref fireCount);

        // Simulate concurrent access from STT receive thread and orchestrator
        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(() =>
        {
            if (i % 3 == 0)
                debounce.AddSegment($"segment-{i}");
            else if (i % 3 == 1)
                debounce.Clear();
            else
                _ = debounce.PendingCount;
        }));

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_PreventsSubsequentFiring()
    {
        var debounce = Create(debounceMs: 50);
        string? received = null;
        debounce.UtteranceReady += u => received = u;

        debounce.AddSegment("will dispose");
        debounce.Dispose();

        Thread.Sleep(150);
        received.Should().BeNull("dispose should cancel pending timer");
    }
}
