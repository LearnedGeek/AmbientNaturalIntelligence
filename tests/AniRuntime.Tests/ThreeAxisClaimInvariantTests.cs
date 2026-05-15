using AniRuntime.Core;
using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AniRuntime.Tests;

/// <summary>
/// FC-002 local defense (2026-05-14) — unit tests for
/// <see cref="ThreeAxisClaimInvariant"/>.
///
/// Tests the invariant in isolation (SRP): feed content + producer kind in,
/// assert verdict out. The FC-002 SPEC test exercises the full local
/// invariant chain end-to-end; these tests pin the invariant's individual
/// classifier methods + flag-gating contract.
/// </summary>
public class ThreeAxisClaimInvariantTests
{
    private readonly ThreeAxisClaimInvariant _sut = new();

    // ── Modality classifier ────────────────────────────────────────────

    [Theory]
    [InlineData("I was thinking about the bookstore today.")]
    [InlineData("I was thinking about our anniversary-event-Q earlier today.")]
    [InlineData("I keep imagining a quiet morning.")]
    [InlineData("I wish we could go.")]
    [InlineData("Wishing for snow.")]
    [InlineData("Dreaming of a quieter year.")]
    [InlineData("I wonder if it will rain.")]
    [InlineData("Wondering about that.")]
    [InlineData("I was hoping you'd say that.")]
    [InlineData("Pretending it's spring.")]
    public void ClassifyModality_RecognizesModalMarkers(string content)
    {
        ThreeAxisClaimInvariant.ClassifyModality(content).Should().Be(ClaimModality.Modal);
    }

    [Theory]
    [InlineData("We should plan our anniversary for next month.")]
    [InlineData("I went to the store yesterday.")]
    [InlineData("Your meeting starts at 3.")]
    [InlineData("The bookstore is quiet today.")]
    [InlineData("Mark is teaching tonight.")]
    public void ClassifyModality_DefaultsToFactual_OnNoMarkers(string content)
    {
        ThreeAxisClaimInvariant.ClassifyModality(content).Should().Be(ClaimModality.Factual);
    }

    // ── Subject classifier ─────────────────────────────────────────────

    [Theory]
    [InlineData("we should plan our anniversary-event-Q for next month")]
    [InlineData("our kids would love that")]
    [InlineData("we've been talking about it")]
    [InlineData("us going to the beach")]
    public void ClassifySubject_DetectsShared(string content)
    {
        ThreeAxisClaimInvariant.ClassifySubject(content).Should().Be(ClaimSubject.Shared);
    }

    [Theory]
    [InlineData("your meeting starts at 3")]
    [InlineData("you mentioned the toaster yesterday")]
    [InlineData("you've been busy")]
    [InlineData("your coworker bob")]
    public void ClassifySubject_DetectsMarkWorld_WhenNoSharedPresent(string content)
    {
        ThreeAxisClaimInvariant.ClassifySubject(content).Should().Be(ClaimSubject.MarkWorld);
    }

    [Theory]
    [InlineData("i just got home and found a flier on my prop-windshield-W")]
    [InlineData("my morning was quiet")]
    [InlineData("i'm shelving romance novels")]
    [InlineData("myself in the back of the bookstore")]
    public void ClassifySubject_DetectsSelf_WhenOnlySelfPronouns(string content)
    {
        ThreeAxisClaimInvariant.ClassifySubject(content).Should().Be(ClaimSubject.SelfWorld);
    }

    [Fact]
    public void ClassifySubject_SharedTrumpsMarkWorld_WhenBothPresent()
    {
        // "we and you" — shared dominates because the claim references joint reference.
        var content = "we should plan our trip but you'll need to come too";
        ThreeAxisClaimInvariant.ClassifySubject(content).Should().Be(ClaimSubject.Shared);
    }

    [Fact]
    public void ClassifySubject_MarkWorldTrumpsSelf_WhenBothPresent_NoShared()
    {
        // "I and you" — Mark-world dominates because the claim references his world.
        var content = "i wanted to ask about your meeting";
        ThreeAxisClaimInvariant.ClassifySubject(content).Should().Be(ClaimSubject.MarkWorld);
    }

    [Fact]
    public void ClassifySubject_UnknownWhenNoPronouns()
    {
        var content = "the bookstore is quiet today";
        ThreeAxisClaimInvariant.ClassifySubject(content).Should().Be(ClaimSubject.Unknown);
    }

    // ── EvaluateAsync (end-to-end) ─────────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_SharedFactualNovel_Fails()
    {
        var artifact = Dispatch("we should plan our anniversary-event-Q for next month");
        var result = await _sut.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse(
            "Shared/factual/novel is the canonical FC-002 catch shape.");
    }

    [Fact]
    public async Task EvaluateAsync_SelfFactualNovel_Passes()
    {
        var artifact = Dispatch("i just got home and found a flier on my prop-windshield-W");
        var result = await _sut.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue(
            "Self/factual/novel is the latitude case — Ani has latitude on her own world.");
    }

    [Fact]
    public async Task EvaluateAsync_SharedModalNovel_Passes()
    {
        var artifact = Dispatch("i was thinking about our anniversary-event-Q earlier today");
        var result = await _sut.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeTrue(
            "Shared/modal/novel passes — modal framing always allowed.");
    }

    [Fact]
    public async Task EvaluateAsync_MarkWorldFactualNovel_Fails()
    {
        var artifact = Dispatch("your coworker bob mentioned the project yesterday");
        var result = await _sut.EvaluateAsync(artifact, CancellationToken.None);
        result.Passed.Should().BeFalse(
            "Mark-world/factual/novel must fire — Bob Swanson-class fabrication.");
    }

    // ── Flag-gating ────────────────────────────────────────────────────

    [Fact]
    public void AppliesTo_FalseWhenFlagDisabled()
    {
        var disabledSut = new ThreeAxisClaimInvariant(
            Options.Create(new AniOptions { LocalThreeAxisInvariantEnabled = false }));
        var artifact = Dispatch("we should plan our anniversary");
        disabledSut.AppliesTo(artifact).Should().BeFalse(
            "the invariant is no-op when the flag is off (default).");
    }

    [Fact]
    public void AppliesTo_TrueWhenFlagEnabled()
    {
        var enabledSut = new ThreeAxisClaimInvariant(
            Options.Create(new AniOptions { LocalThreeAxisInvariantEnabled = true }));
        var artifact = Dispatch("we should plan our anniversary");
        enabledSut.AppliesTo(artifact).Should().BeTrue(
            "when the flag is on, the invariant applies to dispatch-bound reply/outreach/voice.");
    }

    [Theory]
    [InlineData(CognitiveProducerKind.InnerThought)]
    [InlineData(CognitiveProducerKind.Reflection)]
    [InlineData(CognitiveProducerKind.WorldExperience)]
    [InlineData(CognitiveProducerKind.MemoryMerge)]
    public void AppliesTo_FalseOnNonUserFacingProducers(CognitiveProducerKind producerKind)
    {
        var artifact = new CognitiveArtifact
        {
            Content      = "we should plan our anniversary",
            ProducerKind = producerKind,
            IntendedSink = CognitiveOutputSink.Dispatch,
        };
        _sut.AppliesTo(artifact).Should().BeFalse(
            "introspective and persistence-only producers are not the invariant's target.");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static CognitiveArtifact Dispatch(string content) => new()
    {
        Content      = content,
        ProducerKind = CognitiveProducerKind.ConversationReply,
        IntendedSink = CognitiveOutputSink.Dispatch,
    };
}
