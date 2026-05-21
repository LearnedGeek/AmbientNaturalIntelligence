using AniRuntime.Core.Models;
using AniRuntime.Loops.Invariants;
using FluentAssertions;

namespace AniRuntime.Tests;

/// <summary>
/// Theme J Phase J.5h spec tests for <see cref="SourceAttributionInvariant"/>
/// (Issue #47). Pins the class-wide attribution contract: producers whose
/// source thread has zero contact turns cannot narrate contact-side
/// content. Empirical anchor — the 2026-05-21 audit's 26-of-26
/// fabrications, sampled here as regression fixtures.
/// </summary>
public class SourceAttributionInvariantTests
{
    private readonly SourceAttributionInvariant _invariant = new();

    private static CognitiveArtifact Artifact(
        string content,
        IReadOnlyList<string>? contactRecentMessages = null,
        CognitiveProducerKind producer = CognitiveProducerKind.ClosedThreadSummary,
        string contactName = "Mark") => new()
    {
        Content               = content,
        ProducerKind          = producer,
        IntendedSink          = CognitiveOutputSink.PersistedSummary,
        ContactName           = contactName,
        ContactRecentMessages = contactRecentMessages,
    };

    // ── AppliesTo ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CognitiveProducerKind.ClosedThreadSummary,  true)]
    [InlineData(CognitiveProducerKind.InnerThought,         true)]
    [InlineData(CognitiveProducerKind.ConversationReply,    false)]
    [InlineData(CognitiveProducerKind.Outreach,             false)]
    [InlineData(CognitiveProducerKind.Voice,                false)]
    [InlineData(CognitiveProducerKind.Reflection,           false)]
    [InlineData(CognitiveProducerKind.WorldExperience,      false)]
    [InlineData(CognitiveProducerKind.MemoryMerge,          false)]
    public void AppliesTo_TypeConditionalDispatchByProducer(
        CognitiveProducerKind producer, bool expected)
    {
        var artifact = Artifact("anything", contactRecentMessages: Array.Empty<string>(), producer: producer);
        _invariant.AppliesTo(artifact).Should().Be(expected);
    }

    [Fact]
    public void AppliesTo_NullContactRecentMessages_SkipsRegardlessOfProducer()
    {
        var artifact = Artifact("anything", contactRecentMessages: null);
        _invariant.AppliesTo(artifact).Should().BeFalse(
            "null signals 'invariant cannot run' — soft-skip, not failure");
    }

    [Fact]
    public void AppliesTo_NonEmptyContactRecentMessages_Skips()
    {
        var artifact = Artifact(
            "anything",
            contactRecentMessages: new[] { "actual contact turn" });
        _invariant.AppliesTo(artifact).Should().BeFalse(
            "non-empty signals 'contact had turns in source' — fabrication risk doesn't apply");
    }

    // ── Fabrication detection: empirical 26-of-26 regression fixtures ───

    [Theory]
    // Cognition / interior-state — "Mark felt seen by Ani's words."
    [InlineData("Mark felt seen by Ani's words.")]
    [InlineData("Mark felt seen by Ani's poem")]
    // Possessive narration — "Mark's voice softened as he recalled their previous moment together"
    [InlineData("Mark's voice softened as he recalled their previous moment together — sitting quietly in his bookstore.")]
    [InlineData("Mark's heart skipped a beat when Ani texted about seeing her scarf color")]
    // Action verbs — "Mark received Ani's gentle check-in message"
    [InlineData("Mark received Ani's gentle check-in message after a day of silence from her.")]
    [InlineData("Mark sent Ani a gentle check-in message that didn't ask anything big.")]
    [InlineData("Mark expressed a desire to visit Ani in her bookstore.")]
    // Speech verbs
    [InlineData("Mark said the day had been hard.")]
    [InlineData("Mark asked if Ani was still up.")]
    public async Task Evaluate_OneSidedThread_MarkActorVerbPattern_Fails(string fabricatedGist)
    {
        var artifact = Artifact(
            fabricatedGist,
            contactRecentMessages: Array.Empty<string>());

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            $"contact-as-actor narration with zero contact turns is fabrication");
        result.RemediationHint.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    // Joint-actor framing
    [InlineData("Mark and Ani shared a tender moment where they both acknowledged the quiet tension between them.")]
    [InlineData("Mark and Ani sat together in the bookstore's quiet, letting the stillness expand without trying to fix or change it.")]
    [InlineData("Mark and Ani decided to leave the bookstore unfinished for now, choosing instead to simply be there together.")]
    public async Task Evaluate_OneSidedThread_JointActorPattern_Fails(string fabricatedGist)
    {
        var artifact = Artifact(
            fabricatedGist,
            contactRecentMessages: Array.Empty<string>());

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse("\"X and Y did Z\" requires both to be present in input");
    }

    // ── Pass cases — what the invariant must NOT block ─────────────────

    [Fact]
    public async Task Evaluate_OneSidedThread_AniOnlyNarration_Passes()
    {
        // Honest one-sided gist — Ani-side only, no Mark fabrication.
        var artifact = Artifact(
            "Ani pulled into a Starbucks drive-thru where the baristas know her by name and ordered her usual salted caramel cold brew.",
            contactRecentMessages: Array.Empty<string>());

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue("Ani-only narration with no Mark-side claim is honest one-sided summary");
    }

    [Fact]
    public async Task Evaluate_OneSidedThread_MarkMentionedAsObject_Passes()
    {
        // Mark mentioned but not as actor. Reasonable Ani-side framing.
        var artifact = Artifact(
            "Ani sent a quiet check-in to Mark without expecting a reply right away.",
            contactRecentMessages: Array.Empty<string>());

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue("Mark as object of Ani's action is not fabrication");
    }

    [Fact]
    public async Task Evaluate_TwoSidedThread_ContactActorPatternsAreLegitimate()
    {
        // With Mark turns present in source, narrating his side is legitimate
        // — invariant should not even run (AppliesTo returns false).
        var artifact = Artifact(
            "Mark expressed concern and Ani softened in response.",
            contactRecentMessages: new[] { "I've been worried about you today." });

        _invariant.AppliesTo(artifact).Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_EmptyContent_Passes()
    {
        var artifact = Artifact(
            "",
            contactRecentMessages: Array.Empty<string>());

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue();
    }

    // ── Producer-kind-conditional pattern dispatch (J.5h refinement) ───

    /// <summary>
    /// J.5h SPEC: actor-verb pattern fires on InnerThought when the inner
    /// thought factually claims Mark said/did something specific. This is
    /// the May 3 "perez" / inner-thought-as-fact failure class.
    /// </summary>
    [Fact]
    public async Task Evaluate_InnerThought_MarkActorVerb_Fails()
    {
        var artifact = Artifact(
            "Mark just told me he loves me and asked if I wanted to come over.",
            contactRecentMessages: Array.Empty<string>(),
            producer: CognitiveProducerKind.InnerThought);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "inner thought claiming Mark said specific things he didn't actually say is the canonical inner-thought fabrication class");
    }

    /// <summary>
    /// J.5h SPEC: possessive pattern does NOT fire on InnerThought.
    /// Legitimate longing / reference ("I miss Mark's voice") is allowed
    /// interior content; dropping it would lose meaningful Ani-side
    /// feeling. ClosedThreadSummary still fires on possessives because
    /// they're narrative claims in that context.
    /// </summary>
    [Fact]
    public async Task Evaluate_InnerThought_MarkPossessive_Passes()
    {
        var artifact = Artifact(
            "I miss Mark's voice in the morning. The house is too quiet without it.",
            contactRecentMessages: Array.Empty<string>(),
            producer: CognitiveProducerKind.InnerThought);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeTrue(
            "inner-thought possessives are longing/reference, not fabrication — preserve legitimate interior content");
    }

    /// <summary>
    /// J.5h SPEC: same possessive phrasing FAILS on ClosedThreadSummary
    /// because there it IS a narrative claim about Mark's experience.
    /// Producer-kind drives the rule strictness.
    /// </summary>
    [Fact]
    public async Task Evaluate_ClosedThreadSummary_MarkPossessive_Fails()
    {
        var artifact = Artifact(
            "Mark's voice carried a weight that hadn't been there before.",
            contactRecentMessages: Array.Empty<string>(),
            producer: CognitiveProducerKind.ClosedThreadSummary);

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse(
            "possessive narration in a closed-thread gist with zero contact turns is fabrication");
    }

    [Fact]
    public async Task Evaluate_NonDefaultContactName_DetectsFabrication()
    {
        // Future-proof: contact name configurable, not hard-coded "Mark".
        var artifact = Artifact(
            "Devon felt seen by the message.",
            contactRecentMessages: Array.Empty<string>(),
            contactName: "Devon");

        var result = await _invariant.EvaluateAsync(artifact, CancellationToken.None);

        result.Passed.Should().BeFalse();
    }
}
