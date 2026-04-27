using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Perception;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

public class RegisterSaturationPerceptionSourceTests
{
    private static readonly DateTimeOffset NOW = new(2026, 04, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeAnalytics : IMemoryAnalytics
    {
        public List<EmotionalContribution> Active { get; set; } = new();

        public Task<List<EmotionalContribution>> GetActiveContributionsAsync(CancellationToken ct = default)
            => Task.FromResult(Active);

        // Other interface members unused in these tests.
        public Task<IEnumerable<OpenLoop>> GetOpenLoopsAsync(CancellationToken ct = default)
            => Task.FromResult<IEnumerable<OpenLoop>>(Array.Empty<OpenLoop>());
        public Task<List<MemoryContradiction>> GetFlaggedContradictionsAsync(bool includeResolved = false, CancellationToken ct = default)
            => Task.FromResult(new List<MemoryContradiction>());
        public Task<int> GetRecentMessageCountAsync(int days, CancellationToken ct = default) => Task.FromResult(0);
        public Task<float> GetAverageConversationValenceAsync(int days, CancellationToken ct = default) => Task.FromResult(0f);
        public Task<(int outreach, int inbound)> GetInitiativeBalanceAsync(int days, CancellationToken ct = default) => Task.FromResult((0, 0));
        public Task<List<EmotionalContribution>> GetContributionsSinceAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(new List<EmotionalContribution>());
        public Task<List<string>> GetProcessedThemesAsync(int maxThemes = 5, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = NOW;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AniOptions OptionsWith(
        bool enabled = true,
        double threshold = 0.70,
        int minContributions = 4,
        double cooldownMinutes = 60.0)
        => new()
        {
            RegisterSaturationPerceptionEnabled = enabled,
            RegisterSaturationThreshold         = threshold,
            RegisterSaturationMinContributions  = minContributions,
            RegisterSaturationCooldownMinutes   = cooldownMinutes,
        };

    private static EmotionalContribution Contribution(string register)
        => new() { Register = register };

    private static RegisterSaturationPerceptionSource Build(
        FakeAnalytics analytics, AniOptions options, FakeTimeProvider time)
        => new(analytics, Options.Create(options), time,
               NullLogger<RegisterSaturationPerceptionSource>.Instance);

    [Fact]
    public async Task PollAsync_DoesNotEmit_WhenDisabled()
    {
        var analytics = new FakeAnalytics
        {
            Active = Enumerable.Repeat(Contribution("Tenderness"), 10).ToList(),
        };
        var src = Build(analytics, OptionsWith(enabled: false), new FakeTimeProvider());

        var events = await src.PollAsync(NOW);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_DoesNotEmit_WhenBelowMinContributions()
    {
        var analytics = new FakeAnalytics
        {
            Active = new[] { Contribution("Tenderness"), Contribution("Tenderness") }.ToList(),
        };
        var src = Build(analytics, OptionsWith(minContributions: 4), new FakeTimeProvider());

        var events = await src.PollAsync(NOW);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_DoesNotEmit_WhenBelowThreshold()
    {
        // 4 of 10 are Tenderness — share 0.40 — below default threshold 0.70.
        var contributions = new List<EmotionalContribution>();
        contributions.AddRange(Enumerable.Repeat(Contribution("Tenderness"), 4));
        contributions.AddRange(Enumerable.Repeat(Contribution("Longing"), 3));
        contributions.AddRange(Enumerable.Repeat(Contribution("Playfulness"), 3));
        var analytics = new FakeAnalytics { Active = contributions };
        var src = Build(analytics, OptionsWith(), new FakeTimeProvider());

        var events = await src.PollAsync(NOW);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_Emits_WhenDominantRegisterCrossesThreshold()
    {
        // 8 of 10 are Tenderness — share 0.80 — above threshold 0.70.
        var contributions = new List<EmotionalContribution>();
        contributions.AddRange(Enumerable.Repeat(Contribution("Tenderness"), 8));
        contributions.AddRange(Enumerable.Repeat(Contribution("Longing"), 2));
        var analytics = new FakeAnalytics { Active = contributions };
        var src = Build(analytics, OptionsWith(), new FakeTimeProvider());

        var events = (await src.PollAsync(NOW)).ToList();

        events.Should().HaveCount(1);
        events[0].SourceName.Should().Be("register-saturation");
        events[0].Category.Should().Be(PerceptionCategory.Internal);
        events[0].Summary.Should().Contain("soft");      // tenderness-specific text
        events[0].Summary.Should().Contain("80%");       // share rendering
    }

    [Fact]
    public async Task PollAsync_GenericTextWhenRegisterUnknownToTextDictionary()
    {
        // A register the BuildSummary switch doesn't have a custom phrasing
        // for falls through to the generic template. Test that the generic
        // template still includes the register name and the share.
        var contributions = Enumerable.Repeat(Contribution("Resilience"), 8).ToList();
        contributions.AddRange(Enumerable.Repeat(Contribution("Longing"), 2));
        var analytics = new FakeAnalytics { Active = contributions };
        var src = Build(analytics, OptionsWith(), new FakeTimeProvider());

        var events = (await src.PollAsync(NOW)).ToList();

        events.Should().HaveCount(1);
        events[0].Summary.Should().Contain("resilience");
        events[0].Summary.Should().Contain("80%");
    }

    [Fact]
    public async Task PollAsync_RespectsCooldown_DoesNotReEmitDuringWindow()
    {
        var contributions = Enumerable.Repeat(Contribution("Tenderness"), 10).ToList();
        var analytics = new FakeAnalytics { Active = contributions };
        var time = new FakeTimeProvider { Now = NOW };
        var src = Build(analytics, OptionsWith(cooldownMinutes: 60), time);

        // First poll — emits.
        (await src.PollAsync(NOW)).Should().HaveCount(1);

        // 30 minutes later, still saturated — should NOT emit (within cooldown).
        time.Now = NOW.AddMinutes(30);
        (await src.PollAsync(time.Now)).Should().BeEmpty();

        // 70 minutes later (past 60-min cooldown) — emits again.
        time.Now = NOW.AddMinutes(70);
        (await src.PollAsync(time.Now)).Should().HaveCount(1);
    }

    [Fact]
    public async Task PollAsync_PicksHighestRegister_WhenMultipleAboveThreshold()
    {
        // Edge case: shouldn't happen with threshold ≥ 0.50 but tested for
        // robustness. With threshold lowered to 0.40 and two registers each
        // at 0.50, the dominant (first by count, ties broken arbitrarily)
        // is what gets named.
        var contributions = new List<EmotionalContribution>();
        contributions.AddRange(Enumerable.Repeat(Contribution("Tenderness"), 5));
        contributions.AddRange(Enumerable.Repeat(Contribution("Longing"), 3));
        contributions.AddRange(Enumerable.Repeat(Contribution("Playfulness"), 2));
        var analytics = new FakeAnalytics { Active = contributions };
        var src = Build(analytics, OptionsWith(threshold: 0.40), new FakeTimeProvider());

        var events = (await src.PollAsync(NOW)).ToList();

        events.Should().HaveCount(1);
        // Tenderness has the highest count (5/10 = 50%), beats both others.
        // Tenderness has a custom phrasing that uses "soft"; the lowercase
        // register name also appears in the generic fallback. Either match works.
        var summary = events[0].Summary.ToLowerInvariant();
        (summary.Contains("soft") || summary.Contains("tenderness")).Should().BeTrue();
    }
}
