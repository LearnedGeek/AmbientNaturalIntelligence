using AniRuntime.Core;
using AniRuntime.Core.Interfaces;
using AniRuntime.Core.Models;
using AniRuntime.Loops;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AniRuntime.Tests;

/// <summary>
/// Feature 44 (Interoceptive Axis, Phase I.1, 2026-08-03) — spec tests for
/// <see cref="InteroceptiveStateService"/>. Pins:
///
/// <list type="number">
///   <item>When <see cref="AniOptions.InteroceptiveAxisEnabled"/> is false,
///     <see cref="InteroceptiveStateService.Update"/> is a no-op — state
///     fields retain their previous values / defaults.</item>
///   <item>Each driver (Tiredness, Restlessness, Groundedness,
///     AmbientBodySense) produces expected values under mocked exogenous
///     inputs. Drivers are pure functions of the context — no I/O, no
///     hidden state.</item>
///   <item>Circadian and weather modulation fire on the documented hour +
///     temperature thresholds.</item>
///   <item>Values clamp cleanly to [0, 1] under extreme inputs.</item>
///   <item><see cref="EmotionalState.LastInteroceptiveUpdate"/> advances
///     on every successful update and is preserved through JSON round-trip.</item>
/// </list>
/// </summary>
public class InteroceptiveStateServiceTests
{
    private static InteroceptiveStateService Build(bool enabled = true, Action<AniOptions>? configure = null)
    {
        var opts = new AniOptions { InteroceptiveAxisEnabled = enabled };
        configure?.Invoke(opts);
        return new InteroceptiveStateService(
            Options.Create(opts),
            NullLogger<InteroceptiveStateService>.Instance);
    }

    private static InteroceptiveDriverContext Ctx(
        DateTimeOffset? now = null,
        double hoursSinceOutreach = 0.0,
        double hoursSinceContact = 0.0,
        int recentEvents = 0,
        double? tempF = null)
        => new(
            Now:                          now ?? new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero),
            HoursSinceLastOutreach:       hoursSinceOutreach,
            HoursSinceLastInboundContact: hoursSinceContact,
            RecentPerceptionEventCount:   recentEvents,
            CurrentTemperatureFahrenheit: tempF);

    // ── Flag-off no-op ─────────────────────────────────────────────────────

    [Fact]
    public void Update_FlagDisabled_LeavesStateUntouched()
    {
        var service = Build(enabled: false);
        var state = new EmotionalState
        {
            Tiredness = 0.7f, Restlessness = 0.6f, Groundedness = 0.4f, AmbientBodySense = 0.3f,
            LastInteroceptiveUpdate = DateTimeOffset.MinValue,
        };
        var context = Ctx(hoursSinceOutreach: 100.0, hoursSinceContact: 100.0, recentEvents: 20, tempF: 110.0);

        service.Update(state, context);

        state.Tiredness.Should().Be(0.7f);
        state.Restlessness.Should().Be(0.6f);
        state.Groundedness.Should().Be(0.4f);
        state.AmbientBodySense.Should().Be(0.3f);
        state.LastInteroceptiveUpdate.Should().Be(DateTimeOffset.MinValue);
    }

    // ── Tiredness driver ───────────────────────────────────────────────────

    [Fact]
    public void ComputeTiredness_AtZeroHoursQuiet_ReturnsNearZero()
    {
        var service = Build();
        var t = service.ComputeTiredness(Ctx(hoursSinceOutreach: 0.0));
        t.Should().BeApproximately(0.0f, precision: 0.01f);
    }

    [Fact]
    public void ComputeTiredness_AtHalfLifeQuiet_ReturnsHalfSaturation()
    {
        var service = Build(configure: o => o.InteroceptiveTirednessCycleHalfLife = 6.0);
        // 1 - exp(-6/6) ≈ 1 - 0.368 ≈ 0.632
        var t = service.ComputeTiredness(Ctx(hoursSinceOutreach: 6.0));
        t.Should().BeApproximately(0.632f, precision: 0.02f);
    }

    [Fact]
    public void ComputeTiredness_LateNightBoostFires_ForHoursBetween23And6()
    {
        var service = Build();
        var late = new DateTimeOffset(2026, 8, 3, 3, 0, 0, TimeSpan.Zero); // 3 AM
        var early = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero); // 2 PM

        var lateT = service.ComputeTiredness(Ctx(now: late, hoursSinceOutreach: 2.0));
        var earlyT = service.ComputeTiredness(Ctx(now: early, hoursSinceOutreach: 2.0));

        lateT.Should().BeGreaterThan(earlyT);
        (lateT - earlyT).Should().BeApproximately(0.35f, precision: 0.02f); // matches default night boost
    }

    [Fact]
    public void ComputeTiredness_ClampsAtOne_ForVeryLongQuietPlusNightBoost()
    {
        var service = Build();
        var late = new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero);
        var t = service.ComputeTiredness(Ctx(now: late, hoursSinceOutreach: 100.0));
        t.Should().BeLessThanOrEqualTo(1.0f);
        t.Should().BeGreaterThan(0.95f);
    }

    // ── Restlessness driver ────────────────────────────────────────────────

    [Fact]
    public void ComputeRestlessness_BelowOnset_ReturnsZero()
    {
        var service = Build(configure: o => o.InteroceptiveRestlessnessQuietOnsetHours = 4.0);
        var r = service.ComputeRestlessness(Ctx(hoursSinceOutreach: 2.0, hoursSinceContact: 2.0));
        r.Should().Be(0.0f);
    }

    [Fact]
    public void ComputeRestlessness_PastOnset_AccumulatesLinearly()
    {
        var service = Build(configure: o =>
        {
            o.InteroceptiveRestlessnessQuietOnsetHours = 4.0;
            o.InteroceptiveRestlessnessRatePerHour = 0.08;
        });
        // 8h since outreach = 4h past onset × 0.08 = 0.32; contact contribution 0.
        var r = service.ComputeRestlessness(Ctx(hoursSinceOutreach: 8.0, hoursSinceContact: 0.0));
        r.Should().BeApproximately(0.32f, precision: 0.01f);
    }

    [Fact]
    public void ComputeRestlessness_InterlocutorGapContributesHalfWeight()
    {
        var service = Build(configure: o =>
        {
            o.InteroceptiveRestlessnessQuietOnsetHours = 4.0;
            o.InteroceptiveRestlessnessRatePerHour = 0.08;
        });
        // 4h since outreach (0 contribution), 8h since contact (4 past onset × 0.08 × 0.5 = 0.16)
        var r = service.ComputeRestlessness(Ctx(hoursSinceOutreach: 4.0, hoursSinceContact: 8.0));
        r.Should().BeApproximately(0.16f, precision: 0.01f);
    }

    [Fact]
    public void ComputeRestlessness_ClampsAtCap()
    {
        var service = Build(configure: o => o.InteroceptiveRestlessnessMax = 0.85);
        var r = service.ComputeRestlessness(Ctx(hoursSinceOutreach: 100.0, hoursSinceContact: 100.0));
        r.Should().BeLessThanOrEqualTo(0.85f);
        r.Should().BeGreaterThan(0.80f); // hits the cap
    }

    // ── Groundedness driver ────────────────────────────────────────────────

    [Fact]
    public void ComputeGroundedness_ZeroEvents_ReturnsBaseline()
    {
        var service = Build(configure: o => o.InteroceptiveGroundednessBaseline = 0.55);
        var g = service.ComputeGroundedness(Ctx(recentEvents: 0));
        g.Should().BeApproximately(0.55f, precision: 0.001f);
    }

    [Fact]
    public void ComputeGroundedness_ManyEvents_DecreasesFromBaseline()
    {
        var service = Build(configure: o =>
        {
            o.InteroceptiveGroundednessBaseline = 0.55;
            o.InteroceptiveGroundednessChangePenalty = 0.06;
        });
        var g = service.ComputeGroundedness(Ctx(recentEvents: 5));
        // 0.55 - 5 * 0.06 = 0.25
        g.Should().BeApproximately(0.25f, precision: 0.01f);
    }

    [Fact]
    public void ComputeGroundedness_ClampsAtZero_ForExtremeEventFlood()
    {
        var service = Build();
        var g = service.ComputeGroundedness(Ctx(recentEvents: 100));
        g.Should().Be(0.0f);
    }

    // ── AmbientBodySense driver ────────────────────────────────────────────

    [Fact]
    public void ComputeAmbientBodySense_MildDayNoWeather_ReturnsZero()
    {
        var service = Build();
        var mid = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);
        var a = service.ComputeAmbientBodySense(Ctx(now: mid, tempF: null));
        a.Should().Be(0.0f);
    }

    [Fact]
    public void ComputeAmbientBodySense_HotWeatherAddsPenalty()
    {
        var service = Build(configure: o =>
        {
            o.InteroceptiveAmbientHotThresholdF = 82.0;
            o.InteroceptiveAmbientWeatherPenalty = 0.25;
        });
        var mid = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);
        var a = service.ComputeAmbientBodySense(Ctx(now: mid, tempF: 92.0));
        a.Should().BeApproximately(0.25f, precision: 0.01f);
    }

    [Fact]
    public void ComputeAmbientBodySense_ColdWeatherAlsoAddsPenalty()
    {
        var service = Build(configure: o =>
        {
            o.InteroceptiveAmbientColdThresholdF = 38.0;
            o.InteroceptiveAmbientWeatherPenalty = 0.25;
        });
        var mid = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);
        var a = service.ComputeAmbientBodySense(Ctx(now: mid, tempF: 20.0));
        a.Should().BeApproximately(0.25f, precision: 0.01f);
    }

    [Fact]
    public void ComputeAmbientBodySense_LateNightAddsCircadianBoost()
    {
        var service = Build(configure: o => o.InteroceptiveAmbientLateNightBoost = 0.20);
        var late = new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero);
        var a = service.ComputeAmbientBodySense(Ctx(now: late, tempF: 70.0));
        a.Should().BeApproximately(0.20f, precision: 0.01f);
    }

    [Fact]
    public void ComputeAmbientBodySense_HotLateNight_StacksBothContributions()
    {
        var service = Build(configure: o =>
        {
            o.InteroceptiveAmbientHotThresholdF = 82.0;
            o.InteroceptiveAmbientWeatherPenalty = 0.25;
            o.InteroceptiveAmbientLateNightBoost = 0.20;
        });
        var late = new DateTimeOffset(2026, 8, 3, 2, 0, 0, TimeSpan.Zero);
        var a = service.ComputeAmbientBodySense(Ctx(now: late, tempF: 92.0));
        a.Should().BeApproximately(0.45f, precision: 0.01f);
    }

    // ── Full update semantics ──────────────────────────────────────────────

    [Fact]
    public void Update_AdvancesLastInteroceptiveUpdateTimestamp()
    {
        var service = Build();
        var state = new EmotionalState { LastInteroceptiveUpdate = DateTimeOffset.MinValue };
        var now = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);

        service.Update(state, Ctx(now: now));

        state.LastInteroceptiveUpdate.Should().Be(now);
    }

    [Fact]
    public void Update_WritesAllFourAxes_InSameCall()
    {
        var service = Build();
        var state = new EmotionalState();
        var ctx = Ctx(hoursSinceOutreach: 12.0, hoursSinceContact: 10.0, recentEvents: 2, tempF: 92.0);

        service.Update(state, ctx);

        state.Tiredness.Should().BeGreaterThan(0.0f);
        state.Restlessness.Should().BeGreaterThan(0.0f);
        // Groundedness = 0.55 - 2 * 0.06 = 0.43
        state.Groundedness.Should().BeApproximately(0.43f, precision: 0.02f);
        state.AmbientBodySense.Should().BeApproximately(0.25f, precision: 0.02f); // hot weather at mid-day
    }

    // ── JSON round-trip ────────────────────────────────────────────────────

    [Fact]
    public void EmotionalState_JsonRoundTrip_PreservesInteroceptiveFields()
    {
        var state = new EmotionalState
        {
            Warmth = 0.7f, Energy = 0.6f, Worry = 0.3f, Playfulness = 0.5f,
            Tiredness = 0.42f, Restlessness = 0.31f,
            Groundedness = 0.65f, AmbientBodySense = 0.18f,
            LastInteroceptiveUpdate = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<EmotionalState>(json)!;

        roundTripped.Tiredness.Should().Be(0.42f);
        roundTripped.Restlessness.Should().Be(0.31f);
        roundTripped.Groundedness.Should().Be(0.65f);
        roundTripped.AmbientBodySense.Should().Be(0.18f);
        roundTripped.LastInteroceptiveUpdate.Should()
            .Be(new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void EmotionalState_DeserializePreExistingJson_ContainsInteroceptiveDefaults()
    {
        // Pre-Feature-44 JSON blob (no interoceptive fields present at all).
        var oldJson = "{\"Warmth\":0.6,\"Energy\":0.5,\"Concern\":0.2,\"Playfulness\":0.5,\"LastUpdated\":\"2026-07-01T00:00:00+00:00\"}";
        var state = System.Text.Json.JsonSerializer.Deserialize<EmotionalState>(oldJson)!;

        state.Tiredness.Should().Be(0.2f);        // model default
        state.Restlessness.Should().Be(0.2f);
        state.Groundedness.Should().Be(0.5f);
        state.AmbientBodySense.Should().Be(0.3f);
        state.LastInteroceptiveUpdate.Should().Be(DateTimeOffset.MinValue); // sentinel
    }

    // ── Argument validation ────────────────────────────────────────────────

    [Fact]
    public void Update_NullState_Throws()
    {
        var service = Build();
        Assert.Throws<ArgumentNullException>(() =>
            service.Update(null!, Ctx()));
    }

    [Fact]
    public void Update_NullContext_Throws()
    {
        var service = Build();
        Assert.Throws<ArgumentNullException>(() =>
            service.Update(new EmotionalState(), null!));
    }
}
